#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans.Sources
{
    /// <summary>内存文件源。适合小文件或已在内存中的内容（如生成的配置、序列化结果）。</summary>
    public sealed class MemorySource : IFileSource
    {
        private readonly ReadOnlyMemory<byte> _data;

        public MemorySource(string name, ReadOnlyMemory<byte> data, byte[]? knownSha256 = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            Name = name;
            _data = data;
            KnownSha256 = knownSha256;
        }

        public MemorySource(string name, byte[] data, byte[]? knownSha256 = null)
            : this(name, new ReadOnlyMemory<byte>(data ?? throw new ArgumentNullException(nameof(data))), knownSha256)
        {
        }

        public string Name { get; }

        public long Length => _data.Length;

        public byte[]? KnownSha256 { get; }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (offset >= _data.Length || buffer.Length == 0) return new ValueTask<int>(0);

            int count = Math.Min(buffer.Length, _data.Length - (int)offset);
            _data.Slice((int)offset, count).CopyTo(buffer);
            return new ValueTask<int>(count);
        }
    }
}