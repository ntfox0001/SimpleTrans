#nullable enable

using System;

namespace SimpleTrans.Server
{
    /// <summary>客户端接入。</summary>
    public sealed class ClientConnectedEventArgs : EventArgs
    {
        public ClientConnectedEventArgs(Guid sessionId, string remoteEndPoint)
        {
            SessionId = sessionId;
            RemoteEndPoint = remoteEndPoint;
        }

        public Guid SessionId { get; }
        public string RemoteEndPoint { get; }
    }

    /// <summary>客户端传输进度（每个分片触发一次）。</summary>
    public sealed class ClientProgressEventArgs : EventArgs
    {
        public ClientProgressEventArgs(Guid sessionId, TransferProgress progress)
        {
            SessionId = sessionId;
            Progress = progress;
        }

        public Guid SessionId { get; }
        public TransferProgress Progress { get; }
    }

    /// <summary>单个文件发送结束。</summary>
    public sealed class FileCompletedEventArgs : EventArgs
    {
        public FileCompletedEventArgs(Guid sessionId, Guid fileId, string fileName, long length, bool succeeded, Exception? error)
        {
            SessionId = sessionId;
            FileId = fileId;
            FileName = fileName;
            Length = length;
            Succeeded = succeeded;
            Error = error;
        }

        public Guid SessionId { get; }
        public Guid FileId { get; }
        public string FileName { get; }
        public long Length { get; }
        public bool Succeeded { get; }
        public Exception? Error { get; }
    }

    /// <summary>一个客户端的整批传输结束。</summary>
    public sealed class ClientCompletedEventArgs : EventArgs
    {
        public ClientCompletedEventArgs(Guid sessionId, int fileCount, int failedCount)
        {
            SessionId = sessionId;
            FileCount = fileCount;
            FailedCount = failedCount;
        }

        public Guid SessionId { get; }
        public int FileCount { get; }
        public int FailedCount { get; }
        public bool Succeeded => FailedCount == 0;
    }

    /// <summary>客户端会话异常结束。</summary>
    public sealed class ClientFailedEventArgs : EventArgs
    {
        public ClientFailedEventArgs(Guid sessionId, Exception error)
        {
            SessionId = sessionId;
            Error = error;
        }

        public Guid SessionId { get; }
        public Exception Error { get; }
    }
}