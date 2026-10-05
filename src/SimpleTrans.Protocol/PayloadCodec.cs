#nullable enable

using System;
using System.Buffers;
using System.Text;

namespace SimpleTrans.Protocol
{
    /// <summary>协议 payload 编码器（大端序）。仅供协议内部使用。</summary>
    internal sealed class PayloadWriter : IDisposable
    {
        private byte[] _buffer;
        private int _length;

        public PayloadWriter(int initialCapacity)
        {
            if (initialCapacity < 16) initialCapacity = 16;
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        }

        public int Length => _length;

        private void Ensure(int extra)
        {
            if (_length + extra <= _buffer.Length) return;
            int size = _buffer.Length * 2;
            while (size < _length + extra) size *= 2;
            byte[] next = ArrayPool<byte>.Shared.Rent(size);
            Buffer.BlockCopy(_buffer, 0, next, 0, _length);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[_length++] = value;
        }

        public void WriteUInt16(ushort value)
        {
            Ensure(2);
            _buffer[_length++] = (byte)(value >> 8);
            _buffer[_length++] = (byte)value;
        }

        public void WriteInt32(int value)
        {
            Ensure(4);
            _buffer[_length++] = (byte)(value >> 24);
            _buffer[_length++] = (byte)(value >> 16);
            _buffer[_length++] = (byte)(value >> 8);
            _buffer[_length++] = (byte)value;
        }

        public void WriteInt64(long value)
        {
            Ensure(8);
            for (int shift = 56; shift >= 0; shift -= 8)
            {
                _buffer[_length++] = (byte)(value >> shift);
            }
        }

        public void WriteGuid(Guid value)
        {
            Ensure(16);
            value.TryWriteBytes(new Span<byte>(_buffer, _length, 16));
            _length += 16;
        }

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            if (value.Length == 0) return;
            Ensure(value.Length);
            value.CopyTo(new Span<byte>(_buffer, _length, value.Length));
            _length += value.Length;
        }

        /// <summary>写入长度前缀字符串：ushort 字节数 + UTF-8 内容。</summary>
        public void WriteString(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > ProtocolLimits.MaxStringBytes)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "字符串字段超出上限");

            WriteUInt16((ushort)byteCount);
            Ensure(byteCount);
            Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _length);
            _length += byteCount;
        }

        /// <summary>把已写入的内容复制为一个独立数组。</summary>
        public byte[] ToArray()
        {
            byte[] result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        public void Dispose()
        {
            byte[] buffer = _buffer;
            _buffer = Array.Empty<byte>();
            _length = 0;
            if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>协议 payload 解码器（大端序）。任何越界读取都会抛 <see cref="ErrorCode.MalformedMessage"/>。</summary>
    internal ref struct PayloadReader
    {
        private readonly ReadOnlySpan<byte> _span;
        private int _position;

        public PayloadReader(ReadOnlySpan<byte> span)
        {
            _span = span;
            _position = 0;
        }

        public int Remaining => _span.Length - _position;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Remaining < count)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "消息 payload 长度不足");
            ReadOnlySpan<byte> slice = _span.Slice(_position, count);
            _position += count;
            return slice;
        }

        public byte ReadByte() => Take(1)[0];

        public ushort ReadUInt16()
        {
            ReadOnlySpan<byte> s = Take(2);
            return (ushort)((s[0] << 8) | s[1]);
        }

        public int ReadInt32()
        {
            ReadOnlySpan<byte> s = Take(4);
            return (s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3];
        }

        public long ReadInt64()
        {
            ReadOnlySpan<byte> s = Take(8);
            long value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | s[i];
            return value;
        }

        public Guid ReadGuid() => new Guid(Take(16));

        public byte[] ReadBytes(int count) => Take(count).ToArray();

        public ReadOnlySpan<byte> ReadSpan(int count) => Take(count);

        public string ReadString()
        {
            ushort byteCount = ReadUInt16();
            if (byteCount == 0) return string.Empty;
            if (byteCount > ProtocolLimits.MaxStringBytes)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "字符串字段超出上限");
            ReadOnlySpan<byte> bytes = Take(byteCount);
#if NETSTANDARD2_1
            return Encoding.UTF8.GetString(bytes);
#else
            return Encoding.UTF8.GetString(bytes.ToArray());
#endif
        }

        /// <summary>确认 payload 已全部消费，否则视为畸形。</summary>
        public void EnsureConsumed()
        {
            if (Remaining != 0)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "消息 payload 存在多余字节");
        }
    }
}