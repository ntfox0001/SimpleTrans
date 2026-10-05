#nullable enable

using System;
using System.Collections.Generic;

namespace SimpleTrans.Client
{
    /// <summary>单个文件接收失败的信息。</summary>
    public sealed class FileReceiveError
    {
        public FileReceiveError(FileManifestEntry file, Exception error)
        {
            File = file;
            Error = error;
        }

        public FileManifestEntry File { get; }
        public Exception Error { get; }
    }

    /// <summary>一次 <c>ReceiveAsync</c> 的结果汇总。</summary>
    public sealed class ReceiveResult
    {
        internal ReceiveResult(
            Guid batchId,
            int requestedCount,
            int completedCount,
            int failedCount,
            int skippedCount,
            long transferredBytes,
            IReadOnlyList<FileReceiveError> errors)
        {
            BatchId = batchId;
            RequestedCount = requestedCount;
            CompletedCount = completedCount;
            FailedCount = failedCount;
            SkippedCount = skippedCount;
            TransferredBytes = transferredBytes;
            Errors = errors;
        }

        public Guid BatchId { get; }

        /// <summary>本次请求的文件总数。</summary>
        public int RequestedCount { get; }

        /// <summary>成功接收（含校验通过）的文件数。</summary>
        public int CompletedCount { get; }

        /// <summary>失败的文件数。</summary>
        public int FailedCount { get; }

        /// <summary>因续传记录显示已完成而跳过的文件数。</summary>
        public int SkippedCount { get; }

        /// <summary>本次实际接收的字节数。</summary>
        public long TransferredBytes { get; }

        public IReadOnlyList<FileReceiveError> Errors { get; }

        public bool Succeeded => FailedCount == 0;
    }
}