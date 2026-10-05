#nullable enable

using System;
using System.Buffers;

namespace SimpleTrans.Protocol
{
    /// <summary>
    /// 一个已解出的帧。<see cref="Payload"/> 指向内部缓冲，<b>仅在处理完当前帧、再次调用
    /// <see cref="FrameReader.ReadFrameAsync"/> 之前有效</b>。
    /// <para>
    /// 小帧直接指向读取器的共享缓冲（零拷贝，<see cref="Dispose"/> 不做归还）；
    /// 大帧持有从池中租借的独立缓冲，<see cref="Dispose"/> 时归还。因此必须 Dispose。
    /// </para>
    /// </summary>
    public sealed class Frame : IDisposable
    {
        private byte[]? _buffer;
        private readonly ArrayPool<byte>? _ownerPool;

        internal Frame(MessageType type, byte[] buffer, int offset, int length, ArrayPool<byte>? ownerPool)
        {
            Type = type;
            _buffer = buffer;
            Offset = offset;
            Length = length;
            _ownerPool = ownerPool;
        }

        public MessageType Type { get; }

        internal int Offset { get; }

        internal int Length { get; }

        /// <summary>payload 只读视图。</summary>
        public ReadOnlyMemory<byte> Payload =>
            new ReadOnlyMemory<byte>(_buffer ?? Array.Empty<byte>(), Offset, Length);

        /// <summary>payload 只读 span（同步解析用，避免拷贝）。</summary>
        public ReadOnlySpan<byte> PayloadSpan =>
            new ReadOnlySpan<byte>(_buffer ?? Array.Empty<byte>(), Offset, Length);

        public void Dispose()
        {
            byte[]? buffer = _buffer;
            if (buffer != null && _ownerPool != null)
            {
                _buffer = null;
                _ownerPool.Return(buffer);
            }
        }
    }
}