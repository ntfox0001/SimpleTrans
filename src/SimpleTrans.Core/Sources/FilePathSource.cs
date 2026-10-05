#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans.Sources
{
    /// <summary>
    /// 磁盘文件源。持有共享读句柄（<see cref="FileShare.Read"/>），不会被其它进程的写句柄挡住；
    /// 因为 <c>Stream</c> 的游标是共享可变状态，这里用信号量串行化「定位 + 读取」。
    /// <para>
    /// netstandard2.1 不提供 <c>RandomAccess</c>，无法做无锁位置读；对同一文件源的并发读会被排队，
    /// 但每次读只是本地磁盘（多数还命中页缓存），相对网络传输时间可忽略。
    /// 不同文件源之间完全并行。
    /// </para>
    /// </summary>
    public sealed class FilePathSource : IFileSource, IDisposable
    {
        private readonly FileStream _stream;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public FilePathSource(string path, string? name = null, byte[]? knownSha256 = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

            FullPath = Path.GetFullPath(path);
            _stream = new FileStream(
                FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 0,
                FileOptions.Asynchronous);

            Length = _stream.Length;
            Name = name ?? Path.GetFileName(FullPath);
            KnownSha256 = knownSha256;
        }

        /// <summary>绝对路径。</summary>
        public string FullPath { get; }

        public string Name { get; }

        public long Length { get; }

        public byte[]? KnownSha256 { get; }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FilePathSource));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (buffer.Length == 0) return 0;

            long remaining = Length - offset;
            if (remaining <= 0) return 0;
            if (remaining < buffer.Length) buffer = buffer.Slice(0, (int)remaining);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _stream.Seek(offset, SeekOrigin.Begin);

                int total = 0;
                while (total < buffer.Length)
                {
                    int read = await _stream.ReadAsync(buffer.Slice(total), cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    total += read;
                }
                return total;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stream.Dispose();
            _gate.Dispose();
        }
    }
}