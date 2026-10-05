#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans.Protocol
{
    /// <summary>
    /// 帧写入器。内部串行化写入，因此心跳等控制帧可以与数据传输并发调用而不产生交错。
    /// 不是线程安全的，每个连接一个实例。
    /// </summary>
    public sealed class FrameWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly ArrayPool<byte> _pool;
        private readonly IFrameTransformer? _transformer;
        private readonly int _maxPayloadBytes;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public FrameWriter(
            Stream stream,
            int maxPayloadBytes = ProtocolLimits.DefaultMaxPayloadBytes,
            IFrameTransformer? transformer = null)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _maxPayloadBytes = maxPayloadBytes;
            _transformer = transformer;
            _pool = ArrayPool<byte>.Shared;
        }

        /// <summary>写入一个控制消息。</summary>
        public async ValueTask WriteAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (payload.Length > _maxPayloadBytes)
                throw new SimpleTransException(ErrorCode.FrameTooLarge, "待发送 payload 超出上限");

            int total = ProtocolLimits.HeaderSize + payload.Length + ProtocolLimits.CrcSize;
            byte[] buffer = _pool.Rent(total);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(FrameWriter));

                FillHeader(buffer, type, payload.Length);
                if (payload.Length > 0) payload.CopyTo(new Memory<byte>(buffer, ProtocolLimits.HeaderSize, payload.Length));
                WriteChecksum(buffer, payload.Length);
                ApplyEncode(buffer, payload.Length);

                await _stream.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, total), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pool.Return(buffer);
                _gate.Release();
            }
        }

        /// <summary>写入无 payload 的消息。</summary>
        public ValueTask WriteEmptyAsync(MessageType type, CancellationToken cancellationToken)
            => WriteAsync(type, ReadOnlyMemory<byte>.Empty, cancellationToken);

        /// <summary>
        /// 写入一个 FileData 帧（避免为 payload 再拷贝一次数据）。
        /// </summary>
        public async ValueTask WriteFileDataAsync(Guid fileId, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            int payloadLength = MessageCodec.FileDataHeaderSize + data.Length;
            if (payloadLength > _maxPayloadBytes)
                throw new SimpleTransException(ErrorCode.FrameTooLarge, "待发送 payload 超出上限");

            int total = ProtocolLimits.HeaderSize + payloadLength + ProtocolLimits.CrcSize;
            byte[] buffer = _pool.Rent(total);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(FrameWriter));

                FillHeader(buffer, MessageType.FileData, payloadLength);
                int p = ProtocolLimits.HeaderSize;
                fileId.TryWriteBytes(new Span<byte>(buffer, p, 16));
                p += 16;

                for (int shift = 56; shift >= 0; shift -= 8) buffer[p++] = (byte)(offset >> shift);
                for (int shift = 24; shift >= 0; shift -= 8) buffer[p++] = (byte)(data.Length >> shift);
                if (data.Length > 0) data.CopyTo(new Memory<byte>(buffer, p, data.Length));

                WriteChecksum(buffer, payloadLength);
                ApplyEncode(buffer, payloadLength);

                await _stream.WriteAsync(new ReadOnlyMemory<byte>(buffer, 0, total), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pool.Return(buffer);
                _gate.Release();
            }
        }

        private static void FillHeader(byte[] buffer, MessageType type, int payloadLength)
        {
            buffer[0] = (byte)type;
            buffer[1] = (byte)(payloadLength >> 24);
            buffer[2] = (byte)(payloadLength >> 16);
            buffer[3] = (byte)(payloadLength >> 8);
            buffer[4] = (byte)payloadLength;
        }

        private void WriteChecksum(byte[] buffer, int payloadLength)
        {
            int covered = ProtocolLimits.HeaderSize + payloadLength;
            uint crc = Crc32.Begin();
            crc = Crc32.Update(crc, new ReadOnlySpan<byte>(buffer, 0, covered));
            crc = Crc32.Finish(crc);

            int offset = covered;
            buffer[offset] = (byte)(crc >> 24);
            buffer[offset + 1] = (byte)(crc >> 16);
            buffer[offset + 2] = (byte)(crc >> 8);
            buffer[offset + 3] = (byte)crc;
        }

        private void ApplyEncode(byte[] buffer, int payloadLength)
        {
            if (_transformer == null || payloadLength == 0) return;
            _transformer.Encode(new Span<byte>(buffer, ProtocolLimits.HeaderSize, payloadLength));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.Dispose();
        }
    }
}