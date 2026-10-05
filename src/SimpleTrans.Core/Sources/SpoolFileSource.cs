#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans.Sources
{
    /// <summary>
    /// 不可 Seek 的源（网络流、压缩流、加密流）转文件源：创建时先把内容落到临时文件，
    /// 之后按 <see cref="FilePathSource"/> 的位置读对外服务。
    /// <para>如果源已知可 Seek，请直接用 <see cref="StreamFileSource"/>，避免多一次磁盘落盘。</para>
    /// </summary>
    public sealed class SpoolFileSource : IFileSource, IDisposable
    {
        private readonly FilePathSource _inner;
        private readonly string _tempPath;

        private SpoolFileSource(FilePathSource inner, string tempPath, string name, byte[]? knownSha256)
        {
            _inner = inner;
            _tempPath = tempPath;
            Name = name;
            KnownSha256 = knownSha256;
        }

        public string Name { get; }

        public long Length => _inner.Length;

        public byte[]? KnownSha256 { get; }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
            => _inner.ReadAsync(offset, buffer, cancellationToken);

        /// <summary>
        /// 把 <paramref name="source"/> 全量落到临时文件并返回文件源。
        /// </summary>
        /// <param name="name">逻辑文件名。</param>
        /// <param name="source">不可 Seek 的源流。</param>
        /// <param name="tempDirectory">临时目录；null 表示使用系统临时目录。</param>
        /// <param name="leaveOpen">是否保留 <paramref name="source"/> 不关闭。</param>
        public static async Task<SpoolFileSource> CreateAsync(
            string name,
            Stream source,
            string? tempDirectory = null,
            bool leaveOpen = false,
            byte[]? knownSha256 = null,
            CancellationToken cancellationToken = default)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (source == null) throw new ArgumentNullException(nameof(source));

            string dir = tempDirectory ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);
            string tempPath = Path.Combine(dir, "simpletrans-" + Guid.NewGuid().ToString("N") + ".spool");

            try
            {
                using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    byte[] buffer = new byte[81920];
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (read <= 0) break;
                        await file.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (!leaveOpen) source.Dispose();
            }

            var inner = new FilePathSource(tempPath, name, knownSha256);
            return new SpoolFileSource(inner, tempPath, name, knownSha256);
        }

        public void Dispose()
        {
            _inner.Dispose();
            try
            {
                if (File.Exists(_tempPath)) File.Delete(_tempPath);
            }
            catch (IOException)
            {
                // 临时文件清理失败不应影响主流程。
            }
        }
    }
}