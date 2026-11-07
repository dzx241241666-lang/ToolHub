using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard.Transfer;

public sealed class TransferService : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 20 };
    private sealed class TransferError : Exception { public int Status { get; } public TransferError(int status, string message) : base(message) => Status = status; }
    private sealed class Slot
    {
        public required TransferFile File; public string Token = TransferRules.Token(); public bool Receiving; public string? Path; public long Bytes;
    }
    private sealed class Session
    {
        public string Id = Guid.NewGuid().ToString("N"); public required string Address, PeerName, Directory;
        public required Dictionary<string, Slot> Files; public bool Copy; public CancellationTokenSource Cancel = new();
        public long LastActivity = DateTime.UtcNow.Ticks;
    }
    private readonly TransferOptions _options;
    private readonly TransferIdentity _identity;
    private readonly CancellationTokenSource _life = new();
    private readonly ConcurrentDictionary<string, TransferPeer> _peers = new();
    private readonly object _receiveLock = new();
    private WebApplication? _server;
    private UdpClient? _udp;
    private Task? _udpTask, _announceTask;
    private Session? _session;
    private ReceiveOffer? _offer;
    private bool _reserved;
    private long _progressAt;
    private string _receiveDirectory;
    public PeerInfo Self { get; private set; }
    public event Action<ReceiveOffer>? Offered;
    public event Action<ReceiveBatch>? Received;
    public event Action<TransferProgress>? Progress;
    public event Action<string>? Notice;
    public IReadOnlyList<TransferPeer> Peers => _peers.Values.Where(p => p.Manual || DateTimeOffset.UtcNow - p.LastSeen < TimeSpan.FromSeconds(35)).OrderBy(p => p.Name).ToList();
    public TransferService(TransferOptions options)
    {
        _options = options; _receiveDirectory = options.ReceiveDirectory;
        _identity = new(options.StateDirectory);
        Self = new(_identity.Id, options.Name[..Math.Min(64, options.Name.Length)], "Windows", options.Port, _identity.Fingerprint);
    }
    public static List<string> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && TransferRules.LocalAddress(a) && a.GetAddressBytes()[0] != 169)
        .Select(a => a.ToString()).Distinct().ToList();
    public void SetReceiveDirectory(string directory) => _receiveDirectory = directory;
    public void SetName(string name) { if (!string.IsNullOrWhiteSpace(name)) Self = Self with { Name = name.Trim()[..Math.Min(64, name.Trim().Length)] }; }
    public async Task StartAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var port = _options.Port == 0 ? 0 : _options.Port + attempt;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.AddServerHeader = false;
                server.Limits.MaxRequestBodySize = TransferRules.MaxFileSize;
                server.Limits.MaxConcurrentConnections = 16;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                server.Listen(_options.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, port, listen => listen.UseHttps(_identity.Certificate, tls => tls.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13));
            });
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Connection.RemoteIpAddress is not IPAddress address || !TransferRules.LocalAddress(address)) { context.Response.StatusCode = 403; return; }
                try { await next(context); }
                catch (TransferError error) { if (!context.Response.HasStarted) await Reply(context, error.Status, new { error = error.Message }); }
                catch (OperationCanceledException) { if (!context.Response.HasStarted) context.Response.StatusCode = 408; }
                catch (Exception error) { SettingsStore.Log(error); if (!context.Response.HasStarted) await Reply(context, 500, new { error = "传输失败" }); }
            });
            app.MapGet(TransferRules.Api + "/info", context => Reply(context, 200, Self));
            app.MapPost(TransferRules.Api + "/prepare", PrepareAsync);
            app.MapPost(TransferRules.Api + "/upload", UploadAsync);
            app.MapPost(TransferRules.Api + "/cancel", CancelAsync);
            try
            {
                await app.StartAsync(_life.Token);
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                Self = Self with { Port = new Uri(addresses.First()).Port };
                _server = app;
                if (_options.Discovery) StartDiscovery();
                return;
            }
            catch (IOException) when (_options.Port != 0 && attempt < 7) { await app.DisposeAsync(); }
            catch { await app.DisposeAsync(); throw; }
        }
        throw new IOException("互传端口不可用");
    }
    private static string Remote(HttpContext context) => context.Connection.RemoteIpAddress!.MapToIPv4().ToString();
    private static Task Reply(HttpContext context, int status, object value)
    { context.Response.StatusCode = status; return context.Response.WriteAsJsonAsync(value, Json, context.RequestAborted); }
    private static async Task<T> Body<T>(HttpContext context)
    {
        if (context.Request.ContentLength is > 262144) throw new TransferError(413, "文件列表过大");
        using var memory = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted); if (count == 0) break; if (memory.Length + count > 262144) throw new TransferError(413, "文件列表过大"); memory.Write(buffer, 0, count); }
        try { return JsonSerializer.Deserialize<T>(memory.ToArray(), Json) ?? throw new TransferError(400, "请求无效"); }
        catch (JsonException) { throw new TransferError(400, "请求无效"); }
    }
    private async Task PrepareAsync(HttpContext context)
    {
        var request = await Body<PrepareTransfer>(context);
        if (!TransferRules.ValidPeer(request.Sender) || request.Files == null || request.Files.Count is < 1 or > TransferRules.MaxFiles ||
            request.Files.Any(f => f == null || string.IsNullOrWhiteSpace(f.Id) || f.Id.Length > 80 || string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 512 || f.Size < 0 || f.Size > TransferRules.MaxFileSize || f.Mime == TransferPayloads.TextMime && f.Size > TransferPayloads.MaxTextBytes || !TransferRules.Hash(f.Sha256)) ||
            request.Files.Select(f => f.Id).Distinct().Count() != request.Files.Count) throw new TransferError(400, "文件信息无效");
        if (request.Files.Sum(f => f.Size) > TransferRules.MaxFileSize) throw new TransferError(413, "单次传输超过 32 GB");
        var offer = new ReceiveOffer { PeerName = request.Sender.Name, Address = Remote(context), Files = request.Files };
        lock (_receiveLock) { if (_reserved) throw new TransferError(409, "正在接收其他文件"); _reserved = true; _offer = offer; }
        var retained = false;
        try
        {
            Offered?.Invoke(offer);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _life.Token);
            ReceiveDecision decision;
            try { decision = await offer.Decision.Task.WaitAsync(TimeSpan.FromSeconds(120), linked.Token); }
            catch (TimeoutException) { throw new TransferError(403, "接收请求已超时"); }
            if (!decision.Accept) throw new TransferError(403, "对方已拒绝");
            var directory = Path.GetFullPath(_receiveDirectory); Directory.CreateDirectory(directory);
            var drive = new DriveInfo(Path.GetPathRoot(directory)!);
            if (drive.IsReady && drive.AvailableFreeSpace < offer.Bytes + 16 * 1024 * 1024) throw new TransferError(507, "接收目录空间不足");
            var session = new Session { Address = offer.Address, PeerName = offer.PeerName, Directory = directory, Copy = decision.CopyToClipboard, Files = request.Files.ToDictionary(f => f.Id, f => new Slot { File = f }) };
            lock (_receiveLock) { _session = session; _offer = null; retained = true; }
            await Reply(context, 200, new PrepareReply(session.Id, session.Files.ToDictionary(p => p.Key, p => p.Value.Token)));
        }
        finally
        {
            offer.Reject();
            lock (_receiveLock) { if (!retained) { _reserved = false; _offer = null; } }
        }
    }
    private async Task UploadAsync(HttpContext context)
    {
        var id = context.Request.Query["session"].ToString(); var fileId = context.Request.Query["file"].ToString(); var token = context.Request.Query["token"].ToString();
        Session session; Slot slot;
        lock (_receiveLock)
        {
            session = _session ?? throw new TransferError(403, "传输会话无效");
            if (session.Id != id || session.Address != Remote(context) || !session.Files.TryGetValue(fileId, out slot!)) throw new TransferError(403, "传输会话无效");
            if (token.Length != slot.Token.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(slot.Token))) throw new TransferError(403, "传输凭据无效");
            if (slot.Receiving || slot.Path != null) throw new TransferError(409, "文件已在接收");
            if (context.Request.ContentLength != slot.File.Size) throw new TransferError(400, "文件长度不一致");
            slot.Receiving = true;
        }
        var partRoot = Path.Combine(session.Directory, ".switchboard-partial", session.Id);
        Directory.CreateDirectory(partRoot);
        var part = Path.Combine(partRoot, Guid.NewGuid().ToString("N") + ".part");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _life.Token, session.Cancel.Token);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072]; long written = 0;
                while (true)
                {
                    var count = await context.Request.Body.ReadAsync(buffer, linked.Token); if (count == 0) break;
                    written += count; if (written > slot.File.Size) throw new TransferError(400, "文件长度不一致");
                    await file.WriteAsync(buffer.AsMemory(0, count), linked.Token); hash.AppendData(buffer, 0, count);
                    lock (_receiveLock) { slot.Bytes = written; session.LastActivity = DateTime.UtcNow.Ticks; }
                    Report(new("接收", session.PeerName, TransferPayloads.Label(slot.File.Name, slot.File.Mime), session.Files.Values.Sum(s => s.Bytes), session.Files.Values.Sum(s => s.File.Size)));
                }
                if (written != slot.File.Size) throw new TransferError(400, "文件未完整接收");
                await file.FlushAsync(linked.Token);
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            if (!digest.Equals(slot.File.Sha256, StringComparison.OrdinalIgnoreCase)) throw new TransferError(422, "文件校验失败");
            linked.Token.ThrowIfCancellationRequested();
            if (slot.File.Mime == TransferPayloads.FolderMime) Report(new("接收", session.PeerName, "整理文件夹 · " + Path.GetFileNameWithoutExtension(slot.File.Name), session.Files.Values.Sum(s => s.Bytes), session.Files.Values.Sum(s => s.File.Size)), true);
            string saved;
            using (var heartbeat = new System.Threading.Timer(_ => Interlocked.Exchange(ref session.LastActivity, DateTime.UtcNow.Ticks), null, TimeSpan.Zero, TimeSpan.FromSeconds(5)))
                saved = await TransferPayloads.PublishAsync(part, session.Directory, slot.File, linked.Token);
            ReceiveBatch? batch = null;
            lock (_receiveLock)
            {
                slot.Path = saved; slot.Bytes = slot.File.Size;
                if (session.Files.Values.All(s => s.Path != null))
                {
                    batch = new(session.PeerName, session.Files.Values.Select(s => s.Path!).ToList(), session.Files.Values.Sum(s => s.File.Size), session.Copy,
                        session.Files.Count == 1 && slot.File.Mime == TransferPayloads.TextMime ? TransferPayloads.ReadText(saved) : null);
                    if (_session == session) { _session = null; _reserved = false; }
                }
            }
            if (batch != null) { Report(new("接收", batch.PeerName, "已完成", batch.Bytes, batch.Bytes), true); Received?.Invoke(batch); }
            context.Response.StatusCode = 200;
        }
        catch
        {
            lock (_receiveLock) { slot.Receiving = false; slot.Bytes = 0; }
            throw;
        }
        finally { try { if (File.Exists(part)) File.Delete(part); if (Directory.Exists(partRoot) && !Directory.EnumerateFileSystemEntries(partRoot).Any()) Directory.Delete(partRoot); } catch { } }
    }
    private Task CancelAsync(HttpContext context)
    {
        lock (_receiveLock)
        {
            if (_session is { } session && session.Id == context.Request.Query["session"] && session.Address == Remote(context))
            { session.Cancel.Cancel(); _session = null; _reserved = false; }
        }
        context.Response.StatusCode = 200; return Task.CompletedTask;
    }
    private void Report(TransferProgress progress, bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now - Interlocked.Read(ref _progressAt) < Stopwatch.Frequency / 8) return;
        Interlocked.Exchange(ref _progressAt, now); Progress?.Invoke(progress);
    }
    private static HttpClient Client(string? fingerprint, Action<string>? observe = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate == null || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime()) return false;
            var actual = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant(); observe?.Invoke(actual);
            return fingerprint == null || string.Equals(fingerprint, actual, StringComparison.OrdinalIgnoreCase);
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<TransferPeer> AddPeerAsync(string address, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate("https://" + address.Trim().Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/'), UriKind.Absolute, out var uri) ||
            !IPAddress.TryParse(uri.Host, out var ip) || !TransferRules.LocalAddress(ip) || uri.AbsolutePath != "/") throw new ArgumentException("地址格式：192.168.1.10:53318");
        var port = uri.IsDefaultPort ? TransferRules.DiscoveryPort : uri.Port;
        string? observed = null; using var client = Client(null, value => observed = value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _life.Token); timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var info = await client.GetFromJsonAsync<PeerInfo>($"https://{ip}:{port}{TransferRules.Api}/info", Json, timeout.Token);
        if (!TransferRules.ValidPeer(info) || !string.Equals(info!.CertificateSha256, observed, StringComparison.OrdinalIgnoreCase) || info.Port != port) throw new InvalidOperationException("设备信息或证书无效");
        if (info.Id == Self.Id) throw new InvalidOperationException("这是本机地址");
        var peer = new TransferPeer(info, ip.ToString(), DateTimeOffset.UtcNow, true); _peers[info.Id] = peer; return peer;
    }
    public Task SendAsync(TransferPeer peer, IReadOnlyList<string> paths, CancellationToken cancellation) =>
        SendAsync(peer, paths.Select(path => new TransferSource(path, Path.GetFileName(path))).ToArray(), cancellation);
    public async Task SendAsync(TransferPeer peer, IReadOnlyList<TransferSource> sources, CancellationToken cancellation)
    {
        if (!TransferRules.ValidPeer(peer.Info) || !IPAddress.TryParse(peer.Address, out var address) || !TransferRules.LocalAddress(address)) throw new ArgumentException("设备地址无效");
        if (sources.Count is < 1 or > TransferRules.MaxFiles) throw new ArgumentException("内容数量须为 1–512");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _life.Token);
        using var client = Client(peer.Info.CertificateSha256);
        var root = $"https://{peer.Address}:{peer.Info.Port}{TransferRules.Api}";
        using (var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
        {
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(6));
            var info = await client.GetFromJsonAsync<PeerInfo>(root + "/info", Json, connectionTimeout.Token);
            if (info?.Id != peer.Info.Id) throw new IOException("设备身份已变化");
        }
        var files = new List<(string Path, TransferFile File)>();
        foreach (var source in sources)
        {
            var path = source.Path;
            linked.Token.ThrowIfCancellationRequested(); var info = new FileInfo(path);
            if (!info.Exists || info.Length > TransferRules.MaxFileSize) throw new IOException("文件不存在或超过 32 GB");
            Report(new("发送", peer.Name, "校验中 · " + TransferPayloads.Label(source.Name, source.Mime), 0, sources.Sum(p => new FileInfo(p.Path).Length)), true);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, linked.Token)).ToLowerInvariant();
            files.Add((path, new(Guid.NewGuid().ToString("N"), source.Name, info.Length, source.Mime, hash)));
        }
        PrepareReply? prepared = null; var succeeded = false; long done = 0; var total = files.Sum(f => f.File.Size);
        try
        {
            Report(new("发送", peer.Name, "等待接收", 0, total), true);
            using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token); approvalTimeout.CancelAfter(TimeSpan.FromSeconds(135));
            using var metadata = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new PrepareTransfer(Self, files.Select(f => f.File).ToList()), Json));
            metadata.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync(root + "/prepare", metadata, approvalTimeout.Token);
            if (!response.IsSuccessStatusCode) throw new IOException(response.StatusCode == HttpStatusCode.Forbidden ? "对方拒绝或接收请求超时" : response.StatusCode == HttpStatusCode.Conflict ? "对方正在接收其他文件" : "对方无法接收文件");
            prepared = await response.Content.ReadFromJsonAsync<PrepareReply>(Json, linked.Token) ?? throw new IOException("传输会话无效");
            foreach (var file in files)
            {
                if (!prepared.Files.TryGetValue(file.File.Id, out var token)) throw new IOException("对方未接受全部文件");
                using var content = new FileContent(file.Path, file.File.Size, bytes => Report(new("发送", peer.Name, TransferPayloads.Label(file.File.Name, file.File.Mime), done + bytes, total)), linked.Token);
                var url = root + "/upload?session=" + Uri.EscapeDataString(prepared.Session) + "&file=" + file.File.Id + "&token=" + Uri.EscapeDataString(token);
                using var upload = await client.PostAsync(url, content, linked.Token);
                if (!upload.IsSuccessStatusCode) throw new IOException(upload.StatusCode == HttpStatusCode.UnprocessableEntity ? "文件校验失败" : "文件未能发送完成");
                done += file.File.Size;
            }
            succeeded = true; Report(new("发送", peer.Name, "已完成", total, total), true);
        }
        finally
        {
            if (!succeeded && prepared != null)
                try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); using var ignored = await client.PostAsync(root + "/cancel?session=" + Uri.EscapeDataString(prepared.Session), null, timeout.Token); } catch { }
        }
    }
    private sealed class FileContent : HttpContent
    {
        private readonly string _path; private readonly long _length; private readonly Action<long> _progress; private readonly CancellationToken _cancel;
        public FileContent(string path, long length, Action<long> progress, CancellationToken cancel) { _path = path; _length = length; _progress = progress; _cancel = cancel; Headers.ContentLength = length; Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); }
        protected override bool TryComputeLength(out long length) { length = _length; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            if (file.Length != _length) throw new IOException("发送期间文件已变化");
            var buffer = new byte[131072]; long sent = 0;
            while (true) { var count = await file.ReadAsync(buffer, _cancel); if (count == 0) break; await stream.WriteAsync(buffer.AsMemory(0, count), _cancel); sent += count; _progress(sent); }
        }
    }
    private void StartDiscovery()
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, TransferRules.DiscoveryPort));
            foreach (var address in Addresses()) try { _udp.JoinMulticastGroup(IPAddress.Parse("224.0.0.167"), IPAddress.Parse(address)); } catch (SocketException) { }
            _udpTask = ListenDiscoveryAsync();
        }
        catch (SocketException error) { SettingsStore.Log(error); Notice?.Invoke("自动发现不可用，可使用设备地址连接"); }
        _announceTask = AnnounceLoopAsync();
    }
    private async Task ListenDiscoveryAsync()
    {
        while (!_life.IsCancellationRequested && _udp != null)
        {
            try
            {
                var datagram = await _udp.ReceiveAsync(_life.Token);
                if (!TransferRules.LocalAddress(datagram.RemoteEndPoint.Address) || datagram.Buffer.Length > 4096) continue;
                using var json = JsonDocument.Parse(datagram.Buffer);
                if (!json.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != "switchboard-send") continue;
                var info = json.RootElement.GetProperty("peer").Deserialize<PeerInfo>(Json);
                if (!TransferRules.ValidPeer(info) || info!.Id == Self.Id) continue;
                _peers[info.Id] = new(info, datagram.RemoteEndPoint.Address.ToString(), DateTimeOffset.UtcNow, _peers.TryGetValue(info.Id, out var previous) && previous.Manual);
                if (json.RootElement.TryGetProperty("announce", out var announce) && announce.GetBoolean()) await DiscoverAsync(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception error) { if (!_life.IsCancellationRequested) SettingsStore.Log(error); }
        }
    }
    public async Task DiscoverAsync(bool announce = true)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { kind = "switchboard-send", peer = Self, announce }, Json);
        foreach (var address in Addresses())
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(address), 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                await udp.SendAsync(payload, new IPEndPoint(IPAddress.Parse("224.0.0.167"), TransferRules.DiscoveryPort), _life.Token);
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException) { }
        }
    }
    private async Task AnnounceLoopAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                await DiscoverAsync();
                lock (_receiveLock)
                {
                    if (_session is { } session && DateTime.UtcNow.Ticks - session.LastActivity > TimeSpan.FromMinutes(3).Ticks)
                    { session.Cancel.Cancel(); _session = null; _reserved = false; }
                }
                foreach (var peer in _peers.Where(p => !p.Value.Manual && DateTimeOffset.UtcNow - p.Value.LastSeen > TimeSpan.FromMinutes(1))) _peers.TryRemove(peer.Key, out _);
                await Task.Delay(TimeSpan.FromSeconds(5), _life.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _life.Cancel(); _udp?.Dispose();
        lock (_receiveLock) { _offer?.Reject(); _session?.Cancel.Cancel(); }
        if (_server != null) { try { await _server.StopAsync(new CancellationToken(true)); } catch { } await _server.DisposeAsync(); }
        _identity.Dispose();
    }
}
