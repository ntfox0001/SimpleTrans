#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans
{
    /// <summary>
    /// 接收端落地抽象：由使用方实现，决定数据写到哪里（磁盘文件 / 内存 / AssetBundle / 自定义存储）。
    /// <para>
    /// 全部为异步方法，库会 await 返回值后才回收内部缓冲，因此实现方可以直接持有
    /// <c>ReadOnlyMemory&lt;byte&gt;</c> 直到返回。若要跨调用保留数据，必须在返回前自行拷贝。
    /// </para>
    /// <para>同一个 sink 实例只会被单个客户端顺序调用（同一时刻不会重入）。</para>
    /// </summary>
    public interface IReceiveSink
    {
        /// <summary>
        /// 一个文件即将开始传输。<paramref name="resumeOffset"/> 为本次起点（0 表示从头）。
        /// 实现方应在此打开/定位目标存储。
        /// </summary>
        ValueTask OnFileStartAsync(FileManifestEntry file, long resumeOffset, CancellationToken cancellationToken);

        /// <summary>收到一段数据。<paramref name="offset"/> 为该段在文件中的绝对偏移。</summary>
        ValueTask OnChunkAsync(FileManifestEntry file, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

        /// <summary>文件接收完成且校验通过。</summary>
        ValueTask OnFileCompleteAsync(FileManifestEntry file, CancellationToken cancellationToken);

        /// <summary>文件接收失败（校验不符、连接中断等）。实现方应清理半成品或保留以便续传。</summary>
        ValueTask OnFileFailedAsync(FileManifestEntry file, Exception error, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 断点续传状态存储抽象：记录每个文件已成功落地的字节数。
    /// <para>可选；不提供时客户端一律从 0 开始。实现必须线程安全且可跨进程持久化（若希望跨启动续传）。</para>
    /// </summary>
    public interface IReceiveStateStore
    {
        /// <summary>返回该文件已完成的字节数，无记录返回 0。</summary>
        ValueTask<long> GetOffsetAsync(Guid fileId, CancellationToken cancellationToken);

        /// <summary>记录该文件已完成的字节数。</summary>
        ValueTask SetOffsetAsync(Guid fileId, long offset, CancellationToken cancellationToken);
    }
}