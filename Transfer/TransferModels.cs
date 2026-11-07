using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Switchboard.Transfer;

public sealed record PeerInfo(string Id, string Name, string Platform, int Port, string CertificateSha256, int Version = 1);
public sealed record TransferPeer(PeerInfo Info, string Address, DateTimeOffset LastSeen, bool Manual = false)
{
    public string Name => Info.Name;
    public string Details => Info.Platform + " · " + Address + ":" + Info.Port;
}
public sealed record TransferFile(string Id, string Name, long Size, string Mime, string Sha256);
public sealed record PrepareTransfer(PeerInfo Sender, List<TransferFile> Files);
public sealed record PrepareReply(string Session, Dictionary<string, string> Files);
public sealed record ReceiveDecision(bool Accept, bool CopyToClipboard);
public sealed record ReceiveBatch(string PeerName, List<string> Paths, long Bytes, bool CopyToClipboard, string? Text = null);
public sealed record TransferProgress(string Direction, string PeerName, string FileName, long Completed, long Total);
public sealed class ReceiveOffer
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string PeerName { get; init; }
    public required string Address { get; init; }
    public required IReadOnlyList<TransferFile> Files { get; init; }
    public long Bytes => Files.Sum(f => f.Size);
    public TaskCompletionSource<ReceiveDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Pending => !Decision.Task.IsCompleted;
    public void Accept(bool copy) => Decision.TrySetResult(new(true, copy));
    public void Reject() => Decision.TrySetResult(new(false, false));
}
public sealed record TransferOptions(string Name, string StateDirectory, string ReceiveDirectory, int Port = 53318, bool LoopbackOnly = false, bool Discovery = true);
public static class TransferRules
{
    public const int DiscoveryPort = 53318;
    public const int MaxFiles = 512;
    public const long MaxFileSize = 32L * 1024 * 1024 * 1024;
    public const string Api = "/api/switchboard/v1";
    public static bool LocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 169 && bytes[1] == 254);
    }
    public static string Name(string input)
    {
        var last = (input ?? "file").Replace('\\', '/').Split('/').LastOrDefault() ?? "file";
        var value = Regex.Replace(last, "[\\x00-\\x1f<>:\"/\\\\|?*]", "_").Trim().TrimEnd('.', ' ');
        if (value.Length == 0) value = "file";
        if (value.Length > 180) value = value[..140] + Path.GetExtension(value)[..Math.Min(20, Path.GetExtension(value).Length)];
        if (Regex.IsMatch(value.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) value = "_" + value;
        return value;
    }
    public static bool Hash(string? value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
    public static bool ValidPeer(PeerInfo? peer) => peer != null && peer.Version == 1 && Guid.TryParse(peer.Id, out _) && !string.IsNullOrWhiteSpace(peer.Name) && peer.Name.Length <= 64 && peer.Platform != null && peer.Platform.Length <= 32 && peer.Port is >= 1 and <= 65535 && Hash(peer.CertificateSha256);
    public static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public static string Size(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : bytes + " B";
    public static string SaveUnique(string part, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        name = Name(name);
        for (var i = 0; i < 10000; i++)
        {
            var target = Path.Combine(directory, i == 0 ? name : Path.GetFileNameWithoutExtension(name) + $" ({i})" + Path.GetExtension(name));
            try { File.Move(part, target, false); return target; }
            catch (IOException) when (File.Exists(target)) { }
        }
        throw new IOException("同名文件过多");
    }
}
