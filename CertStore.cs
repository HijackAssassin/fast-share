using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FastShare;

/// <summary>
/// Creates and persists the TLS certificate used when hosting.
///
/// It is self-signed because the host is reached by raw IP and no CA will issue for one.
/// Rather than trusting a CA chain, the far side pins this exact certificate by SHA-256
/// fingerprint, carried inside the connection code. Only this one certificate is ever
/// accepted, which is stricter than ordinary browser PKI.
///
/// TLS costs nothing here: AES-GCM with hardware acceleration runs at gigabytes per second,
/// far above the link. The certificate is persisted so its fingerprint - and therefore every
/// issued connection code - stays valid across restarts.
/// </summary>
public static class CertStore
{
    /// <remarks>
    /// Loaded with UserKeySet, never EphemeralKeySet. On Windows, SChannel cannot use an
    /// ephemeral private key for *server* authentication: the listener starts normally and
    /// then drops every connection mid-handshake with an unexpected EOF.
    /// </remarks>
    public static X509Certificate2 LoadOrCreate(string pfxPath, ref string? password)
    {
        if (File.Exists(pfxPath) && !string.IsNullOrEmpty(password))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfxPath), password,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            }
            catch
            {
                // Corrupt or password mismatch: fall through and mint a new one.
            }
        }

        password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=FastShare", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("fastshare");
        request.CertificateExtensions.Add(san.Build());

        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        var pfx = created.Export(X509ContentType.Pfx, password);
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, pfx);

        return X509CertificateLoader.LoadPkcs12(pfx, password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    public static string Fingerprint(X509Certificate2 cert) =>
        Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
}
