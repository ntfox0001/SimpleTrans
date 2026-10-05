#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SimpleTrans.Protocol;

namespace SimpleTrans.Server
{
    /// <summary>
    /// 单个客户端会话：独立 Task、独立状态机、独立缓冲与 CancellationToken。
    /// 一个客户端断开或卡死不会影响其它客户端。
    /// </summary>
    internal sealed class ClientSession : IDisposable
    {
        private readonly FileDistributionServer _server;
        private readonly ServerOptions _options;
        private readonly TcpClient _tcp;
        private readonly CancellationTokenSource _cts;
        private readonly FrameReader _reader;
        private readonly FrameWriter _writer;
        private readonly byte[] _chunkBuffer;
        private Task? _transferTask;
        private int _transferRunning;
        private bool _registered;
        private bool _disposed;

        public ClientSession(FileDistributionServer server, ServerOptions options, TcpClient tcp, CancellationToken serverToken)
        {
            _server = server;
            _options = options;
            _tcp = tcp;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            RemoteEndPoint = SafeRemoteEndPoint(tcp);

            try
            {
                tcp.Client.NoDelay = true;
            }
            catch (SocketException)
            {
                // 某些平台不支持设置 NoDelay，忽略。
            }

            Stream stream = tcp.GetStream();
            _reader = new FrameReader(stream, options.MaxFramePayloadBytes, options.FrameTransformer);
            _writer = new FrameWriter(stream, options.MaxFramePayloadBytes, options.FrameTransformer);
            _chunkBuffer = ArrayPool<byte>.Shared.Rent(options.ChunkSize);
        }

        public Guid SessionId { get; } = Guid.NewGuid();

        public string RemoteEndPoint { get; }

        public Task? TransferTask => _transferTask;

        private static string SafeRemoteEndPoint(TcpClient tcp)
        {
            try
            {
                return tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch (SocketException)
            {
                return "unknown";
            }
        }

        public async Task RunAsync()
        {
            try
            {
                await HandshakeAsync().ConfigureAwait(false);

                if (!_server.TryRegisterClient())
                {
                    await SendHelloAckAsync(HelloStatus.TooManyClients, "并发客户端数已达上限").ConfigureAwait(false);
                    return;
                }
                _registered = true;

                await SendHelloAckAsync(HelloStatus.Ok, null).ConfigureAwait(false);
                _server.RaiseClientConnected(SessionId, RemoteEndPoint);

                Task heartbeat = HeartbeatLoopAsync(_cts.Token);
                await ReadLoopAsync().ConfigureAwait(false);
                await AwaitQuietlyAsync(heartbeat).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常关闭路径。
            }
            catch (SimpleTransException ex)
            {
                _server.RaiseClientFailed(SessionId, ex);
            }
            catch (IOException ex)
            {
                _server.RaiseClientFailed(SessionId, new SimpleTransException(ErrorCode.ConnectionClosed, "连接中断: " + ex.Message, ex));
            }
            catch (SocketException ex)
            {
                _server.RaiseClientFailed(SessionId, new SimpleTransException(ErrorCode.ConnectionClosed, "连接中断: " + ex.Message, ex));
            }
            catch (Exception ex)
            {
                _server.RaiseClientFailed(SessionId, ex);
            }
            finally
            {
                await CloseAsync().ConfigureAwait(false);
                Dispose();
            }
        }

        // ---------------- 握手 ----------------

        private async Task HandshakeAsync()
        {
            Frame? frame = await ReadFrameAsync().ConfigureAwait(false);
            using (frame)
            {
                if (frame == null)
                    throw new SimpleTransException(ErrorCode.ConnectionClosed, "对端在握手前关闭连接");
                if (frame.Type != MessageType.Hello)
                    throw new SimpleTransException(ErrorCode.MalformedMessage, "握手阶段收到非 Hello 消息");

                HelloMessage hello = MessageCodec.DecodeHello(frame.PayloadSpan);
                if (hello.Version != ProtocolLimits.ProtocolVersion)
                {
                    await SendHelloAckAsync(HelloStatus.VersionMismatch, "协议版本不匹配").ConfigureAwait(false);
                    throw new SimpleTransException(ErrorCode.ProtocolVersionMismatch,
                        "协议版本不匹配：客户端 " + hello.Version.ToString() + "，服务端 " + ProtocolLimits.ProtocolVersion.ToString());
                }

                if (_options.Authenticator != null)
                {
                    AuthResult result = await _options.Authenticator
                        .ValidateAsync(hello.AuthPayload, _cts.Token)
                        .ConfigureAwait(false);
                    if (!result.Success)
                    {
                        await SendHelloAckAsync(HelloStatus.Rejected, result.Message).ConfigureAwait(false);
                        throw new SimpleTransException(ErrorCode.AuthenticationFailed, result.Message ?? "认证失败");
                    }
                }
            }
        }

        private async Task SendHelloAckAsync(HelloStatus status, string? message)
        {
            int chunkSize = Math.Min(_options.ChunkSize, ProtocolLimits.MaxChunkSize);
            var ack = new HelloAckMessage(
                ProtocolLimits.ProtocolVersion,
                status,
                (ushort)chunkSize,
                (ushort)Math.Max(1, (int)_options.HeartbeatInterval.TotalSeconds),
                SessionId,
                message);

            await _writer.WriteAsync(MessageType.HelloAck, MessageCodec.EncodeHelloAck(ack), _cts.Token).ConfigureAwait(false);
        }

        // ---------------- 主读循环 ----------------

        private async Task ReadLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Frame? frame = await ReadFrameAsync().ConfigureAwait(false);
                using (frame)
                {
                    if (frame == null) return; // 对端正常关闭

                    switch (frame.Type)
                    {
                        case MessageType.ManifestRequest:
                            await SendManifestAsync().ConfigureAwait(false);
                            break;

                        case MessageType.StreamRequest:
                            StartTransfer(frame.PayloadSpan);
                            break;

                        case MessageType.Heartbeat:
                            {
                                HeartbeatKind kind = MessageCodec.DecodeHeartbeat(frame.PayloadSpan);
                                if (kind == HeartbeatKind.Ping)
                                {
                                    await _writer.WriteAsync(
                                        MessageType.Heartbeat,
                                        MessageCodec.EncodeHeartbeat(HeartbeatKind.Ack),
                                        _cts.Token).ConfigureAwait(false);
                                }
                                break;
                            }

                        case MessageType.Cancel:
                            {
                                CancelReason reason = MessageCodec.DecodeCancel(frame.PayloadSpan);
                                _server.RaiseClientFailed(SessionId,
                                    new SimpleTransException(ErrorCode.Cancelled, "客户端取消：" + reason.ToString()));
                                return;
                            }

                        case MessageType.Error:
                            {
                                ErrorMessage error = MessageCodec.DecodeError(frame.PayloadSpan);
                                _server.RaiseClientFailed(SessionId,
                                    new SimpleTransException(error.Code, "客户端报错：" + error.Message));
                                return;
                            }

                        default:
                            throw new SimpleTransException(ErrorCode.UnsupportedMessage,
                                "不支持的消息类型: 0x" + ((byte)frame.Type).ToString("X2"));
                    }
                }
            }
        }

