#nullable enable

namespace SimpleTrans.Protocol
{
    /// <summary>
    /// 帧类型。帧格式：<c>[1B msgType][4B payloadLen 大端][payload][4B CRC32 大端]</c>，
    /// CRC 覆盖 msgType + payloadLen + payload。
    /// </summary>
    public enum MessageType : byte
    {
        /// <summary>C→S：握手，携带协议版本与可选认证载荷。</summary>
        Hello = 0x01,

        /// <summary>S→C：握手应答，携带协商后的分片大小与心跳间隔。</summary>
        HelloAck = 0x02,

        /// <summary>C→S：请求清单。</summary>
        ManifestRequest = 0x03,

        /// <summary>S→C：清单响应（批次内的全部文件）。</summary>
        ManifestResponse = 0x04,

        /// <summary>C→S：请求开始传输，携带选定子集与每个文件的已收偏移（断点续传）。</summary>
        StreamRequest = 0x05,

        /// <summary>S→C：单个文件开始。</summary>
        FileStart = 0x06,

        /// <summary>S→C：一个数据分片。</summary>
        FileData = 0x07,

        /// <summary>S→C：单个文件结束，携带总长度与可选 SHA-256。</summary>
        FileEnd = 0x08,

        /// <summary>S→C：整批结束。</summary>
        BatchEnd = 0x09,

        /// <summary>双向：心跳（Kind=0 为 ping，Kind=1 为 ack）。</summary>
        Heartbeat = 0x0A,

        /// <summary>双向：优雅取消。</summary>
        Cancel = 0x0B,

        /// <summary>双向：错误。</summary>
        Error = 0x0C,
    }

    /// <summary>握手结果。</summary>
    public enum HelloStatus : byte
    {
        Ok = 0,
        VersionMismatch = 1,
        Rejected = 2,
        TooManyClients = 3,
    }

    /// <summary>心跳种类。</summary>
    public enum HeartbeatKind : byte
    {
        Ping = 0,
        Ack = 1,
    }

    /// <summary>文件/批次的结束状态。</summary>
    public enum TransferStatus : byte
    {
        Ok = 0,
        Failed = 1,
        Cancelled = 2,
    }

    /// <summary>取消原因。</summary>
    public enum CancelReason : byte
    {
        UserCancelled = 0,
        ClientStopped = 1,
        ServerShutdown = 2,
        ProtocolError = 3,
    }
}