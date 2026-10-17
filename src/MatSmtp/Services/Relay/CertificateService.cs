using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MatSmtp.Configuration;

namespace MatSmtp.Services.Relay;

/// <summary>What the receiver page shows about the certificate in use.</summary>
public sealed record CertificateInfo(string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, string Thumbprint, bool SelfSigned, bool Uploaded);

/// <summary>
/// The certificate of STARTTLS and of the TLS port. An uploaded one (PKCS#12 or PEM, kept as <c>{data}/certs/smtp.pfx</c>) wins; without it a self-signed one is made
/// for the host name on the first start and kept (<c>selfsigned.pfx</c>), so the clients see the same certificate after a restart.
/// </summary>
public sealed class CertificateService(ConfigStore config, SecretProtector secrets, ILogger<CertificateService> logger)
{
    public const string UploadedFile = "smtp.pfx";

    private const string SelfSignedFile = "selfsigned.pfx";

    private readonly object _gate = new();
    private X509Certificate2? _current;

    /// <summary>The certificate in use (made or loaded on first use, again after <see cref="Reset"/>).</summary>
    public X509Certificate2 Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    public CertificateInfo Info()
    {
        var c = Current;
        return new CertificateInfo(c.Subject, c.Issuer, c.NotBefore.ToUniversalTime(), c.NotAfter.ToUniversalTime(), c.Thumbprint, c.Subject == c.Issuer, IsUploaded);
    }

    public bool IsUploaded => !string.IsNullOrEmpty(config.Current.Smtp.CertificateFile) && File.Exists(Path.Combine(config.CertDir, config.Current.Smtp.CertificateFile));

    /// <summary>Takes an uploaded certificate (PKCS#12 with its password, or a PEM certificate with its key). Returns the error in words, or null.</summary>
    public string? Upload(byte[] file, string? password, byte[]? pemKey)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = pemKey is { Length: > 0 }
                ? X509Certificate2.CreateFromPem(System.Text.Encoding.UTF8.GetString(file), System.Text.Encoding.UTF8.GetString(pemKey))
                : X509CertificateLoader.LoadPkcs12(file, password, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException ex)
        {
            return "Das Zertifikat konnte nicht gelesen werden: " + ex.Message;
        }

        if (!certificate.HasPrivateKey)
        {
            return "Zum Zertifikat fehlt der private Schlüssel.";
        }

        if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow)
        {
            return $"Das Zertifikat ist am {Fmt.Date(certificate.NotAfter)} abgelaufen.";
        }

        // Kept as PKCS#12 with a password of its own, which the key ring protects.
        var newPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Directory.CreateDirectory(config.CertDir);
        File.WriteAllBytes(Path.Combine(config.CertDir, UploadedFile), certificate.Export(X509ContentType.Pkcs12, newPassword));
        config.Update(c =>
        {
            c.Smtp.CertificateFile = UploadedFile;
            c.Smtp.CertificatePasswordProtected = secrets.Protect(newPassword);
        });
        Reset();
        return null;
    }

    /// <summary>Goes back to the self-signed certificate.</summary>
    public void RemoveUploaded()
    {
        var path = Path.Combine(config.CertDir, UploadedFile);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        config.Update(c =>
        {
            c.Smtp.CertificateFile = "";
            c.Smtp.CertificatePasswordProtected = "";
        });
        Reset();
    }

    /// <summary>Makes a new self-signed certificate (after a change of the host name).</summary>
    public void RenewSelfSigned()
    {
        var path = Path.Combine(config.CertDir, SelfSignedFile);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Reset();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _current = null;
        }
    }

    private X509Certificate2 Load()
    {
        if (IsUploaded)
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(config.CertDir, config.Current.Smtp.CertificateFile), secrets.Unprotect(config.Current.Smtp.CertificatePasswordProtected), KeyFlags);
            }
            catch (CryptographicException ex)
            {
                logger.LogError(ex, "The uploaded certificate could not be loaded; the self-signed one is used.");
            }
        }

        var selfSigned = Path.Combine(config.CertDir, SelfSignedFile);
        if (File.Exists(selfSigned))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(selfSigned, null, KeyFlags);
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex, "The self-signed certificate could not be loaded; a new one is made.");
            }
        }

        Directory.CreateDirectory(config.CertDir);
        var bytes = MakeSelfSigned(config.Current.Smtp.Hostname);
        File.WriteAllBytes(selfSigned, bytes);
        return X509CertificateLoader.LoadPkcs12(bytes, null, KeyFlags);
    }

    /// <summary>On Linux the key stays in memory; Windows (a developer PC) cannot hand an in-memory key to TLS.</summary>
    private static X509KeyStorageFlags KeyFlags => OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    public static byte[] MakeSelfSigned(string hostname)
    {
        var name = string.IsNullOrWhiteSpace(hostname) ? "matsmtp.local" : hostname.Trim();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return certificate.Export(X509ContentType.Pkcs12);
    }
}
