using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans.Demo
{
    /// <summary>
    /// 示例落地实现：把每个文件写到指定目录，续传时以追加方式打开。
    /// 展示 <see cref="IReceiveSink"/> 的典型用法。
    /// </summary>
    internal sealed class DirectorySink : IReceiveSink
    {
        private readonly string _directory;
        private readonly Dictionary<Guid, FileStream> _open = new Dictionary<Guid, FileStream>();

        public DirectorySink(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(directory);
        }

        public ValueTask OnFileStartAsync(FileManifestEntry file, long resumeOffset, CancellationToken cancellationToken)
        {
            string path = Path.Combine(_directory, file.Name);
            var stream = new FileStream(path, resumeOffset > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.None);
            stream.SetLength(resumeOffset);
            stream.Seek(resumeOffset, SeekOrigin.Begin);
            _open[file.FileId] = stream;
            return default;
        }

        public async ValueTask OnChunkAsync(FileManifestEntry file, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            FileStream stream = _open[file.FileId];
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask OnFileCompleteAsync(FileManifestEntry file, CancellationToken cancellationToken)
        {
            FileStream stream = _open[file.FileId];
            _open.Remove(file.FileId);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Dispose();
        }

        public ValueTask OnFileFailedAsync(FileManifestEntry file, Exception error, CancellationToken cancellationToken)
        {
            if (_open.TryGetValue(file.FileId, out FileStream? stream))
            {
                _open.Remove(file.FileId);
                stream.Dispose();
            }
            Console.WriteLine("  [sink] 文件失败: " + file.Name + " - " + error.Message);
            return default;
        }
    }

    /// <summary>
    /// 在累计接收达到阈值后主动触发取消的包装 sink，用来模拟「传输中途断线」。
    /// </summary>
    internal sealed class CancellingSink : IReceiveSink
    {
        private readonly IReceiveSink _inner;
        private readonly CancellationTokenSource _cts;
        private readonly long _threshold;
        private long _received;

        public CancellingSink(IReceiveSink inner, CancellationTokenSource cts, long threshold)
        {
            _inner = inner;
            _cts = cts;
            _threshold = threshold;
        }

        public ValueTask OnFileStartAsync(FileManifestEntry file, long resumeOffset, CancellationToken cancellationToken)
            => _inner.OnFileStartAsync(file, resumeOffset, cancellationToken);

        public async ValueTask OnChunkAsync(FileManifestEntry file, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await _inner.OnChunkAsync(file, offset, data, cancellationToken).ConfigureAwait(false);
            _received += data.Length;
            if (_received >= _threshold && !_cts.IsCancellationRequested)
            {
                Console.WriteLine("  [sink] 已接收 " + _received + " 字节，主动中断以模拟断线");
                _cts.Cancel();
            }
        }

        public ValueTask OnFileCompleteAsync(FileManifestEntry file, CancellationToken cancellationToken)
            => _inner.OnFileCompleteAsync(file, cancellationToken);

        public ValueTask OnFileFailedAsync(FileManifestEntry file, Exception error, CancellationToken cancellationToken)
            => _inner.OnFileFailedAsync(file, error, cancellationToken);
    }

    /// <summary>示例续传状态存储：内存字典。真实场景可换成持久化实现。</summary>
    internal sealed class MemoryStateStore : IReceiveStateStore
    {
        private readonly ConcurrentDictionary<Guid, long> _offsets = new ConcurrentDictionary<Guid, long>();

        public void Seed(Guid fileId, long offset) => _offsets[fileId] = offset;

        public ValueTask<long> GetOffsetAsync(Guid fileId, CancellationToken cancellationToken)
            => new ValueTask<long>(_offsets.TryGetValue(fileId, out long offset) ? offset : 0L);

        public ValueTask SetOffsetAsync(Guid fileId, long offset, CancellationToken cancellationToken)
        {
            _offsets[fileId] = offset;
            return default;
        }
    }
}