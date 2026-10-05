#nullable enable

using System;
using System.Collections.Generic;

namespace SimpleTrans.Protocol
{
    /// <summary>StreamRequest 中的单个文件续传点。</summary>
    public readonly struct FileResumePoint
    {
        public FileResumePoint(Guid fileId, long offset)
        {
            FileId = fileId;
            Offset = offset;
        }

        public Guid FileId { get; }

        /// <summary>该文件已成功落地的字节数，服务端从这里开始发。</summary>
        public long Offset { get; }
    }

    /// <summary>Hello 消息。</summary>
    public sealed class HelloMessage
    {
        public HelloMessage(byte version, byte[]? authPayload = null)
        {
            Version = version;
            AuthPayload = authPayload;
        }

        public byte Version { get; }
        public byte[]? AuthPayload { get; }
    }

    /// <summary>HelloAck 消息。</summary>
    public sealed class HelloAckMessage
    {
        public HelloAckMessage(byte version, HelloStatus status, ushort chunkSize, ushort heartbeatSeconds, Guid sessionId, string? message = null)
        {
            Version = version;
            Status = status;
            ChunkSize = chunkSize;
            HeartbeatSeconds = heartbeatSeconds;
            SessionId = sessionId;
            Message = message;
        }

        public byte Version { get; }
        public HelloStatus Status { get; }
        public ushort ChunkSize { get; }
        public ushort HeartbeatSeconds { get; }
        public Guid SessionId { get; }
        public string? Message { get; }
    }

    /// <summary>StreamRequest 消息。</summary>
    public sealed class StreamRequestMessage
    {
        public StreamRequestMessage(ushort chunkSize, IReadOnlyList<FileResumePoint> files)
        {
            ChunkSize = chunkSize;
            Files = files;
        }

        public ushort ChunkSize { get; }
        public IReadOnlyList<FileResumePoint> Files { get; }
    }

    /// <summary>FileStart 消息。</summary>
    public sealed class FileStartMessage
    {
        public FileStartMessage(Guid fileId, long offset, long totalLength)
        {
            FileId = fileId;
            Offset = offset;
            TotalLength = totalLength;
        }

        public Guid FileId { get; }

        /// <summary>本次传输的起点（断点续传时大于 0）。</summary>
        public long Offset { get; }

        public long TotalLength { get; }
    }

    /// <summary>FileEnd 消息。</summary>
    public sealed class FileEndMessage
    {
        public FileEndMessage(Guid fileId, long totalLength, TransferStatus status, byte[]? sha256)
        {
            FileId = fileId;
            TotalLength = totalLength;
            Status = status;
            Sha256 = sha256;
        }

        public Guid FileId { get; }
        public long TotalLength { get; }
        public TransferStatus Status { get; }
        public byte[]? Sha256 { get; }
    }

    /// <summary>BatchEnd 消息。</summary>
    public sealed class BatchEndMessage
    {
        public BatchEndMessage(TransferStatus status, int fileCount)
        {
            Status = status;
            FileCount = fileCount;
        }

        public TransferStatus Status { get; }
        public int FileCount { get; }
    }

    /// <summary>Error 消息。</summary>
    public sealed class ErrorMessage
    {
        public ErrorMessage(ErrorCode code, string message)
        {
            Code = code;
            Message = message;
        }

        public ErrorCode Code { get; }
        public string Message { get; }
    }

    /// <summary>
    /// 消息 payload 编解码。所有整数均为大端序；所有解码入口都会做长度与上限校验，
    /// 畸形输入在分配内存之前即被拒绝。
    /// </summary>
    public static class MessageCodec
    {
        // ---------- Hello ----------

        public static byte[] EncodeHello(HelloMessage message)
        {
            using (var w = new PayloadWriter(16))
            {
                w.WriteByte(message.Version);
                byte[] auth = message.AuthPayload ?? Array.Empty<byte>();
                w.WriteUInt16((ushort)auth.Length);
                w.WriteBytes(auth);
                return w.ToArray();
            }
        }

        public static HelloMessage DecodeHello(ReadOnlySpan<byte> payload)
        {
            var r = new PayloadReader(payload);
            byte version = r.ReadByte();
            ushort authLen = r.ReadUInt16();
            if (authLen > 0 && authLen > r.Remaining)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "认证载荷长度非法");
            byte[]? auth = authLen == 0 ? null : r.ReadBytes(authLen);
            r.EnsureConsumed();
            return new HelloMessage(version, auth);
        }

        // ---------- HelloAck ----------

        public static byte[] EncodeHelloAck(HelloAckMessage message)
        {
            using (var w = new PayloadWriter(64))
            {
                w.WriteByte(message.Version);
                w.WriteByte((byte)message.Status);
                w.WriteUInt16(message.ChunkSize);
                w.WriteUInt16(message.HeartbeatSeconds);
                w.WriteGuid(message.SessionId);
                w.WriteString(message.Message ?? string.Empty);
                return w.ToArray();
            }
        }

