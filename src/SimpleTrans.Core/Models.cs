#nullable enable

using System;
using System.Collections.Generic;

namespace SimpleTrans
{
    /// <summary>
    /// 清单中的单个文件条目。
    /// <para>
    /// <see cref="FileId"/> = SHA256(name ‖ contentSha256) 的前 16 字节，跨服务端重启稳定，
    /// 因此客户端的断点续传记录可以长期复用。当文件源无法预先提供内容摘要时，
    /// 退化为 SHA256(name ‖ length)，此时内容变化不会被 FileId 感知。
    /// </para>
    /// </summary>
    public sealed class FileManifestEntry
    {
        public FileManifestEntry(Guid fileId, string name, long length, byte[]? sha256 = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "文件长度不能为负");
            if (sha256 != null && sha256.Length != 32)
                throw new ArgumentException("SHA-256 必须为 32 字节", nameof(sha256));

            FileId = fileId;
            Name = name;
            Length = length;
            Sha256 = sha256;
        }

        /// <summary>稳定文件标识，断点续传以此为主键。</summary>
        public Guid FileId { get; }

        /// <summary>文件的逻辑名称，由使用方决定是否当作落地文件名。</summary>
        public string Name { get; }

        /// <summary>文件总字节数。</summary>
        public long Length { get; }

        /// <summary>内容 SHA-256（32 字节），可能为 null（服务端未提供）。请勿修改该数组。</summary>
        public byte[]? Sha256 { get; }

        /// <summary>是否携带内容摘要。</summary>
        public bool HasSha256 => Sha256 != null;

        public override string ToString() => Name + " (" + Length.ToString() + " bytes)";
    }

    /// <summary>一批待分发文件的清单。</summary>
    public sealed class Manifest
    {
        public Manifest(Guid batchId, IReadOnlyList<FileManifestEntry> files)
        {
            BatchId = batchId;
            Files = files ?? throw new ArgumentNullException(nameof(files));
        }

        /// <summary>本批次标识；同一批次内容不变，可用于幂等判断。</summary>
        public Guid BatchId { get; }

        public IReadOnlyList<FileManifestEntry> Files { get; }

        /// <summary>按 FileId 查找条目，未找到返回 null。</summary>
        public FileManifestEntry? Find(Guid fileId)
        {
            for (int i = 0; i < Files.Count; i++)
            {
                if (Files[i].FileId == fileId) return Files[i];
            }
            return null;
        }

        public long TotalLength
        {
            get
            {
                long sum = 0;
                for (int i = 0; i < Files.Count; i++) sum += Files[i].Length;
                return sum;
            }
        }
    }

    /// <summary>单文件传输进度快照。</summary>
    public sealed class TransferProgress
    {
        public TransferProgress(Guid fileId, string fileName, long transferred, long total)
        {
            FileId = fileId;
            FileName = fileName;
            Transferred = transferred;
            Total = total;
        }

        public Guid FileId { get; }
        public string FileName { get; }
        public long Transferred { get; }
        public long Total { get; }

        /// <summary>0~1 的比例；Total 为 0 时视为 1。</summary>
        public double Ratio => Total <= 0 ? 1d : (double)Transferred / Total;

        public override string ToString() =>
            FileName + ": " + Transferred.ToString() + "/" + Total.ToString();
    }
}