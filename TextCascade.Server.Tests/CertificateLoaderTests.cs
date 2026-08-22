using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class CertificateLoaderTests
{
    private static (string CertPem, string KeyPem) GenerateSelfSignedRsaPem()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);
        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        var certPem = cert.ExportCertificatePem();
        var keyPem = rsa.ExportPkcs8PrivateKeyPem();
        return (certPem, keyPem);
    }

    private static (string CertPem, string KeyPem) GenerateSelfSignedEcdsaPem()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", ecdsa, HashAlgorithmName.SHA256);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);
        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        var certPem = cert.ExportCertificatePem();
        var keyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        return (certPem, keyPem);
    }

    [Fact]
    public void LoadPemCertificateSupportsRsaCertAndSeparateKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var (certPem, keyPem) = GenerateSelfSignedRsaPem();
            var certPath = Path.Combine(tempDir, "server.crt");
            var keyPath = Path.Combine(tempDir, "server.key");
            File.WriteAllText(certPath, certPem, Encoding.UTF8);
            File.WriteAllText(keyPath, keyPem, Encoding.UTF8);

            using var loaded = CertificateLoader.Load(certPath);
            Assert.True(loaded.Certificate.HasPrivateKey);
            Assert.NotEmpty(loaded.Chain);
            Assert.Equal(loaded.Certificate.Thumbprint, loaded.Chain[0].Thumbprint);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadPemCertificateSupportsCombinedPem()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var (certPem, keyPem) = GenerateSelfSignedRsaPem();
            var pemPath = Path.Combine(tempDir, "server.pem");
            File.WriteAllText(pemPath, certPem + "\n" + keyPem, Encoding.UTF8);

            using var loaded = CertificateLoader.Load(pemPath);
            Assert.True(loaded.Certificate.HasPrivateKey);
            Assert.NotEmpty(loaded.Chain);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadPemCertificateSupportsEcdsaCertAndSeparateKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var (certPem, keyPem) = GenerateSelfSignedEcdsaPem();
            var certPath = Path.Combine(tempDir, "server.crt");
            var keyPath = Path.Combine(tempDir, "server.key");
            File.WriteAllText(certPath, certPem, Encoding.UTF8);
            File.WriteAllText(keyPath, keyPem, Encoding.UTF8);

            using var loaded = CertificateLoader.Load(certPath);
            Assert.True(loaded.Certificate.HasPrivateKey);
            Assert.NotEmpty(loaded.Chain);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadPemCertificateWrapsMissingPrivateKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var (certPem, _) = GenerateSelfSignedRsaPem();
            var certPath = Path.Combine(tempDir, "server.pem");
            File.WriteAllText(certPath, certPem, Encoding.UTF8);

            var ex = Assert.Throws<InvalidOperationException>(() => CertificateLoader.Load(certPath));
            Assert.NotNull(ex.InnerException);
            Assert.Contains("Unable to load PEM certificate", ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadPemCertificateRejectsNoCertificate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var certPath = Path.Combine(tempDir, "empty.pem");
            File.WriteAllText(certPath, "random garbage content", Encoding.UTF8);

            var ex = Assert.Throws<InvalidOperationException>(() => CertificateLoader.Load(certPath));
            Assert.Contains("Unable to load PEM certificate", ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