        public static HelloAckMessage DecodeHelloAck(ReadOnlySpan<byte> payload)
        {
            var r = new PayloadReader(payload);
            byte version = r.ReadByte();
            var status = (HelloStatus)r.ReadByte();
            ushort chunkSize = r.ReadUInt16();
            ushort heartbeat = r.ReadUInt16();
            Guid sessionId = r.ReadGuid();
            string message = r.ReadString();
            r.EnsureConsumed();
            return new HelloAckMessage(version, status, chunkSize, heartbeat, sessionId, message);
        }

        // ---------- ManifestRequest ----------

        public static byte[] EncodeManifestRequest() => Array.Empty<byte>();

        // ---------- ManifestResponse ----------

        public static byte[] EncodeManifestResponse(Guid batchId, IReadOnlyList<FileManifestEntry> files)
        {
            if (files.Count > ProtocolLimits.MaxFilesPerBatch)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "单批次文件数超出上限");

            int capacity = 16 + 4 + files.Count * 32;
            using (var w = new PayloadWriter(capacity))
            {
                w.WriteGuid(batchId);
                w.WriteInt32(files.Count);
                for (int i = 0; i < files.Count; i++)
                {
                    FileManifestEntry entry = files[i];
                    w.WriteGuid(entry.FileId);
                    w.WriteString(entry.Name);
                    w.WriteInt64(entry.Length);
                    byte[]? sha = entry.Sha256;
                    if (sha != null && sha.Length == 32)
                    {
                        w.WriteByte(1);
                        w.WriteBytes(sha);
                    }
                    else
                    {
                        w.WriteByte(0);
                    }
                }
                return w.ToArray();
            }
        }

        public static void DecodeManifestResponse(
            ReadOnlySpan<byte> payload,
            out Guid batchId,
            out IReadOnlyList<FileManifestEntry> files)
        {
            var r = new PayloadReader(payload);
            batchId = r.ReadGuid();
            int count = r.ReadInt32();
            if (count < 0 || count > ProtocolLimits.MaxFilesPerBatch)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "清单文件数非法");
            // 每个条目最少 16(guid) + 2(空名) + 8(len) + 1(sha 标志) = 27 字节
            if (count > r.Remaining / 27 + 1)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "清单文件数与 payload 长度不匹配");

            var list = new List<FileManifestEntry>(count);
            for (int i = 0; i < count; i++)
            {
                Guid fileId = r.ReadGuid();
                string name = r.ReadString();
                long length = r.ReadInt64();
                if (length < 0) throw new SimpleTransException(ErrorCode.MalformedMessage, "文件长度非法");
                byte hasSha = r.ReadByte();
                byte[]? sha = null;
                if (hasSha == 1) sha = r.ReadBytes(32);
                else if (hasSha != 0) throw new SimpleTransException(ErrorCode.MalformedMessage, "SHA 标志非法");
                list.Add(new FileManifestEntry(fileId, name, length, sha));
            }
            r.EnsureConsumed();
            files = list;
        }

        // ---------- StreamRequest ----------

        public static byte[] EncodeStreamRequest(StreamRequestMessage message)
        {
            if (message.Files.Count > ProtocolLimits.MaxFilesPerBatch)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "单批次文件数超出上限");

            using (var w = new PayloadWriter(6 + message.Files.Count * 24))
            {
                w.WriteUInt16(message.ChunkSize);
                w.WriteInt32(message.Files.Count);
                for (int i = 0; i < message.Files.Count; i++)
                {
                    FileResumePoint point = message.Files[i];
                    w.WriteGuid(point.FileId);
                    w.WriteInt64(point.Offset);
                }
                return w.ToArray();
            }
        }

        public static void DecodeStreamRequest(ReadOnlySpan<byte> payload, out StreamRequestMessage message)
        {
            var r = new PayloadReader(payload);
            ushort chunkSize = r.ReadUInt16();
            int count = r.ReadInt32();
            if (count < 0 || count > ProtocolLimits.MaxFilesPerBatch)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "请求文件数非法");
            if (count > r.Remaining / 24 + 1)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "请求文件数与 payload 长度不匹配");

            var list = new List<FileResumePoint>(count);
            for (int i = 0; i < count; i++)
            {
                Guid fileId = r.ReadGuid();
                long offset = r.ReadInt64();
                if (offset < 0) throw new SimpleTransException(ErrorCode.MalformedMessage, "续传偏移非法");
                list.Add(new FileResumePoint(fileId, offset));
            }
            r.EnsureConsumed();
            message = new StreamRequestMessage(chunkSize, list);
        }

        // ---------- FileStart ----------

        public static byte[] EncodeFileStart(FileStartMessage message)
        {
            using (var w = new PayloadWriter(32))
            {
                w.WriteGuid(message.FileId);
                w.WriteInt64(message.Offset);
                w.WriteInt64(message.TotalLength);
                return w.ToArray();
            }
        }

        public static FileStartMessage DecodeFileStart(ReadOnlySpan<byte> payload)
        {
            var r = new PayloadReader(payload);
            Guid fileId = r.ReadGuid();
            long offset = r.ReadInt64();
            long totalLength = r.ReadInt64();
            if (offset < 0 || totalLength < 0)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "文件长度/偏移非法");
            r.EnsureConsumed();
            return new FileStartMessage(fileId, offset, totalLength);
        }

        // ---------- FileData ----------

        /// <summary>FileData 的 payload 头长度：fileId(16) + offset(8) + dataLen(4)。</summary>
        public const int FileDataHeaderSize = 28;

        public static void DecodeFileData(ReadOnlySpan<byte> payload, out Guid fileId, out long offset, out ReadOnlySpan<byte> data)
        {
            var r = new PayloadReader(payload);
            fileId = r.ReadGuid();
            offset = r.ReadInt64();
            int dataLength = r.ReadInt32();
            if (dataLength < 0 || dataLength != r.Remaining)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "FileData 长度字段与实际不符");
            if (offset < 0)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "FileData 偏移非法");
            data = r.ReadSpan(dataLength);
        }

        /// <summary>
        /// 只解析 FileData 的头部（不含数据本身）。数据视图可由调用方按
        /// <see cref="FileDataHeaderSize"/> 从 payload 切出，避免在 async 方法里使用 ref struct。
        /// </summary>
        public static (Guid FileId, long Offset, int DataLength) DecodeFileDataHeader(ReadOnlySpan<byte> payload)
        {
            var r = new PayloadReader(payload);
            Guid fileId = r.ReadGuid();
            long offset = r.ReadInt64();
            int dataLength = r.ReadInt32();
            if (dataLength < 0 || dataLength != r.Remaining)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "FileData 长度字段与实际不符");
            if (offset < 0)
                throw new SimpleTransException(ErrorCode.MalformedMessage, "FileData 偏移非法");
            return (fileId, offset, dataLength);
        }

        // ---------- FileEnd ----------

        public static byte[] EncodeFileEnd(FileEndMessage message)
        {
            using (var w = new PayloadWriter(64))
            {
                w.WriteGuid(message.FileId);
                w.WriteInt64(message.TotalLength);
                w.WriteByte((byte)message.Status);
                byte[]? sha = message.Sha256;
                if (sha != null && sha.Length == 32)
                {
                    w.WriteByte(1);
                    w.WriteBytes(sha);
                }
                else
                {
                    w.WriteByte(0);
                }
                return w.ToArray();
            }
        }

        public static FileEndMessage DecodeFileEnd(ReadOnlySpan<byte> payload)
        {
            var r = new PayloadReader(payload);
            Guid fileId = r.ReadGuid();
            long totalLength = r.ReadInt64();
            var status = (TransferStatus)r.ReadByte();
            byte hasSha = r.ReadByte();
            byte[]? sha = null;
            if (hasSha == 1) sha = r.ReadBytes(32);
            else if (hasSha != 0) throw new SimpleTransException(ErrorCode.MalformedMessage, "SHA 标志非法");
            r.EnsureConsumed();
            return new FileEndMessage(fileId, totalLength, status, sha);
        }

        // ---------- BatchEnd ----------

        public static byte[] EncodeBatchEnd(BatchEndMessage message)
        {
            using (var w = new PayloadWriter(16))
            {
                w.WriteByte((byte)message.Status);
                w.WriteInt32(message.FileCount);
                return w.ToArray();
            }
        }

        public static BatchEndMessage DecodeBatchEnd(ReadOnlySpan<byte> payload)
        {
            var r = new PayloadReader(payload);
            var status = (TransferStatus)r.ReadByte();
            int fileCount = r.ReadInt32();
            r.EnsureConsumed();
            return new BatchEndMessage(status, fileCount);
        }

        // ---------- Heartbeat / Cancel / Error ----------

        public static byte[] EncodeHeartbeat(HeartbeatKind kind)
        {
            using (var w = new PayloadWriter(4))
            {
                w.WriteByte((byte)kind);
                return w.ToArray();
            }
        }

        public static HeartbeatKind DecodeHeartbeat(ReadOnlySpan<byte> payload)
        {
            var r = new PayloadReader(payload);
            var kind = (HeartbeatKind)r.ReadByte();
            r.EnsureConsumed();
            return kind;
        }

        public static byte[] EncodeCancel(CancelReason reason)
        {
            using (var w = new PayloadWriter(4))
            {
                w.WriteByte((byte)reason);
                return w.ToArray();
            }
        }

        public static CancelReason DecodeCancel(ReadOnlySpan<byte> payload)
        {
            var r = new PayloadReader(payload);
            var reason = (CancelReason)r.ReadByte();
            r.EnsureConsumed();
            return reason;
        }

        public static byte[] EncodeError(ErrorMessage message)
        {
            using (var w = new PayloadWriter(64))
            {
                w.WriteUInt16((ushort)message.Code);
                w.WriteString(message.Message ?? string.Empty);
                return w.ToArray();
            }
        }

        public static ErrorMessage DecodeError(ReadOnlySpan<byte> payload)
        {
            var r = new PayloadReader(payload);
            var code = (ErrorCode)r.ReadUInt16();
            string message = r.ReadString();
            r.EnsureConsumed();
            return new ErrorMessage(code, message);
        }
    }
}