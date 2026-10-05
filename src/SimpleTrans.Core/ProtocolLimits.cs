#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans
{
    /// <summary>协议硬上限与默认值。这些值是安全边界，畸形的长度字段必须在分配内存之前被拒绝。</summary>
    public static class ProtocolLimits
    {
        /// <summary>当前协议版本。双方版本不一致时直接拒绝并断开。</summary>
        public const byte ProtocolVersion = 1;

        /// <summary>帧头长度：msgType(1) + payloadLen(4)。</summary>
        public const int HeaderSize = 5;

        /// <summary>帧尾 CRC32 长度。</summary>
        public const int CrcSize = 4;

        /// <summary>单个帧 payload 的默认硬上限（8MB）。</summary>
        public const int DefaultMaxPayloadBytes = 8 * 1024 * 1024;

        /// <summary>默认分片大小（256KB）。</summary>
        public const int DefaultChunkSize = 256 * 1024;

        /// <summary>允许协商的最大分片大小（4MB）。</summary>
        public const int MaxChunkSize = 4 * 1024 * 1024;

        /// <summary>最小分片大小（4KB）。</summary>
        public const int MinChunkSize = 4 * 1024;

        /// <summary>清单里单个字符串字段的上限（文件名等），防止畸形输入撑爆内存。</summary>
        public const int MaxStringBytes = 4096;

        /// <summary>单个批次允许的最大文件数。</summary>
        public const int MaxFilesPerBatch = 100_000;
    }

    /// <summary>协议/传输层错误码，会出现在 Error 消息与本地异常中。</summary>
    public enum ErrorCode : ushort
    {
        Unknown = 0,
        ProtocolVersionMismatch = 1,
        FrameTooLarge = 2,
        FrameChecksumMismatch = 3,
        MalformedMessage = 4,
        AuthenticationFailed = 5,
        FileNotFound = 6,
        FileSourceUnavailable = 7,
        ChecksumMismatch = 8,
        TooManyClients = 9,
        Cancelled = 10,
        InternalError = 11,
        ConnectionClosed = 12,
        Timeout = 13,
        UnsupportedMessage = 14,
        NotConnected = 15,
        InvalidState = 16,
    }

    /// <summary>SimpleTrans 抛出的统一异常类型。</summary>
    public class SimpleTransException : Exception
    {
        public SimpleTransException(ErrorCode code, string message)
            : base(message)
        {
            Code = code;
        }

        public SimpleTransException(ErrorCode code, string message, Exception? innerException)
            : base(message, innerException)
        {
            Code = code;
        }

        public ErrorCode Code { get; }

        public static SimpleTransException Protocol(ErrorCode code, string message)
            => new SimpleTransException(code, message);

        public static SimpleTransException Protocol(ErrorCode code, string message, Exception? inner)
            => new SimpleTransException(code, message, inner);
    }

    /// <summary>认证扩展点的结果。</summary>
    public readonly struct AuthResult
    {
        private AuthResult(bool success, string? message)
        {
            Success = success;
            Message = message;
        }

        public bool Success { get; }

        public string? Message { get; }

        public static AuthResult Ok() => new AuthResult(true, null);

        public static AuthResult Fail(string message) => new AuthResult(false, message ?? "认证失败");
    }

    /// <summary>
    /// 认证扩展点。<b>默认关闭</b>（不设置即为 <c>null</c>，零开销）。
    /// <para>
    /// 库本身不内置加密与鉴权：任何能连上端口的人都能取走文件。
    /// 需要保护的部署方式应自己实现本接口（例如 HMAC 挑战应答）或直接在网络层（VPN / 私网）兜底。
    /// </para>
    /// </summary>
    public interface IAuthenticator
    {
        /// <summary>客户端侧：生成附加到 Hello 的认证载荷，可为 null。</summary>
        ValueTask<byte[]> CreateHelloPayloadAsync(CancellationToken cancellationToken);

        /// <summary>服务端侧：校验 Hello 携带的认证载荷。</summary>
        ValueTask<AuthResult> ValidateAsync(byte[]? helloPayload, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 帧变换扩展点，可用于加密 / 混淆整帧。<b>默认关闭</b>（不设置即为 <c>null</c>）。
    /// <para>
    /// 契约：<b>必须长度不变（in-place）</b>——帧的长度字段在变换范围之外，
    /// 接收方需要先读长度字段才能知道该读多少字节，因此变换不得改变 payload 长度。
    /// </para>
    /// <para>两端必须使用同一实现；CRC 覆盖的是变换前的原始 payload，接收方先逆变换再校验。</para>
    /// </summary>
    public interface IFrameTransformer
    {
        /// <summary>发送前就地对 payload 做变换（不含帧头与 CRC 字段）。</summary>
        void Encode(Span<byte> payload);

        /// <summary>接收后就地对 payload 做逆变换。</summary>
        void Decode(Span<byte> payload);
    }
}