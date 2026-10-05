using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SimpleTrans;
using SimpleTrans.Client;
using SimpleTrans.Server;
using SimpleTrans.Sources;

namespace SimpleTrans.Demo
{
    /// <summary>
    /// 端到端联调示例：一个服务端 + 三个客户端（两个全量、一个断点续传）。
    /// 直接 <c>dotnet run</c> 即可，全部使用回环地址与系统分配端口。
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "simpletrans-demo-" + Guid.NewGuid().ToString("N"));
            string srcDir = Path.Combine(root, "src");
            Directory.CreateDirectory(srcDir);

            Console.WriteLine("临时目录: " + root);

            try
            {
                // ---------- 准备源文件 ----------
                var files = new List<IFileSource>
                {
                    CreateRandomFile(srcDir, "payload.bin", 3 * 1024 * 1024),
                    CreateTextFile(srcDir, "readme.txt", "SimpleTrans 批量分发联调样例。\n"),
                    CreateRandomFile(srcDir, "small.bin", 1024),
                };

                var manifestFiles = new List<FileManifestEntry>();
                foreach (IFileSource source in files)
                {
                    manifestFiles.Add(new FileManifestEntry(
                        FileIdentity.ComputeFileId(source), source.Name, source.Length, source.KnownSha256));
                }

                // ---------- 启动服务端 ----------
                await using var server = new FileDistributionServer(new ServerOptions
                {
                    BindAddress = "127.0.0.1",
                    Port = 0,
                    ChunkSize = 128 * 1024,
                });

                int connected = 0;
                server.ClientConnected += (_, e) =>
                {
                    Interlocked.Increment(ref connected);
                    Console.WriteLine("  [server] 客户端接入: " + e.RemoteEndPoint);
                };
                server.ClientCompleted += (_, e) =>
                    Console.WriteLine($"  [server] 会话完成: 文件 {e.FileCount}，失败 {e.FailedCount}");
                server.ClientFailed += (_, e) =>
                    Console.WriteLine("  [server] 会话异常: " + e.Error.Message);

                await server.StartAsync();
                await server.PublishAsync(files);
                Console.WriteLine("服务端监听 127.0.0.1:" + server.Port);

                // ---------- 客户端 1 / 2：全量接收 ----------
                Task<ReceiveResult> fullA = ReceiveAllAsync("clientA", server.Port, manifestFiles, root, resumeState: null);
                Task<ReceiveResult> fullB = ReceiveAllAsync("clientB", server.Port, manifestFiles, root, resumeState: null);
                ReceiveResult[] results = await Task.WhenAll(fullA, fullB);

                foreach (ReceiveResult result in results)
                {
                    Check(result.Succeeded, "全量接收应全部成功");
                    Check(result.CompletedCount == manifestFiles.Count,
                        $"应完成 {manifestFiles.Count} 个文件，实际 {result.CompletedCount}");
                }

                // ---------- 客户端 3：断点续传 ----------
                string resumeDir = Path.Combine(root, "resume");
                Directory.CreateDirectory(resumeDir);

                // 预置：payload.bin 已收到 1MB，其余文件没有记录
                var state = new MemoryStateStore();
                FileManifestEntry first = manifestFiles[0];
                const long resumeOffset = 1024 * 1024;
                state.Seed(first.FileId, resumeOffset);
                PrewritePartial(resumeDir, first, files[0], resumeOffset);

                Console.WriteLine("断点续传客户端：payload.bin 从 " + resumeOffset + " 字节继续");
                ReceiveResult resumed = await ReceiveAllAsync("clientC", server.Port, manifestFiles, root, state, resumeDir);

                Check(resumed.Succeeded, "续传应全部成功");
                Check(resumed.TransferredBytes == TotalLength(manifestFiles) - resumeOffset,
                    $"续传字节数应等于剩余量，实际 {resumed.TransferredBytes}");

                // ---------- 客户端 4：真实中断后重连续传 ----------
                string interruptDir = Path.Combine(root, "interrupt");
                Directory.CreateDirectory(interruptDir);
                var liveState = new MemoryStateStore();
                long firstLen = manifestFiles[0].Length;
                long interruptedAt;

                using (var cts = new CancellationTokenSource())
                using (var client = new DistributionClient(new ClientOptions { ReceiveStateStore = liveState }))
                {
                    await client.ConnectAsync("127.0.0.1", server.Port);
                    await client.GetManifestAsync();

                    var inner = new DirectorySink(interruptDir);
                    var cancelling = new CancellingSink(inner, cts, 512 * 1024);
                    try
                    {
                        await client.ReceiveAsync(cancelling, new ReceiveOptions { Resume = true }, cts.Token);
                        throw new InvalidOperationException("预期传输被中断，但实际正常完成了");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                interruptedAt = await liveState.GetOffsetAsync(manifestFiles[0].FileId, CancellationToken.None);
                Check(interruptedAt > 0 && interruptedAt < firstLen,
                    "中断后应记录到部分进度，实际 " + interruptedAt.ToString());
                Console.WriteLine("中断发生于 " + interruptedAt + " 字节，重新连接继续");

                await using (var client = new DistributionClient(new ClientOptions { ReceiveStateStore = liveState }))
                {
                    await client.ConnectAsync("127.0.0.1", server.Port);
                    ReceiveResult after = await client.ReceiveAsync(
                        new DirectorySink(interruptDir), new ReceiveOptions { Resume = true });
                    Check(after.Succeeded, "重连后应全部成功");
                    Check(after.TransferredBytes == TotalLength(manifestFiles) - interruptedAt,
                        $"重连后只需续传剩余 {TotalLength(manifestFiles) - interruptedAt} 字节，实际 {after.TransferredBytes}");
                    Console.WriteLine("重连后补传 " + after.TransferredBytes + " 字节");
                }

                // ---------- 校验落盘内容 ----------
                ForEachTarget(root, manifestFiles, files, (name, path, source) =>
                {
                    byte[] expected = ReadSource(source);
                    byte[] actual = File.ReadAllBytes(path);
                    Check(expected.Length == actual.Length, name + " 长度应为 " + expected.Length.ToString());
                    Check(SameContent(expected, actual), name + " 内容应与源文件一致");
                });

                string interruptPath = Path.Combine(interruptDir, manifestFiles[0].Name);
                Check(SameContent(ReadSource(files[0]), File.ReadAllBytes(interruptPath)),
                    "中断重连后的 payload.bin 内容应与源文件一致");

                Console.WriteLine();
                Console.WriteLine("全部联调用例通过。");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("联调失败: " + ex);
                return 1;
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        private static async Task<ReceiveResult> ReceiveAllAsync(
            string label,
            int port,
            IReadOnlyList<FileManifestEntry> manifestFiles,
            string root,
            IReceiveStateStore? resumeState,
            string? outputDir = null)
        {
            outputDir ??= Path.Combine(root, label);
            Directory.CreateDirectory(outputDir);

            var options = new ClientOptions
            {
                RequestedChunkSize = 64 * 1024,
                ReceiveStateStore = resumeState,
            };

            using var client = new DistributionClient(options);
            await client.ConnectAsync("127.0.0.1", port);
            Manifest manifest = await client.GetManifestAsync();
            Check(manifest.Files.Count == manifestFiles.Count, label + " 清单文件数不一致");

            var sink = new DirectorySink(outputDir);
            ReceiveResult result = await client.ReceiveAsync(sink, new ReceiveOptions { Resume = resumeState != null });
            Console.WriteLine($"{label}: 完成 {result.CompletedCount}，失败 {result.FailedCount}，传输 {result.TransferredBytes} 字节");
            return result;
        }

        private static void ForEachTarget(
            string root,
            IReadOnlyList<FileManifestEntry> entries,
            IReadOnlyList<IFileSource> sources,
            Action<string, string, IFileSource> action)
        {
            string[] dirs = { "clientA", "clientB", "resume" };
            foreach (string dir in dirs)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    string path = Path.Combine(root, dir, entries[i].Name);
                    Check(File.Exists(path), "缺少文件: " + path);
                    action(entries[i].Name, path, sources[i]);
                }
            }
        }

        private static long TotalLength(IReadOnlyList<FileManifestEntry> entries)
        {
            long sum = 0;
            foreach (FileManifestEntry entry in entries) sum += entry.Length;
            return sum;
        }

        private static byte[] ReadSource(IFileSource source)
        {
            var buffer = new byte[source.Length];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = source.ReadAsync(total, buffer.AsMemory(total), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if (read <= 0) break;
                total += read;
            }
            return buffer;
        }

        private static void PrewritePartial(string dir, FileManifestEntry entry, IFileSource source, long length)
        {
            byte[] head = new byte[length];
            int total = 0;
            while (total < head.Length)
            {
                int read = source.ReadAsync(total, head.AsMemory(total), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if (read <= 0) break;
                total += read;
            }
            File.WriteAllBytes(Path.Combine(dir, entry.Name), head);
        }

        private static IFileSource CreateRandomFile(string dir, string name, int length)
        {
            byte[] data = new byte[length];
            new Random(length).NextBytes(data);
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, data);
            return new FilePathSource(path, name, Sha256(data));
        }

        private static IFileSource CreateTextFile(string dir, string name, string text)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, data);
            return new FilePathSource(path, name, Sha256(data));
        }

        private static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data);
        }

        private static bool SameContent(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("断言失败: " + message);
        }
    }
}