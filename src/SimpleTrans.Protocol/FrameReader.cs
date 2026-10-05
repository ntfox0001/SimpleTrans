#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans.Protocol
{
    /// <summary>
    /// 轻量帧读取器：自带一个小读取缓冲以减少系统调用，小帧零拷贝直接从缓冲切出，
    /// 大帧租借独立缓冲。不是线程安全的，每个连接一个实例。
    /// </summary>
    public sealed class FrameReader : IDisposable
    {
        private const int DefaultReadBufferSize = 64 * 1024;

        private readonly Stream _stream;
        private readonly ArrayPool<byte> _pool;
        private readonly IFrameTransformer? _transformer;
        private readonly int _maxPayloadBytes;
        private byte[] _buffer;
        private int _start;
        private int _count;
        private bool _disposed;

        public FrameReader(
            Stream stream,
            int maxPayloadBytes = ProtocolLimits.DefaultMaxPayloadBytes,
            IFrameTransformer? transformer = null,
            int readBufferSize = DefaultReadBufferSize)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _maxPayloadBytes = maxPayloadBytes;
            _transformer = transformer;
            _pool = ArrayPool<byte>.Shared;
            _buffer = _pool.Rent(Math.Max(readBufferSize, ProtocolLimits.HeaderSize + ProtocolLimits.CrcSize + 1));
        }

        /// <summary>
        /// 读取下一帧。对端正常关闭（EOF）返回 null；帧畸形或校验失败抛 <see cref="SimpleTransException"/>。
        /// </summary>
        public async ValueTask<Frame?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FrameReader));

            // 1) 先凑齐 5 字节帧头
            if (!await EnsureAsync(ProtocolLimits.HeaderSize, cancellationToken).ConfigureAwait(false))
            {
                if (_count == 0) return null; // 干净 EOF
                throw new SimpleTransException(ErrorCode.ConnectionClosed, "连接在帧头中途关闭");
            }

            int payloadLength =
                (_buffer[_start + 1] << 24) |
                (_buffer[_start + 2] << 16) |
                (_buffer[_start + 3] << 8) |
                _buffer[_start + 4];

            if (payloadLength < 0 || payloadLength > _maxPayloadBytes)
            {
                throw new SimpleTransException(
                    ErrorCode.FrameTooLarge,
                    "帧 payload 长度 " + payloadLength.ToString() + " 超出上限 " + _maxPayloadBytes.ToString());
            }

            var type = (MessageType)_buffer[_start];
            int frameBody = payloadLength + ProtocolLimits.CrcSize;

            // 2) 小帧：直接从共享缓冲切出
            if (_count >= ProtocolLimits.HeaderSize + frameBody)
            {
                int frameStart = _start;
                int payloadStart = frameStart + ProtocolLimits.HeaderSize;

                if (_transformer != null)
                {
                    _transformer.Decode(new Span<byte>(_buffer, payloadStart, payloadLength));
                }
                VerifyChecksum(_buffer, frameStart, payloadLength);

                _start += ProtocolLimits.HeaderSize + frameBody;
                _count -= ProtocolLimits.HeaderSize + frameBody;

                // ownerPool 为 null：该帧指向共享缓冲，下次读取前有效。
                return new Frame(type, _buffer, payloadStart, payloadLength, null);
            }

            // 3) 大帧：租借连续缓冲，把已缓冲部分拷过去再补齐
            int total = ProtocolLimits.HeaderSize + frameBody;
            byte[] rented = _pool.Rent(total);
            Buffer.BlockCopy(_buffer, _start, rented, 0, _count);
            int filled = _count;
            _start = 0;
            _count = 0;

            try
            {
                while (filled < total)
                {
                    int read = await _stream.ReadAsync(
                        new Memory<byte>(rented, filled, total - filled),
                        cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                        throw new SimpleTransException(ErrorCode.ConnectionClosed, "连接在帧数据中途关闭");
                    filled += read;
                }

                if (_transformer != null)
                {
                    _transformer.Decode(new Span<byte>(rented, ProtocolLimits.HeaderSize, payloadLength));
                }
                VerifyChecksum(rented, 0, payloadLength);
                return new Frame(type, rented, ProtocolLimits.HeaderSize, payloadLength, _pool);
            }
            catch
            {
                _pool.Return(rented);
                throw;
            }
        }

        private void VerifyChecksum(byte[] buffer, int frameStart, int payloadLength)
        {
            int covered = ProtocolLimits.HeaderSize + payloadLength;
            uint crc = Crc32.Begin();
            crc = Crc32.Update(crc, new ReadOnlySpan<byte>(buffer, frameStart, covered));
            uint expected = Crc32.Finish(crc);

            int crcOffset = frameStart + covered;
            uint actual =
                ((uint)buffer[crcOffset] << 24) |
                ((uint)buffer[crcOffset + 1] << 16) |
                ((uint)buffer[crcOffset + 2] << 8) |
                buffer[crcOffset + 3];

            if (expected != actual)
                throw new SimpleTransException(ErrorCode.FrameChecksumMismatch, "帧 CRC32 校验失败");
        }

        /// <summary>确保缓冲中至少有 <paramref name="need"/> 字节；返回 false 表示对端已关闭。</summary>
        private async ValueTask<bool> EnsureAsync(int need, CancellationToken cancellationToken)
        {
            while (_count < need)
            {
                // 把未消费的数据移到缓冲头部（_start 与 _count 同时为 0 时无需搬运）。
                if (_start > 0)
                {
                    if (_count > 0) Buffer.BlockCopy(_buffer, _start, _buffer, 0, _count);
                    _start = 0;
                }

                if (_buffer.Length - _count < 1)
                {
                    // 缓冲被小帧占满（理论上不会发生：帧头最多 5 字节）
                    throw new SimpleTransException(ErrorCode.InternalError, "读取缓冲不足");
                }

                int read = await _stream.ReadAsync(
                    new Memory<byte>(_buffer, _count, _buffer.Length - _count),
                    cancellationToken).ConfigureAwait(false);
                if (read <= 0) return false;
                _count += read;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            byte[] buffer = _buffer;
            _buffer = Array.Empty<byte>();
            _start = 0;
            _count = 0;
            if (buffer.Length > 0) _pool.Return(buffer);
        }
    }
}