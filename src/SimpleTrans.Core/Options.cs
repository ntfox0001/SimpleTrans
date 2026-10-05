#nullable enable

using System;

namespace SimpleTrans
{
    /// <summary>服务端选项。</summary>
    public sealed class ServerOptions
    {
        /// <summary>监听端口；0 表示由系统自动分配（用 <c>FileDistributionServer.Port</c> 读取实际端口）。</summary>
        public int Port { get; set; }

        /// <summary>绑定地址；null 表示所有地址。指定 <c>127.0.0.1</c> 可避免 Windows 防火墙弹窗。</summary>
        public string? BindAddress { get; set; }

        /// <summary>每个 FileData 帧携带的数据量（字节）。</summary>
        public int ChunkSize { get; set; } = ProtocolLimits.DefaultChunkSize;

        /// <summary>同时服务的客户端上限，超出直接拒绝并回 Error。</summary>
        public int MaxConcurrentClients { get; set; } = 16;

        /// <summary>接受到的数据帧 payload 上限（安全边界）。</summary>
        public int MaxFramePayloadBytes { get; set; } = ProtocolLimits.DefaultMaxPayloadBytes;

        /// <summary>心跳发送间隔。</summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>客户端空闲超时（既无数据也无心跳即断开）。</summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>监听队列长度。</summary>
        public int Backlog { get; set; } = 64;

        /// <summary>可选认证扩展点；null 表示不鉴权。</summary>
        public IAuthenticator? Authenticator { get; set; }

        /// <summary>可选帧变换扩展点；null 表示不做变换。</summary>
        public IFrameTransformer? FrameTransformer { get; set; }

        /// <summary>校验选项合法性，非法即抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public void Validate()
        {
            if (Port < 0 || Port > 65535) throw new ArgumentOutOfRangeException(nameof(Port), "端口必须在 0~65535 之间");
            if (ChunkSize < ProtocolLimits.MinChunkSize || ChunkSize > ProtocolLimits.MaxChunkSize)
                throw new ArgumentOutOfRangeException(nameof(ChunkSize), "ChunkSize 必须在 4KB~4MB 之间");
            if (MaxConcurrentClients < 1) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentClients));
            if (MaxFramePayloadBytes < ChunkSize) throw new ArgumentOutOfRangeException(nameof(MaxFramePayloadBytes), "必须不小于 ChunkSize");
            if (HeartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval));
            if (IdleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(IdleTimeout));
        }
    }

    /// <summary>客户端选项（连接与接收的默认值）。</summary>
    public sealed class ClientOptions
    {
        /// <summary>接受到的数据帧 payload 上限（安全边界）。</summary>
        public int MaxFramePayloadBytes { get; set; } = ProtocolLimits.DefaultMaxPayloadBytes;

        /// <summary>期望的分片大小，服务端会取其与自身配置的较小值。</summary>
        public int RequestedChunkSize { get; set; } = ProtocolLimits.DefaultChunkSize;

        /// <summary>心跳发送间隔。</summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>读取超时：超过该时间没有收到任何帧即判定连接已死。</summary>
        public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>连接超时。</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// 可选回调线程上下文（Unity 主线程 / UI 线程）。设置后所有 sink 回调都会派发到该上下文；
        /// 不设置则在 IO 线程直接回调（零拷贝，但实现方不可触碰 UI / Unity API）。
        /// </summary>
        public System.Threading.SynchronizationContext? CallbackContext { get; set; }

        /// <summary>可选断点续传状态存储；null 表示每次都从 0 开始。</summary>
        public IReceiveStateStore? ReceiveStateStore { get; set; }

        /// <summary>可选认证扩展点；null 表示不鉴权。</summary>
        public IAuthenticator? Authenticator { get; set; }

        /// <summary>可选帧变换扩展点；null 表示不做变换。</summary>
        public IFrameTransformer? FrameTransformer { get; set; }

        /// <summary>校验选项合法性，非法即抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public void Validate()
        {
            if (MaxFramePayloadBytes < ProtocolLimits.MinChunkSize) throw new ArgumentOutOfRangeException(nameof(MaxFramePayloadBytes));
            if (RequestedChunkSize < ProtocolLimits.MinChunkSize || RequestedChunkSize > ProtocolLimits.MaxChunkSize)
                throw new ArgumentOutOfRangeException(nameof(RequestedChunkSize), "RequestedChunkSize 必须在 4KB~4MB 之间");
            if (HeartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval));
            if (ReadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ReadTimeout));
            if (ConnectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }
    }

    /// <summary>单次接收调用的选项。</summary>
    public sealed class ReceiveOptions
    {
        /// <summary>只接收这些 FileId；null 或空表示接收清单中的全部文件。</summary>
        public System.Collections.Generic.IReadOnlyList<Guid>? FileIds { get; set; }

        /// <summary>是否启用断点续传（从 <see cref="IReceiveStateStore"/> 读取已收偏移）。</summary>
        public bool Resume { get; set; } = true;

        /// <summary>是否在文件结束时校验 SHA-256（清单未提供摘要时自动跳过）。</summary>
        public bool VerifySha256 { get; set; } = true;

        /// <summary>本次调用专用回调上下文；null 表示沿用 <see cref="ClientOptions.CallbackContext"/>。</summary>
        public System.Threading.SynchronizationContext? CallbackContext { get; set; }

        public static ReceiveOptions All { get; } = new ReceiveOptions();
    }
}