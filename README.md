# SimpleTrans

跨平台批量文件分发库。裸 TCP + 纯二进制私有协议，一对多广播，断点续传，**零第三方依赖**。

发送端（服务端）与接收端（客户端）都包含在本库中，可直接用于 **Android / iOS / Windows** 以及 **Unity**。

```text
服务端 ──发布一批文件──▶ 清单（Manifest）
                          ▲
        客户端 A ── 拉清单 ─┘── 从偏移 0 请求 ──▶ 落盘
        客户端 B ── 拉清单 ─┘── 从偏移 N 请求 ──▶ 续传落盘
        客户端 C ── 拉清单 ─┘── 按需挑选子集
```

## 特性

- **一对多广播**：多个客户端可同时从同一批文件源并发拉取，互不阻塞。
- **断点续传**：接收进度按文件偏移记录，进程重启、网络中断后从断点继续，不重传已完成部分。
- **偏移量寻址的文件源抽象**：服务端不暴露 `Stream`，而是按 `offset` 读取，天然支持并发与续传。
- **可插拔落地**：接收端把数据写到哪（磁盘 / 内存 / 自定义存储）由使用方决定。
- **完整性校验**：每帧 CRC32；文件可再做 SHA-256 校验。
- **低门槛接入**：无设备发现、无配对、无鉴权，IP 与端口由宿主应用自行传递。
- **Unity 友好**：netstandard2.1 源码包，不使用反射 / `dynamic` / `Reflection.Emit`，兼容 IL2CPP。

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 运行时目标 | `.NET Standard 2.1` |
| 语言版本 | C# 9.0（源码包由 Unity 编译；DLL 为预编译产物） |
| .NET SDK（构建 DLL 时） | 8.0 或更高 |
| Unity | 2021.3 及以上 |

> **WebGL 不支持**：WebGL 平台无法使用原始 TCP socket。库在 `UNITY_WEBGL` 下会直接抛出 `PlatformNotSupportedException`。

## 安装

两种方式**只能选其一**，同时使用会出现程序集同名冲突。

### 方式一：UPM 源码包（推荐用于 Unity）

在 Unity 中打开 `Window → Package Manager → + → Add package from git URL`，填入：

```text
https://github.com/ntfox0001/SimpleTrans.git
```

包名 `com.simpletrans.distribution`，根目录即包根，源码会按 `src/*` 下的 `.asmdef` 编译为 4 个程序集：

```text
SimpleTrans.Core ← SimpleTrans.Protocol ← SimpleTrans.Server
                                        ← SimpleTrans.Client
```

接入示例见 [`samples~`](samples~/SimpleTrans.Demo)（该目录以 `~` 结尾，Unity 会忽略，不会参与编译）。

### 方式二：Release DLL

在仓库根目录运行 [`build.bat`](build.bat)，产物输出到 `dist\SimpleTrans\`：

```text
SimpleTrans.Core.dll
SimpleTrans.Protocol.dll
SimpleTrans.Server.dll
SimpleTrans.Client.dll
（含同名 .pdb 与 link.xml）
```

把整个 `dist\SimpleTrans` 文件夹复制到 Unity 工程的 `Assets\Plugins\` 下即可。

> `dist/` 已在 [.gitignore](.gitignore) 中排除，仓库不提交构建产物，请自行构建。
> Player Settings 中 `Api Compatibility Level` 需为 `.NET Standard 2.1`（Unity 2021.3+ 默认值）。

## 快速开始

下面的代码均来自可直接运行的 [`samples~/SimpleTrans.Demo`](samples~/SimpleTrans.Demo)，在仓库根目录执行 `dotnet run --project "samples~\SimpleTrans.Demo"` 即可看到端到端联调全过程。

### 服务端

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleTrans;
using SimpleTrans.Server;
using SimpleTrans.Sources;

var server = new FileDistributionServer(new ServerOptions
{
    BindAddress = "0.0.0.0",   // 指定 127.0.0.1 可避免 Windows 防火墙弹窗
    Port = 9000,
    ChunkSize = 256 * 1024,
});

server.ClientConnected += (_, e) => Console.WriteLine("客户端接入: " + e.RemoteEndPoint);
server.ClientCompleted += (_, e) => Console.WriteLine($"会话完成 文件 {e.FileCount}，失败 {e.FailedCount}");
server.ClientFailed    += (_, e) => Console.WriteLine("会话异常: " + e.Error.Message);

await server.StartAsync();

// 方式 A：直接发布一组磁盘路径
await server.PublishAsync(new[] { @"C:\data\a.bin", @"C:\data\b.bin" });

// 方式 B：显式构造文件源，可自定义逻辑名、预置 SHA-256、或使用内存源
var sources = new List<IFileSource>
{
    new MemorySource("config.json", System.Text.Encoding.UTF8.GetBytes("{}")),
    new FilePathSource(@"C:\data\a.bin", name: "a.bin", knownSha256: sha256Bytes),
};
await server.PublishAsync(sources);

// 等所有客户端传完，再优雅停止
await server.WaitForCompletionAsync();
await server.StopAsync();
```

