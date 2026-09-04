using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TextCascade.Server;

namespace TextCascade.Server.Tests.NetworkIntegration;

/// <summary>
/// Real-Kestrel TLS fixture for the NetworkIntegration category: self-signed certificate,
/// random port binding, and a restart-friendly handle over ServerHost.CreateApp.
/// </summary>
public sealed class NetworkTestFixture : IAsyncDisposable
{
    public string TempDir { get; }
    public RuntimeConfig Config { get; }
    public UsersFile Users { get; }
    public string UsersPath { get; }
    public string StatePath { get; }
    public string PfxPath { get; }
    public TestLogCollector Logs { get; } = new();

    private NetworkTestFixture(string tempDir, RuntimeConfig config, UsersFile users, string usersPath, string statePath, string pfxPath)
    {
        TempDir = tempDir;
        Config = config;
        Users = users;
        UsersPath = usersPath;
        StatePath = statePath;
        PfxPath = pfxPath;
    }

    public static NetworkTestFixture Create(
        Func<RuntimeConfig, RuntimeConfig>? configModifier = null,
        Action<UsersFile>? usersOverride = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "textcascade-ni-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var certificate = SelfSignedCertificate.Create("localhost");
        var pfxPath = Path.Combine(tempDir, "server.pfx");
        File.WriteAllBytes(pfxPath, certificate.Export(X509ContentType.Pfx));

        var usersPath = Path.Combine(tempDir, "users.json");
        var statePath = Path.Combine(tempDir, "state.json");
        var users = new UsersFile
        {
            Users =
            [
                new UserRecord("alice", "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA", 1),
            ],
            NextTokenVersion = 2,
        };
        usersOverride?.Invoke(users);
        UsersFile.SaveUsers(usersPath, users);

        var defaults = TextCascade.Server.Config.CreateDefaultConfig();
        var config = defaults with
        {
            TokenSecret = Encoding.UTF8.GetBytes("12345678901234567890123456789012"),
            Files = new FilesConfig(usersPath, statePath),
            Server = new ServerConfig("127.0.0.1", 0, pfxPath),
            Limits = defaults.Limits,
        };
        if (configModifier is not null)
        {
            config = configModifier(config);
        }

        return new NetworkTestFixture(tempDir, config, users, usersPath, statePath, pfxPath);
    }

    /// <summary>Starts a Kestrel instance bound to 127.0.0.1:0 over the self-signed PFX.</summary>
    public async Task<RunningServer> StartAsync()
    {
        using var loaded = CertificateLoader.Load(PfxPath);
        // LoadedCertificate disposes the underlying cert; give CreateApp its own instance.
        var app = ServerHost.CreateApp(
            [],
            Config,
            Users,
            new RuntimeStateStore(StatePath),
            hasher: new FastPasswordHasher(),
            clock: TimeProvider.System,
            certificate: new LoadedCertificate(
                new X509Certificate2(PfxPath),
                new X509Certificate2Collection(new X509Certificate2(PfxPath))));

        app.Services.GetRequiredService<ILoggerFactory>().AddProvider(Logs);
        app.Urls.Add("https://127.0.0.1:0");
        await app.StartAsync();

        var address = app.Urls.First();
        var uri = new Uri(address);
        return new RunningServer(app, uri.Port, uri.Authority);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(TempDir))
        {
            try { Directory.Delete(TempDir, true); } catch { }
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public sealed class RunningServer
    {
        public WebApplication App { get; }
        public int Port { get; }
        public string Authority { get; }

        public RunningServer(WebApplication app, int port, string authority)
        {
            App = app;
            Port = port;
            Authority = authority;
        }

        public string WssUrl => $"wss://{Authority}/api/v1/sync";

        public async Task StopAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await App.StopAsync(cts.Token); } catch { }
            await App.DisposeAsync();
        }
    }
}

public sealed class TestLogCollector : ILoggerProvider, ILogger
{
    public List<string> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add(formatter(state, exception));
        }
    }

    public void Dispose() { }
}

public sealed class FastPasswordHasher : IPasswordHasher
{
    public const string ValidHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA";

    public string Hash(string password, Isopoh.Cryptography.Argon2.Argon2Config config) => ValidHash;

    public bool Verify(string password, string encodedHash) => password == "password123" && encodedHash == ValidHash;

    public bool NeedsRehash(string encodedHash, Isopoh.Cryptography.Argon2.Argon2Config config) => false;
}

/// <summary>Self-signed leaf certificate helpers (decision Q3: generated at test runtime).</summary>
public static class SelfSignedCertificate
{
    public static X509Certificate2 Create(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subject}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(subject);
        san.AddIpAddress(IPAddress.Parse("127.0.0.1"));
        request.CertificateExtensions.Add(san.Build());

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }
}