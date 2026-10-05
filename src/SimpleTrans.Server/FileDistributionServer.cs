#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleTrans.Server
{
    /// <summary>
    /// 批量文件分发服务器：一对多广播，客户端自行拉取清单并按偏移续传。
    /// <para>
    /// 库不负责设备发现与地址交换——IP/端口由宿主应用通过自己的渠道传递。
    /// 库也不内置鉴权与加密：任何能连上该端口的人都能取走文件，需要保护的场景请实现
    /// <see cref="IAuthenticator"/> / <see cref="IFrameTransformer"/> 或在网络层（私网、VPN）兜底。
    /// </para>
    /// </summary>
    public sealed class FileDistributionServer : IAsyncDisposable
    {
        private readonly ServerOptions _options;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<ClientSession> _sessions = new List<ClientSession>();
        private readonly object _sync = new object();

        private TcpListener? _listener;
        private Task? _acceptLoop;
        private Dictionary<Guid, IFileSource> _sources = new Dictionary<Guid, IFileSource>();
        private Manifest? _manifest;
        private int _activeClients;
        private bool _started;
        private bool _disposed;

        private int _activeTransfers;
        private TaskCompletionSource<bool> _completion = CreateCompleted();

        public FileDistributionServer(ServerOptions? options = null)
        {
            _options = options ?? new ServerOptions();
            _options.Validate();
        }

        // ---------------- 事件 ----------------

        public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
        public event EventHandler<ClientProgressEventArgs>? ClientProgress;
        public event EventHandler<FileCompletedEventArgs>? FileCompleted;
        public event EventHandler<ClientCompletedEventArgs>? ClientCompleted;
        public event EventHandler<ClientFailedEventArgs>? ClientFailed;

        // ---------------- 状态 ----------------

        /// <summary>实际监听端口（<see cref="StartAsync"/> 之后有效；选项端口为 0 时由系统分配）。</summary>
        public int Port { get; private set; }

        public IPEndPoint? LocalEndPoint { get; private set; }

        public bool IsRunning => _started && !_disposed;

        /// <summary>当前批次清单；未发布任何文件时为 null。</summary>
        public Manifest? CurrentManifest => Volatile.Read(ref _manifest);

        /// <summary>当前已接入的客户端数。</summary>
        public int ConnectedClientCount => Volatile.Read(ref _activeClients);

        // ---------------- 生命周期 ----------------

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PlatformNotSupportedException(
                "SimpleTrans 不支持 WebGL：WebGL 平台无法使用原始 TCP socket。请在桌面 / 移动端构建中使用。");
#else
            if (_disposed) throw new ObjectDisposedException(nameof(FileDistributionServer));
            if (_started) throw new SimpleTransException(ErrorCode.InvalidState, "服务器已启动");

            IPAddress address = string.IsNullOrEmpty(_options.BindAddress)
                ? IPAddress.Any
                : IPAddress.Parse(_options.BindAddress!);

            var listener = new TcpListener(address, _options.Port);
            listener.Start(_options.Backlog);

            _listener = listener;
            LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
            Port = LocalEndPoint.Port;
            _started = true;

            cancellationToken.ThrowIfCancellationRequested();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return Task.CompletedTask;
#endif
        }

        /// <summary>优雅停止：取消所有会话并关闭监听。可重复调用。</summary>
        public async Task StopAsync()
        {
            if (!_started) return;
            _started = false;

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
            }

            ClientSession[] sessions;
            lock (_sync)
            {
                sessions = _sessions.ToArray();
                _sessions.Clear();
            }

            var pending = new List<Task>();
            foreach (ClientSession session in sessions)
            {
                Task? task = session.TransferTask;
                if (task != null) pending.Add(task);
            }
            if (_acceptLoop != null) pending.Add(_acceptLoop);

            if (pending.Count > 0)
            {
                try
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                catch
                {
                    // 关闭过程中的异常不阻断收尾。
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            await StopAsync().ConfigureAwait(false);
            _disposed = true;
            _listener = null;
            _cts.Dispose();
        }

        // ---------------- 发布 ----------------

        /// <summary>
        /// 用一组文件源替换当前批次。调用方拥有这些文件源的生命周期，服务器不会 Dispose 它们。
        /// <para>建议在上一批次传输结束后再发布新批次；替换会让正在传输的客户端找不到文件而失败。</para>
        /// </summary>
        public Task PublishAsync(IEnumerable<IFileSource> sources)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));

            var entries = new List<FileManifestEntry>();
            var map = new Dictionary<Guid, IFileSource>();
            foreach (IFileSource source in sources)
            {
                if (source == null) continue;
                Guid fileId = FileIdentity.ComputeFileId(source);
                if (map.ContainsKey(fileId)) continue;

                map.Add(fileId, source);
                entries.Add(new FileManifestEntry(fileId, source.Name, source.Length, source.KnownSha256));
            }

            _sources = map;
            Volatile.Write(ref _manifest, new Manifest(Guid.NewGuid(), entries));
            if (Volatile.Read(ref _activeTransfers) == 0)
            {
                Volatile.Write(ref _completion, CreateCompleted());
            }
            return Task.CompletedTask;
        }

        /// <summary>便捷重载：把一组磁盘路径作为批次发布，返回发布成功的文件数。</summary>
        public Task<int> PublishAsync(IEnumerable<string> paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));

            var list = new List<IFileSource>();
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                list.Add(new Sources.FilePathSource(path));
            }

            PublishAsync(list);
            return Task.FromResult(list.Count);
        }

        /// <summary>
        /// 等待当前所有进行中的传输完成。没有任何传输在跑时立即返回。
        /// </summary>
        public Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
        {
            Task task = Volatile.Read(ref _completion).Task;
            if (!cancellationToken.CanBeCanceled) return task;
            return WithCancellationAsync(task, cancellationToken);
        }

        // ---------------- 内部：会话管理 ----------------

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            TcpListener? listener = _listener;
            if (listener == null) return;

            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    RaiseClientFailed(Guid.Empty, ex);
                    continue;
                }

                var session = new ClientSession(this, _options, tcp, cancellationToken);
                lock (_sync)
                {
                    _sessions.Add(session);
                }

                _ = Task.Run(() => session.RunAsync());
            }
        }

        internal bool TryRegisterClient()
        {
            while (true)
            {
                int current = Volatile.Read(ref _activeClients);
                if (current >= _options.MaxConcurrentClients) return false;
                if (Interlocked.CompareExchange(ref _activeClients, current + 1, current) == current) return true;
            }
        }

        internal void UnregisterClient() => Interlocked.Decrement(ref _activeClients);

        internal void UnregisterSession(ClientSession session)
        {
            lock (_sync)
            {
                _sessions.Remove(session);
            }
        }

        internal IFileSource? FindSource(Guid fileId)
        {
            Dictionary<Guid, IFileSource> map = _sources;
            return map.TryGetValue(fileId, out IFileSource? source) ? source : null;
        }

        internal void NotifyTransferStarted()
        {
            if (Interlocked.Increment(ref _activeTransfers) == 1)
            {
                Volatile.Write(ref _completion, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }

        internal void NotifyTransferFinished()
        {
            if (Interlocked.Decrement(ref _activeTransfers) == 0)
            {
                Volatile.Read(ref _completion).TrySetResult(true);
            }
        }

        private static TaskCompletionSource<bool> CreateCompleted()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult(true);
            return tcs;
        }

        private static async Task WithCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            var cancelSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelSignal))
            {
                Task finished = await Task.WhenAny(task, cancelSignal.Task).ConfigureAwait(false);
                if (finished != task)
                    throw new OperationCanceledException(cancellationToken);
            }
            await task.ConfigureAwait(false);
        }

        // ---------------- 内部：事件派发 ----------------

        internal void RaiseClientConnected(Guid sessionId, string remoteEndPoint)
            => SafeRaise(ClientConnected, new ClientConnectedEventArgs(sessionId, remoteEndPoint));

        internal void RaiseClientProgress(Guid sessionId, TransferProgress progress)
            => SafeRaise(ClientProgress, new ClientProgressEventArgs(sessionId, progress));

        internal void RaiseFileCompleted(Guid sessionId, Guid fileId, string name, long length, bool succeeded, Exception? error)
            => SafeRaise(FileCompleted, new FileCompletedEventArgs(sessionId, fileId, name, length, succeeded, error));

        internal void RaiseClientCompleted(Guid sessionId, int fileCount, int failedCount)
            => SafeRaise(ClientCompleted, new ClientCompletedEventArgs(sessionId, fileCount, failedCount));

        internal void RaiseClientFailed(Guid sessionId, Exception error)
            => SafeRaise(ClientFailed, new ClientFailedEventArgs(sessionId, error));

        private static void SafeRaise<T>(EventHandler<T>? handler, T args) where T : EventArgs
        {
            if (handler == null) return;
            try
            {
                handler.Invoke(null, args);
            }
            catch
            {
                // 使用方的回调异常不得影响分发流程。
            }
        }
    }
}