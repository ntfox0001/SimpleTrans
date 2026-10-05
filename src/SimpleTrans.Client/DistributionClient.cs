#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SimpleTrans.Protocol;

namespace SimpleTrans.Client
{
    /// <summary>
    /// 接收客户端：连接 → 拉清单 → 按选定子集与续传偏移请求数据 → 投递给 <see cref="IReceiveSink"/>。
    /// <para>
    /// 单连接、单线程使用：<see cref="GetManifestAsync"/> 与 <see cref="ReceiveAsync"/> 不可并发调用。
    /// 心跳在后台独立运行，不影响前台调用。
    /// </para>
    /// <para>
    /// <b>禁止</b>在 UI / Unity 主线程上对返回的 Task 使用 <c>.Result</c> / <c>.Wait()</c>（会死锁）；
    /// 需要回调回主线程时请设置 <see cref="ClientOptions.CallbackContext"/>。
    /// </para>
    /// </summary>
    public sealed class DistributionClient : IDisposable, IAsyncDisposable
    {
        private readonly ClientOptions _options;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private TcpClient? _tcp;
        private FrameReader? _reader;
        private FrameWriter? _writer;
        private Task? _heartbeat;
        private SynchronizationContext? _callbackContext;
        private Manifest? _manifest;
        private int _negotiatedChunkSize;
        private bool _disposed;

        public DistributionClient(ClientOptions? options = null)
        {
            _options = options ?? new ClientOptions();
            _options.Validate();
            _callbackContext = _options.CallbackContext;
        }

        /// <summary>是否已完成握手。</summary>
        public bool IsConnected { get; private set; }

        /// <summary>服务端分配的会话标识。</summary>
        public Guid SessionId { get; private set; }

        /// <summary>协商后的分片大小（<see cref="ConnectAsync"/> 之后有效）。</summary>
        public int NegotiatedChunkSize => _negotiatedChunkSize;

        /// <summary>最近一次拉取到的清单。</summary>
        public Manifest? Manifest => _manifest;

        // ---------------- 连接 ----------------

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PlatformNotSupportedException(
                "SimpleTrans 不支持 WebGL：WebGL 平台无法使用原始 TCP socket。请在桌面 / 移动端构建中使用。");
#else
            if (_disposed) throw new ObjectDisposedException(nameof(DistributionClient));
            if (IsConnected) throw new SimpleTransException(ErrorCode.InvalidState, "客户端已连接");
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException(nameof(host));

            var tcp = new TcpClient
            {
                NoDelay = true,
            };

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_options.ConnectTimeout);
                try
                {
                    await WithCancellationAsync(tcp.ConnectAsync(host, port), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    tcp.Close();
                    throw new SimpleTransException(ErrorCode.Timeout, "连接超时: " + host + ":" + port.ToString());
                }
                catch (SocketException ex)
                {
                    tcp.Close();
                    throw new SimpleTransException(ErrorCode.ConnectionClosed, "无法连接到 " + host + ":" + port.ToString() + " - " + ex.Message, ex);
                }
                catch
                {
                    tcp.Close();
                    throw;
                }
            }

            _tcp = tcp;
            Stream stream = tcp.GetStream();
            _reader = new FrameReader(stream, _options.MaxFramePayloadBytes, _options.FrameTransformer);
            _writer = new FrameWriter(stream, _options.MaxFramePayloadBytes, _options.FrameTransformer);

            try
            {
                await HandshakeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                CleanupConnection();
                throw;
            }