        private async Task SendManifestAsync()
        {
            Manifest? manifest = _server.CurrentManifest;
            byte[] payload = manifest == null
                ? MessageCodec.EncodeManifestResponse(Guid.Empty, Array.Empty<FileManifestEntry>())
                : MessageCodec.EncodeManifestResponse(manifest.BatchId, manifest.Files);

            await _writer.WriteAsync(MessageType.ManifestResponse, payload, _cts.Token).ConfigureAwait(false);
        }

        private void StartTransfer(ReadOnlySpan<byte> payload)
        {
            if (Interlocked.CompareExchange(ref _transferRunning, 1, 0) != 0)
                throw new SimpleTransException(ErrorCode.InvalidState, "该连接已有正在进行的传输");

            MessageCodec.DecodeStreamRequest(payload, out StreamRequestMessage request);
            _transferTask = Task.Run(() => RunTransferAsync(request));
        }

        private async Task RunTransferAsync(StreamRequestMessage request)
        {
            int failed = 0;
            CancellationToken ct = _cts.Token;
            _server.NotifyTransferStarted();

            try
            {
                int chunkSize = request.ChunkSize <= 0 ? _options.ChunkSize : Math.Min(request.ChunkSize, _options.ChunkSize);
                chunkSize = Math.Max(chunkSize, ProtocolLimits.MinChunkSize);
                if (chunkSize > _chunkBuffer.Length) chunkSize = _chunkBuffer.Length;

                Manifest? manifest = _server.CurrentManifest;

                for (int i = 0; i < request.Files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    FileResumePoint point = request.Files[i];

                    FileManifestEntry? entry = manifest?.Find(point.FileId);
                    IFileSource? source = _server.FindSource(point.FileId);

                    if (entry == null || source == null)
                    {
                        failed++;
                        var missing = new SimpleTransException(ErrorCode.FileNotFound, "文件不在当前批次中: " + point.FileId.ToString());
                        await _writer.WriteAsync(MessageType.FileEnd,
                            MessageCodec.EncodeFileEnd(new FileEndMessage(point.FileId, 0, TransferStatus.Failed, null)),
                            ct).ConfigureAwait(false);
                        _server.RaiseFileCompleted(SessionId, point.FileId, entry?.Name ?? point.FileId.ToString(), 0, false, missing);
                        continue;
                    }

                    long total = source.Length;
                    long offset = Math.Min(Math.Max(point.Offset, 0), total);

                    await _writer.WriteAsync(MessageType.FileStart,
                        MessageCodec.EncodeFileStart(new FileStartMessage(entry.FileId, offset, total)),
                        ct).ConfigureAwait(false);

                    long position = offset;
                    Exception? failure = null;

                    while (position < total)
                    {
                        int want = (int)Math.Min(chunkSize, total - position);
                        int read;
                        try
                        {
                            read = await source.ReadAsync(position, _chunkBuffer.AsMemory(0, want), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            break;
                        }

                        if (read <= 0)
                        {
                            failure = new SimpleTransException(ErrorCode.FileSourceUnavailable, "文件源提前结束");
                            break;
                        }

                        await _writer.WriteFileDataAsync(entry.FileId, position, _chunkBuffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        position += read;
                        _server.RaiseClientProgress(SessionId, new TransferProgress(entry.FileId, entry.Name, position, total));
                    }

                    if (failure == null && position == total)
                    {
                        await _writer.WriteAsync(MessageType.FileEnd,
                            MessageCodec.EncodeFileEnd(new FileEndMessage(entry.FileId, total, TransferStatus.Ok, entry.Sha256)),
                            ct).ConfigureAwait(false);
                        _server.RaiseFileCompleted(SessionId, entry.FileId, entry.Name, total, true, null);
                    }
                    else
                    {
                        failed++;
                        await _writer.WriteAsync(MessageType.FileEnd,
                            MessageCodec.EncodeFileEnd(new FileEndMessage(entry.FileId, position, TransferStatus.Failed, null)),
                            ct).ConfigureAwait(false);
                        _server.RaiseFileCompleted(SessionId, entry.FileId, entry.Name, position, false,
                            failure ?? new SimpleTransException(ErrorCode.InternalError, "传输未完成"));
                    }
                }

                await _writer.WriteAsync(MessageType.BatchEnd,
                    MessageCodec.EncodeBatchEnd(new BatchEndMessage(failed == 0 ? TransferStatus.Ok : TransferStatus.Failed, request.Files.Count)),
                    ct).ConfigureAwait(false);

                _server.RaiseClientCompleted(SessionId, request.Files.Count, failed);
            }
            catch (OperationCanceledException)
            {
                // 会话被取消。
            }
            catch (Exception ex)
            {
                _server.RaiseClientFailed(SessionId, ex);
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            finally
            {
                Interlocked.Exchange(ref _transferRunning, 0);
                _server.NotifyTransferFinished();
            }
        }

        // ---------------- 心跳与读取 ----------------

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false);
                    await _writer.WriteAsync(MessageType.Heartbeat,
                        MessageCodec.EncodeHeartbeat(HeartbeatKind.Ping), ct).ConfigureAwait(false);
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

        /// <summary>带回读超时的帧读取：超过 IdleTimeout 无任何输入即判定半开连接。</summary>
        private async ValueTask<Frame?> ReadFrameAsync()
        {
            CancellationToken outer = _cts.Token;
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(outer))
            {
                timeout.CancelAfter(_options.IdleTimeout);
                try
                {
                    return await _reader.ReadFrameAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!outer.IsCancellationRequested)
                {
                    throw new SimpleTransException(ErrorCode.Timeout, "客户端空闲超时");
                }
            }
        }

        private static async Task AwaitQuietlyAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // 收尾阶段的异常不再上抛。
            }
        }

        private async Task CloseAsync()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Task? transfer = _transferTask;
            if (transfer != null) await AwaitQuietlyAsync(transfer).ConfigureAwait(false);

            if (_registered)
            {
                _registered = false;
                _server.UnregisterClient();
            }
            _server.UnregisterSession(this);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _reader.Dispose();
            _writer.Dispose();
            try
            {
                _tcp.Close();
            }
            catch (SocketException)
            {
            }

            ArrayPool<byte>.Shared.Return(_chunkBuffer);
            _cts.Dispose();
        }
    }
}