using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Switchboard.Transfer;

internal sealed class TransferIdentity : IDisposable
{
    public string Id { get; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();
    public TransferIdentity(string directory)
    {
        Directory.CreateDirectory(directory);
        var idPath = Path.Combine(directory, "transfer-id.txt");
        Id = File.Exists(idPath) && Guid.TryParse(File.ReadAllText(idPath), out var id) ? id.ToString() : Guid.NewGuid().ToString();
        File.WriteAllText(idPath, Id);
        var path = Path.Combine(directory, "transfer-identity.pfx");
        if (File.Exists(path)) Certificate = new X509Certificate2(File.ReadAllBytes(path), "", X509KeyStorageFlags.UserKeySet);
        else
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Switchboard Send", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, ""));
            Certificate = new X509Certificate2(File.ReadAllBytes(path), "", X509KeyStorageFlags.UserKeySet);
        }
    }
    public void Dispose() => Certificate.Dispose();
}
