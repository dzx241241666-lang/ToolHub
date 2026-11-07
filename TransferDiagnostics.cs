using Switchboard.Transfer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Switchboard;

internal static class TransferDiagnostics
{
    public static async Task Interop(string root)
    {
        await using var pc = new TransferService(new("Windows interop", Path.Combine(root, "pc-state"), Path.Combine(root, "pc-received"), 0, true, false));
        var received = new TaskCompletionSource<ReceiveBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        pc.Offered += offer => offer.Accept(true); pc.Received += batch => received.TrySetResult(batch);
        await pc.StartAsync();
        using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "java-ready.json")));
        var peer = await pc.AddPeerAsync("127.0.0.1:" + ready.RootElement.GetProperty("port").GetInt32());
        var source = Directory.GetFiles(Path.Combine(root, "source"));
        await File.WriteAllTextAsync(Path.Combine(root, "pc-ready.json"), JsonSerializer.Serialize(new { address = "127.0.0.1:" + pc.Self.Port }));
        await pc.SendAsync(peer, source, CancellationToken.None);
        var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (!batch.CopyToClipboard || batch.Paths.Count != source.Length) throw new Exception("Java receive preferences or file count differed");
        foreach (var path in source)
        {
            var target = batch.Paths.Single(p => Path.GetFileName(p) == Path.GetFileName(path));
            if (!SHA256.HashData(await File.ReadAllBytesAsync(path)).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(target)))) throw new Exception("Java -> Windows checksum mismatch");
        }
        for (var i = 0; !File.Exists(Path.Combine(root, "java-result.json")) && i < 150; i++) await Task.Delay(200);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "java-result.json")));
        if (!result.RootElement.GetProperty("success").GetBoolean()) throw new Exception("Windows -> Java verification failed");
        await File.WriteAllTextAsync(Path.Combine(root, "interop-results.json"), JsonSerializer.Serialize(new { At = DateTimeOffset.Now, FilesEachDirection = source.Length, Sha256Matched = true, ClipboardPreferenceMatched = true, WindowsToJava = true, JavaToWindows = true, PersonalFilesSent = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static async Task Run(string output)
    {
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "transfer-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        Check(TransferRules.Name(@"..\..\CON.txt") == "_CON.txt" && TransferRules.Name("../../hello.txt") == "hello.txt", "Received file names remain inside the receive directory");
        Check(!TransferRules.LocalAddress(IPAddress.Parse("8.8.8.8")) && TransferRules.LocalAddress(IPAddress.Parse("192.168.0.2")), "Only local network addresses are accepted");
        await using var a = new TransferService(new("测试电脑", Path.Combine(root, "a-state"), Path.Combine(root, "a-received"), 0, true, false));
        await using var b = new TransferService(new("测试手机", Path.Combine(root, "b-state"), Path.Combine(root, "b-received"), 0, true, false));
        await a.StartAsync().ConfigureAwait(false); await b.StartAsync().ConfigureAwait(false);
        var received = new TaskCompletionSource<ReceiveBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reject = false;
        b.Offered += offer => { if (reject) offer.Reject(); else offer.Accept(true); }; b.Received += batch => received.TrySetResult(batch);
        var peer = await a.AddPeerAsync("127.0.0.1:" + b.Self.Port).ConfigureAwait(false);
        Check(peer.Info.Id == b.Self.Id && peer.Info.CertificateSha256 == b.Self.CertificateSha256, "Peer discovery verifies the actual TLS certificate");
        var files = new List<string>();
        foreach (var (name, data) in new[] { ("样例.txt", Encoding.UTF8.GetBytes("局域网文件传输测试\n")), ("binary.bin", RandomNumberGenerator.GetBytes(350000)), ("empty.bin", Array.Empty<byte>()) })
        { var path = Path.Combine(root, name); await File.WriteAllBytesAsync(path, data).ConfigureAwait(false); files.Add(path); }
        Directory.CreateDirectory(Path.Combine(root, "b-received"));
        await File.WriteAllTextAsync(Path.Combine(root, "b-received", "样例.txt"), "existing file").ConfigureAwait(false);
        await a.SendAsync(peer, files, CancellationToken.None).ConfigureAwait(false);
        var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(batch.Paths.Count == 3 && batch.CopyToClipboard, "Batch receipt preserves the clipboard preference");
        foreach (var source in files)
        {
            var target = batch.Paths.Single(p => Path.GetFileName(p) == (Path.GetFileName(source) == "样例.txt" ? "样例 (1).txt" : Path.GetFileName(source)));
            Check(SHA256.HashData(await File.ReadAllBytesAsync(source).ConfigureAwait(false)).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(target).ConfigureAwait(false))), "End-to-end file integrity: " + Path.GetFileName(source));
        }
        Check(await File.ReadAllTextAsync(Path.Combine(root, "b-received", "样例.txt")).ConfigureAwait(false) == "existing file", "Existing files are never overwritten");
        var reverse = new TaskCompletionSource<ReceiveBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Offered += offer => offer.Accept(false); a.Received += value => reverse.TrySetResult(value);
        var back = await b.AddPeerAsync("127.0.0.1:" + a.Self.Port).ConfigureAwait(false);
        await b.SendAsync(back, new[] { files[0] }, CancellationToken.None).ConfigureAwait(false);
        Check(!(await reverse.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).CopyToClipboard, "Reverse transfer works with clipboard copying disabled");
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseProxy = false };
        using var client = new HttpClient(handler);
        var endpoint = "https://127.0.0.1:" + b.Self.Port + TransferRules.Api;
        using var unauthorized = await client.PostAsync(endpoint + "/upload?session=invalid&file=invalid&token=invalid", new ByteArrayContent(new byte[] { 1 })).ConfigureAwait(false);
        Check(unauthorized.StatusCode == HttpStatusCode.Forbidden, "Unapproved uploads are rejected");
        var bad = new TransferFile("bad", "bad.bin", 3, "application/octet-stream", "not-a-hash");
        using var invalid = await client.PostAsJsonAsync(endpoint + "/prepare", new PrepareTransfer(a.Self, new() { bad }), TransferService.Json).ConfigureAwait(false);
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Malformed metadata is rejected before a receive prompt");
        var payload = new byte[] { 7, 8, 9 };
        var guarded = new TransferFile("guarded", "guarded.bin", payload.Length, "application/octet-stream", new string('0', 64));
        using var prepare = await client.PostAsJsonAsync(endpoint + "/prepare", new PrepareTransfer(a.Self, new() { guarded }), TransferService.Json);
        var accepted = (await prepare.Content.ReadFromJsonAsync<PrepareReply>(TransferService.Json))!;
        var uploadUrl = endpoint + "/upload?session=" + accepted.Session + "&file=guarded&token=";
        using var wrongToken = await client.PostAsync(uploadUrl + "wrong", new ByteArrayContent(payload));
        Check(wrongToken.StatusCode == HttpStatusCode.Forbidden, "Upload tokens are verified for every file");
        using var corrupt = await client.PostAsync(uploadUrl + accepted.Files["guarded"], new ByteArrayContent(payload));
        Check(corrupt.StatusCode == HttpStatusCode.UnprocessableEntity && !File.Exists(Path.Combine(root, "b-received", "guarded.bin")), "Checksum failures never publish a received file");
        using var cancel = await client.PostAsync(endpoint + "/cancel?session=" + accepted.Session, null);
        using var afterCancel = await client.PostAsync(uploadUrl + accepted.Files["guarded"], new ByteArrayContent(payload));
        Check(afterCancel.StatusCode == HttpStatusCode.Forbidden, "Cancelled receive sessions no longer accept uploads");
        reject = true;
        using var rejected = await client.PostAsJsonAsync(endpoint + "/prepare", new PrepareTransfer(a.Self, new() { guarded }), TransferService.Json);
        Check(rejected.StatusCode == HttpStatusCode.Forbidden, "Rejected transfers do not create a receive session");
        using var wrongPinHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate != null && Convert.ToHexString(SHA256.HashData(certificate.RawData)) == new string('0', 64), UseProxy = false };
        using var wrongPin = new HttpClient(wrongPinHandler);
        var denied = false;
        try { using var ignored = await wrongPin.GetAsync(endpoint + "/info").ConfigureAwait(false); } catch (HttpRequestException) { denied = true; }
        Check(denied, "Certificate mismatch prevents a connection");
        File.WriteAllText(output, JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Checks = checks, FixtureDirectory = root, PersonalFilesSent = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