            IsConnected = true;
            _heartbeat = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
#endif
        }

        private async Task HandshakeAsync(CancellationToken cancellationToken)
        {
            byte[]? authPayload = null;
            if (_options.Authenticator != null)
            {
                authPayload = await _options.Authenticator.CreateHelloPayloadAsync(cancellationToken).ConfigureAwait(false);
            }

            await _writer!.WriteAsync(
                MessageType.Hello,
                MessageCodec.EncodeHello(new HelloMessage(ProtocolLimits.ProtocolVersion, authPayload)),
                cancellationToken).ConfigureAwait(false);

            Frame? frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            using (frame)
            {
                if (frame == null)
                    throw new SimpleTransException(ErrorCode.ConnectionClosed, "服务端在握手阶段关闭了连接");
                if (frame.Type == MessageType.Error)
                {
                    ErrorMessage error = MessageCodec.DecodeError(frame.PayloadSpan);
                    throw new SimpleTransException(error.Code, "服务端拒绝连接：" + error.Message);
                }
                if (frame.Type != MessageType.HelloAck)
                    throw new SimpleTransException(ErrorCode.MalformedMessage, "握手阶段收到非 HelloAck 消息");

                HelloAckMessage ack = MessageCodec.DecodeHelloAck(frame.PayloadSpan);
                if (ack.Status != HelloStatus.Ok)
                {
                    ErrorCode code = ack.Status == HelloStatus.VersionMismatch
                        ? ErrorCode.ProtocolVersionMismatch
                        : ack.Status == HelloStatus.TooManyClients ? ErrorCode.TooManyClients : ErrorCode.AuthenticationFailed;
                    throw new SimpleTransException(code, "服务端拒绝连接：" + (ack.Message ?? ack.Status.ToString()));
                }

                SessionId = ack.SessionId;
                _negotiatedChunkSize = Math.Max(
                    ProtocolLimits.MinChunkSize,
                    Math.Min(Math.Min(_options.RequestedChunkSize, ack.ChunkSize), ProtocolLimits.MaxChunkSize));
            }
        }

        // ---------------- 清单 ----------------

        public async Task<Manifest> GetManifestAsync(CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            await _writer!.WriteEmptyAsync(MessageType.ManifestRequest, cancellationToken).ConfigureAwait(false);

            Frame? frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            using (frame)
            {
                if (frame == null)
                    throw new SimpleTransException(ErrorCode.ConnectionClosed, "服务端在返回清单前关闭了连接");
                if (frame.Type == MessageType.Error)
                {
                    ErrorMessage error = MessageCodec.DecodeError(frame.PayloadSpan);
                    throw new SimpleTransException(error.Code, "服务端返回错误：" + error.Message);
                }
                if (frame.Type != MessageType.ManifestResponse)
                    throw new SimpleTransException(ErrorCode.MalformedMessage, "期望 ManifestResponse，实际收到 " + frame.Type.ToString());

                MessageCodec.DecodeManifestResponse(frame.PayloadSpan, out Guid batchId, out IReadOnlyList<FileManifestEntry> files);
                _manifest = new Manifest(batchId, files);
                return _manifest;
            }
        }

        // ---------------- 接收 ----------------

        public async Task<ReceiveResult> ReceiveAsync(
            IReceiveSink sink,
            ReceiveOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            if (sink == null) throw new ArgumentNullException(nameof(sink));

            ReceiveOptions receiveOptions = options ?? ReceiveOptions.All;
            Manifest manifest = _manifest ?? await GetManifestAsync(cancellationToken).ConfigureAwait(false);
            IReceiveStateStore? store = receiveOptions.Resume ? _options.ReceiveStateStore : null;
            _callbackContext = receiveOptions.CallbackContext ?? _options.CallbackContext;

            // 1) 选定子集并计算续传起点
            var selection = new List<FileManifestEntry>();
            var resumePoints = new List<FileResumePoint>();
            int skipped = 0;

            for (int i = 0; i < manifest.Files.Count; i++)
            {
                FileManifestEntry entry = manifest.Files[i];
                if (receiveOptions.FileIds != null && receiveOptions.FileIds.Count > 0 && !Contains(receiveOptions.FileIds, entry.FileId))
                    continue;

                long offset = 0;
                if (store != null)
                {
                    offset = await store.GetOffsetAsync(entry.FileId, cancellationToken).ConfigureAwait(false);
                    if (offset < 0 || offset > entry.Length) offset = 0;
                }

                if (offset >= entry.Length && entry.Length > 0)
                {
                    skipped++;
                    continue;
                }

                selection.Add(entry);
                resumePoints.Add(new FileResumePoint(entry.FileId, offset));
            }

            var errors = new List<FileReceiveError>();
            int completed = 0;
            long transferredBytes = 0;

            if (selection.Count == 0)
            {
                return new ReceiveResult(manifest.BatchId, 0, 0, 0, skipped, 0, errors);
            }

            // 2) 发起传输
            ushort chunkSize = (ushort)Math.Max(ProtocolLimits.MinChunkSize, Math.Min(_negotiatedChunkSize, ushort.MaxValue));
            await _writer!.WriteAsync(
                MessageType.StreamRequest,
                MessageCodec.EncodeStreamRequest(new StreamRequestMessage(chunkSize, resumePoints)),
                cancellationToken).ConfigureAwait(false);

            // 3) 接收循环
            FileManifestEntry? current = null;
            long expectedOffset = 0;
            IncrementalHash? hash = null;
            bool hashEnabled = false;

            try
            {
                while (true)
                {
                    Frame? frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                    if (frame == null)
                        throw new SimpleTransException(ErrorCode.ConnectionClosed, "服务端在传输完成前关闭了连接");

                    using (frame)
                    {
                        switch (frame.Type)
                        {
                            case MessageType.FileStart:
                                {
                                    FileStartMessage start = MessageCodec.DecodeFileStart(frame.PayloadSpan);
                                    current = manifest.Find(start.FileId);
                                    if (current == null)
                                        throw new SimpleTransException(ErrorCode.MalformedMessage, "服务端发来清单外的文件");

                                    expectedOffset = start.Offset;
                                    hashEnabled = receiveOptions.VerifySha256 && current.HasSha256 && start.Offset == 0;
                                    hash = hashEnabled ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                                    await DispatchAsync(() => sink.OnFileStartAsync(current, start.Offset, cancellationToken)).ConfigureAwait(false);
                                    break;
                                }

                            case MessageType.FileData:
                                {
                                    if (current == null)
                                        throw new SimpleTransException(ErrorCode.MalformedMessage, "未收到 FileStart 就收到 FileData");

                                    (Guid fileId, long offset, int dataLength) = MessageCodec.DecodeFileDataHeader(frame.PayloadSpan);
                                    if (fileId != current.FileId)
                                        throw new SimpleTransException(ErrorCode.MalformedMessage, "FileData 的文件与当前文件不一致");
                                    if (offset != expectedOffset)
                                        throw new SimpleTransException(ErrorCode.MalformedMessage, "FileData 偏移不连续");

                                    if (hash != null) AppendHash(hash, frame, dataLength);

                                    FileManifestEntry entry = current;
                                    long chunkOffset = offset;
                                    // 数据一定紧跟在 FileData 头之后，这里切出对应视图（零拷贝）。
                                    ReadOnlyMemory<byte> memory = frame.Payload.Slice(MessageCodec.FileDataHeaderSize, dataLength);

                                    await DispatchAsync(() => sink.OnChunkAsync(entry, chunkOffset, memory, cancellationToken)).ConfigureAwait(false);

                                    expectedOffset += dataLength;
                                    transferredBytes += dataLength;

                                    if (store != null)
                                    {
                                        await store.SetOffsetAsync(entry.FileId, expectedOffset, cancellationToken).ConfigureAwait(false);
                                    }
                                    break;
                                }

                            case MessageType.FileEnd:
                                {
                                    FileEndMessage end = MessageCodec.DecodeFileEnd(frame.PayloadSpan);
                                    FileManifestEntry? entry = current;
                                    if (entry == null || end.FileId != entry.FileId)
                                        throw new SimpleTransException(ErrorCode.MalformedMessage, "FileEnd 的文件与当前文件不一致");

                                    bool ok = end.Status == TransferStatus.Ok;
                                    Exception? failure = null;

                                    if (ok && hashEnabled)
                                    {
                                        byte[] actual = hash!.GetHashAndReset();
                                        byte[]? expected = entry.Sha256;
                                        if (expected != null && !ByteEquals(actual, expected))
                                        {
                                            ok = false;
                                            failure = new SimpleTransException(ErrorCode.ChecksumMismatch, "文件 SHA-256 校验失败：" + entry.Name);
                                        }
                                    }
                                    hash?.Dispose();
                                    hash = null;

                                    if (ok)
                                    {
                                        await DispatchAsync(() => sink.OnFileCompleteAsync(entry, cancellationToken)).ConfigureAwait(false);
                                        completed++;
                                        if (store != null)
                                        {
                                            await store.SetOffsetAsync(entry.FileId, entry.Length, cancellationToken).ConfigureAwait(false);
                                        }
                                    }
                                    else
                                    {
                                        failure ??= new SimpleTransException(ErrorCode.InternalError, "服务端未能完成该文件：" + entry.Name);
                                        errors.Add(new FileReceiveError(entry, failure));
                                        await DispatchAsync(() => sink.OnFileFailedAsync(entry, failure, cancellationToken)).ConfigureAwait(false);
                                    }

                                    current = null;
                                    break;
                                }

                            case MessageType.BatchEnd:
                                {
                                    BatchEndMessage end = MessageCodec.DecodeBatchEnd(frame.PayloadSpan);
                                    return new ReceiveResult(manifest.BatchId, selection.Count, completed, errors.Count, skipped, transferredBytes, errors);
                                }

                            case MessageType.Heartbeat:
                                {
                                    if (MessageCodec.DecodeHeartbeat(frame.PayloadSpan) == HeartbeatKind.Ping)
                                    {
                                        await _writer.WriteAsync(MessageType.Heartbeat,
                                            MessageCodec.EncodeHeartbeat(HeartbeatKind.Ack),
                                            cancellationToken).ConfigureAwait(false);
                                    }
                                    break;
                                }

                            case MessageType.Error:
                                {
                                    ErrorMessage error = MessageCodec.DecodeError(frame.PayloadSpan);
                                    throw new SimpleTransException(error.Code, "服务端返回错误：" + error.Message);
                                }

                            case MessageType.Cancel:
                                {
                                    CancelReason reason = MessageCodec.DecodeCancel(frame.PayloadSpan);
                                    throw new SimpleTransException(ErrorCode.Cancelled, "服务端取消了传输：" + reason.ToString());
                                }

                            default:
                                throw new SimpleTransException(ErrorCode.UnsupportedMessage,
                                    "收到不支持的消息类型: 0x" + ((byte)frame.Type).ToString("X2"));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await NotifyCurrentFileFailedAsync(sink, current, new SimpleTransException(ErrorCode.Cancelled, "接收已被取消")).ConfigureAwait(false);
                await TrySendCancelAsync(CancelReason.UserCancelled).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await NotifyCurrentFileFailedAsync(sink, current, ex).ConfigureAwait(false);
                throw;
            }
            finally
            {
                hash?.Dispose();
            }
        }

        /// <summary>接收中断时，把「当前文件未完成」告知 sink，使其能关闭句柄、保留半成品用于续传。</summary>
        private async ValueTask NotifyCurrentFileFailedAsync(IReceiveSink sink, FileManifestEntry? current, Exception error)
        {
            if (current == null) return;
            try
            {
                await DispatchAsync(() => sink.OnFileFailedAsync(current, error, CancellationToken.None)).ConfigureAwait(false);
            }
            catch
            {
                // 收尾通知失败不得覆盖原始异常。
            }
        }

        /// <summary>主动请求取消当前传输（服务端会停止发送）。</summary>
        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected) return;
            await _writer!.WriteAsync(MessageType.Cancel, MessageCodec.EncodeCancel(CancelReason.UserCancelled), cancellationToken).ConfigureAwait(false);
        }

        private async Task TrySendCancelAsync(CancelReason reason)
        {
            if (_writer == null || !IsConnected) return;
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    await _writer.WriteAsync(MessageType.Cancel, MessageCodec.EncodeCancel(reason), timeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // 取消失败不应掩盖原始的取消异常。
            }
        }

        // ---------------- 心跳与读取 ----------------

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                    if (_writer == null) return;
                    await _writer.WriteAsync(MessageType.Heartbeat,
                        MessageCodec.EncodeHeartbeat(HeartbeatKind.Ping), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        }

        private async ValueTask<Frame?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token))
            {
                timeout.CancelAfter(_options.ReadTimeout);
                try
                {
                    return await _reader!.ReadFrameAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_cts.IsCancellationRequested)
                {
                    throw new SimpleTransException(ErrorCode.Timeout, "读取超时：对端在 " + _options.ReadTimeout.TotalSeconds.ToString("0") + " 秒内没有发送任何数据");
                }
            }
        }

        /// <summary>把 sink 回调派发到指定线程上下文；未设置上下文时在 IO 线程零拷贝直接调用。</summary>
        private async ValueTask DispatchAsync(Func<ValueTask> callback)
        {
            SynchronizationContext? context = _callbackContext;
            if (context == null)
            {
                await callback().ConfigureAwait(false);
                return;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(_ =>
            {
                try
                {
                    callback().AsTask().ContinueWith(
                        t => { if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions); else tcs.TrySetResult(true); },
                        TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);

            await tcs.Task.ConfigureAwait(false);
        }

        // ---------------- 关闭 ----------------

        private static bool Contains(IReadOnlyList<Guid> list, Guid value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value) return true;
            }
            return false;
        }

        private static bool ByteEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            int diff = 0;
            for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }

        /// <summary>把 FileData 中的数据追加进增量哈希（独立同步方法，避免在 async 方法里使用 ref struct）。</summary>
        private static void AppendHash(IncrementalHash hash, Frame frame, int dataLength)
        {
            hash.AppendData(frame.PayloadSpan.Slice(MessageCodec.FileDataHeaderSize, dataLength));
        }

        private void EnsureConnected()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DistributionClient));
            if (!IsConnected) throw new SimpleTransException(ErrorCode.NotConnected, "尚未连接，请先调用 ConnectAsync");
        }

        private void CleanupConnection()
        {
            IsConnected = false;
            _reader?.Dispose();
            _reader = null;
            _writer?.Dispose();
            _writer = null;
            try
            {
                _tcp?.Close();
            }
            catch (SocketException)
            {
            }
            _tcp = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsConnected = false;

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Task? heartbeat = _heartbeat;
            if (heartbeat != null)
            {
                try
                {
                    heartbeat.Wait(TimeSpan.FromSeconds(1));
                }
                catch
                {
                    // 忽略收尾异常。
                }
            }

            CleanupConnection();
            _cts.Dispose();
        }

        /// <summary>
        /// 异步释放（Unity 在 Domain Reload / 场景卸载时应当 await 本方法，
        /// 确保 socket 被真正关闭，避免句柄泄漏）。
        /// </summary>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
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
    }
}