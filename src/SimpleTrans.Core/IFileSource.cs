#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans
{
    /// <summary>
    /// 服务端文件源抽象：以「偏移量寻址」而非 <see cref="System.IO.Stream"/> 暴露数据。
    /// <para>
    /// 之所以不用 Stream：一对多分发时，多个客户端会各自从任意偏移读取，
    /// Stream 的游标是共享可变状态，需要为每个客户端各持一份。
    /// 偏移量寻址让并发与断点续传天然成立，实现方也可以直接使用无锁的位置读。
    /// </para>
    /// <para>实现必须线程安全：同一实例会被多个客户端并发调用。</para>
    /// </summary>
    public interface IFileSource
    {
        /// <summary>逻辑文件名（用于生成 FileId 与客户端落地名）。</summary>
        string Name { get; }

        /// <summary>文件总长度（字节）。构造时即须可知。</summary>
        long Length { get; }

        /// <summary>内容 SHA-256（32 字节），未知时为 null。</summary>
        byte[]? KnownSha256 { get; }

        /// <summary>
        /// 从 <paramref name="offset"/> 开始读取，最多填满 <paramref name="buffer"/>。
        /// 返回实际读取字节数；返回 0 表示已到文件末尾。实现必须在读完前不返回 0。
        /// </summary>
        ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken);
    }
}