`Port = 0` 表示由系统分配端口，启动后通过 `server.Port` 读取实际端口。

### 客户端

```csharp
using System;
using System.Threading;
using SimpleTrans;
using SimpleTrans.Client;

var options = new ClientOptions
{
    ReceiveStateStore = store,                        // 需要断点续传时提供，null 则每次从 0 开始
    CallbackContext = SynchronizationContext.Current, // Unity 主线程；不设则在 IO 线程回调
};

using var client = new DistributionClient(options);
await client.ConnectAsync("192.168.1.10", 9000);

Manifest manifest = await client.GetManifestAsync();
Console.WriteLine($"本批共 {manifest.Files.Count} 个文件，合计 {manifest.TotalLength} 字节");

var result = await client.ReceiveAsync(new MySink(@"C:\download"));
Console.WriteLine($"完成 {result.CompletedCount}，失败 {result.FailedCount}，实际传输 {result.TransferredBytes} 字节");
```

只接收清单中的部分文件：

```csharp
var only = new ReceiveOptions
{
    FileIds = new[] { manifest.Files[0].FileId },
    Resume = true,
    VerifySha256 = true,
};
await client.ReceiveAsync(sink, only, cancellationToken);
```

### 实现一个接收落地 `IReceiveSink`

```csharp
sealed class MySink : IReceiveSink
{
    private readonly string _dir;
    private readonly Dictionary<Guid, FileStream> _open = new();

    public MySink(string dir) { _dir = dir; Directory.CreateDirectory(dir); }

    public ValueTask OnFileStartAsync(FileManifestEntry file, long resumeOffset, CancellationToken ct)
    {
        var stream = new FileStream(Path.Combine(_dir, file.Name),
            resumeOffset > 0 ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write, FileShare.None);
        stream.SetLength(resumeOffset);           // 截断到续传点，保证内容一致
        stream.Seek(resumeOffset, SeekOrigin.Begin);
        _open[file.FileId] = stream;
        return default;
    }

    public async ValueTask OnChunkAsync(FileManifestEntry file, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        => await _open[file.FileId].WriteAsync(data, ct).ConfigureAwait(false);

    public async ValueTask OnFileCompleteAsync(FileManifestEntry file, CancellationToken ct)
    {
        var stream = _open[file.FileId];
        _open.Remove(file.FileId);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Dispose();
    }

    public ValueTask OnFileFailedAsync(FileManifestEntry file, Exception error, CancellationToken ct)
    {
        if (_open.Remove(file.FileId, out var stream)) stream.Dispose();
        return default;   // 保留半成品文件即可用于下次续传
    }
}
```

约束：

- 所有方法都是异步的，库会 **await 返回值后才回收内部缓冲**。若要把 `data` 留到返回之后使用，必须自行拷贝。
- 同一个 sink 实例只会被单个客户端顺序调用，不会重入。
- 传输被取消或中断时，库会调用 `OnFileFailedAsync`，实现方应在此关闭句柄——否则续传时会因文件被占用而打不开。

### 实现断点续传状态 `IReceiveStateStore`

```csharp
sealed class MyStateStore : IReceiveStateStore
{
    public ValueTask<long> GetOffsetAsync(Guid fileId, CancellationToken ct) { /* 读持久化记录，无则 0 */ }
    public ValueTask SetOffsetAsync(Guid fileId, long offset, CancellationToken ct) { /* 写持久化记录 */ }
}
```

实现必须线程安全，并自行持久化（写入文件 / PlayerPrefs / SQLite 皆可），否则无法跨启动续传。

## Unity 使用要点

1. **不要在 UI / Unity 主线程上对返回的 `Task` 使用 `.Result` / `.Wait()`**，会死锁。一律 `await`。
2. 希望 sink 回调运行在主线程时，设置 `ClientOptions.CallbackContext`（通常传 `SynchronizationContext.Current`，在 Unity 主线程上取值）。不设置则在 IO 线程零拷贝直接回调，此时**实现里不可触碰任何 Unity API**。
3. 场景卸载 / `OnDestroy` 时 `await client.DisposeAsync()`（或 `await server.DisposeAsync()`），确保 socket 真正关闭。`DistributionClient` 同时实现 `IDisposable` 与 `IAsyncDisposable`。
4. 库不使用反射、`dynamic`、`Reflection.Emit`，IL2CPP 剥离下安全；根目录的 [link.xml](link.xml) 仅作兜底，体积敏感时可自行删改。
5. `.asmdef` 均设置 `noEngineReferences: true`，不依赖 `UnityEngine`，可在纯 .NET 世界中复用。

