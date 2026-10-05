#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans.Sources
{
    /// <summary>
    /// 包装一个可 Seek 的 <see cref="Stream"/>（如 Unity AssetBundle 的流、<see cref="FileStream"/>、
    /// <see cref="MemoryStream"/>）。因为 Stream 的游标是共享可变状态，这里用锁串行化「定位 + 读取」，
    /// 并发度不如 <see cref="FilePathSource"/>，但保证正确。
    /// </summary>
    public sealed class StreamFileSource : IFileSource, IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly object _gate = new object();
        private bool _disposed;

        public StreamFileSource(string name, Stream stream, bool leaveOpen = false, byte[]? knownSha256 = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek) throw new ArgumentException("Stream 必须可 Seek；不可 Seek 的源请使用 SpoolFileSource", nameof(stream));

            Name = name;
            _leaveOpen = leaveOpen;
            KnownSha256 = knownSha256;
            Length = stream.Length;
        }

        public string Name { get; }

        public long Length { get; }

        public byte[]? KnownSha256 { get; }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StreamFileSource));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (buffer.Length == 0) return 0;

            long remaining = Length - offset;
            if (remaining <= 0) return 0;
            if (remaining < buffer.Length) buffer = buffer.Slice(0, (int)remaining);

            // 同一 Stream 实例的游标不可并发使用：先定位再读，整段互斥。
            // 互斥区内不做 await，避免异步重入。
            int total = 0;
            lock (_gate)
            {
                _stream.Seek(offset, SeekOrigin.Begin);
                while (total < buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = _stream.Read(buffer.Span.Slice(total));
                    if (read <= 0) break;
                    total += read;
                }
            }
            await Task.CompletedTask.ConfigureAwait(false);
            return total;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_leaveOpen) _stream.Dispose();
        }
    }
}