## 核心概念

### FileId

`FileId = SHA256(name ‖ contentSha256)` 的前 16 字节。内容摘要未知时退化为 `SHA256(name ‖ "len:" ‖ length)`。

它跨服务端重启保持稳定，因此客户端的续传记录可以长期复用。但也意味着：**同一逻辑名的文件内容变了（且服务端未提供 SHA-256）时，FileId 不变**，续传记录会错判。要避免这一点，请在构造文件源时传入 `knownSha256`。

### 文件源 `IFileSource`

以偏移量而非 `Stream` 暴露数据，因为一对多分发时多个客户端会各自从不同偏移读取，`Stream` 的共享游标会成为问题。实现必须线程安全。

| 实现 | 适用场景 |
| --- | --- |
| [`FilePathSource`](src/SimpleTrans.Core/Sources/FilePathSource.cs) | 磁盘文件，共享读句柄，`Stream` 定位+读取用信号量串行化 |
| [`MemorySource`](src/SimpleTrans.Core/Sources/MemorySource.cs) | 小文件、内存中生成的内容 |
| [`StreamFileSource`](src/SimpleTrans.Core/Sources/StreamFileSource.cs) | 可 `Seek` 的流（AssetBundle、`MemoryStream` 等） |
| [`SpoolFileSource`](src/SimpleTrans.Core/Sources/SpoolFileSource.cs) | 不可 `Seek` 的流，创建时先落临时文件 |

```csharp
var spool = await SpoolFileSource.CreateAsync("level.unity3d", networkStream, tempDirectory: Application.temporaryCachePath);
```

## 选项速览

`ServerOptions`：`Port`、`BindAddress`、`ChunkSize`、`MaxConcurrentClients`、`MaxFramePayloadBytes`、`HeartbeatInterval`、`IdleTimeout`、`Backlog`、`Authenticator`、`FrameTransformer`。

`ClientOptions`：`MaxFramePayloadBytes`、`RequestedChunkSize`、`HeartbeatInterval`、`ReadTimeout`、`ConnectTimeout`、`CallbackContext`、`ReceiveStateStore`、`Authenticator`、`FrameTransformer`。

`ReceiveOptions`：`FileIds`（只收指定文件）、`Resume`、`VerifySha256`、`CallbackContext`。

分片大小取「客户端 `RequestedChunkSize`」与「服务端 `ChunkSize`」的较小值，允许范围 4KB ~ 4MB。单个帧 payload 上限默认 8MB。

## 协议

```text
+---------+--------------+------------------+----------+
| 1B 类型 | 4B 负载长度  | 负载（payload）  | 4B CRC32 |
+---------+--------------+------------------+----------+
             大端序                           大端序
```

- CRC32 覆盖 `类型 + 长度 + 负载`（IEEE 802.3，反射多项式 `0xEDB88320`）。
- 负载长度在分配内存**之前**即被硬上限拦截，畸形输入不会撑爆内存。
- 消息类型：`Hello` / `HelloAck` / `ManifestRequest` / `ManifestResponse` / `StreamRequest` / `FileStart` / `FileData` / `FileEnd` / `BatchEnd` / `Cancel` / `Heartbeat` / `Error`。
- 双方协议版本不一致时直接拒绝并断开。

## 安全说明

**本库不做设备发现、不做配对、不内置鉴权与加密。** 任何能连上服务端端口的人都能取走文件。需要保护时：

- 实现 [`IAuthenticator`](src/SimpleTrans.Core/ProtocolLimits.cs)（如 HMAC 挑战应答），或
- 实现 [`IFrameTransformer`](src/SimpleTrans.Core/ProtocolLimits.cs) 对整个 payload 做加密 / 混淆（**必须长度不变**），或
- 在网络层（私网 / VPN）兜底。

## 目录结构

```text
SimpleTrans/
├─ package.json                 UPM 包清单
├─ link.xml                     IL2CPP 剥离兜底
├─ build.bat                    构建 Unity 可用的 Release DLL
├─ src/
│  ├─ SimpleTrans.Core/         模型、选项、文件源 / 落地抽象
│  ├─ SimpleTrans.Protocol/     帧编解码、CRC32、消息定义
│  ├─ SimpleTrans.Server/       服务端与客户端会话
│  └─ SimpleTrans.Client/       接收客户端
└─ samples~/
   └─ SimpleTrans.Demo/         端到端联调示例（控制台）
```

## 许可证

[MIT](LICENSE.md)，Copyright (c) 2026 ntfox0001
