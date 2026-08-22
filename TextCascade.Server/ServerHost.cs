using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

public static class ServerHost
{
    public const int Ok = 0;
    public const int Error = 1;

    public static int RunServer(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--config", StringComparison.Ordinal) && args.Length < 2)
        {
            Console.Error.WriteLine("Configuration error: --config requires a path.");
            return Error;
        }

        RuntimeConfig config;
        UsersFile users;
        RuntimeStateStore stateStore;
        try
        {
            var configPath = args.Length > 0 && args[0] == "--config" ? args[1] : "textcascade.toml";
            config = Config.CreateDefaultConfig();
            config = Config.LoadTomlConfig(configPath, config);
            config = Config.ApplyEnvironmentOverrides(config);
            Config.ValidateConfig(config);
            users = UsersFile.LoadUsers(config.Files.UsersFile);
            stateStore = new RuntimeStateStore(config.Files.StateFile);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or DecoderFallbackException or IOException)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            return Error;
        }

        LoadedCertificate certificate;
        try
        {
            certificate = CertificateLoader.Load(config.Server.CertificatePath);
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            return Error;
        }

        using (certificate)
        {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseKestrel(ConfigureKestrel(config, certificate));
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
        });
        builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton(stateStore);
        builder.Services.AddSingleton(serviceProvider => new SyncServer(
            config,
            users,
            serviceProvider.GetRequiredService<RuntimeStateStore>(),
            serviceProvider.GetRequiredService<IPasswordHasher>(),
            serviceProvider.GetRequiredService<IClock>(),
            serviceProvider.GetRequiredService<ILogger<SyncServer>>()));
        builder.Services.AddHostedService<HeartbeatScannerService>();
        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapPost("/api/v1/login", async context => await AuthService.HandleLoginAsync(
            context,
            config,
            context.RequestServices.GetRequiredService<SyncServer>(),
            app.Logger));
        app.MapGet("/api/v1/sync", async context => await SyncEndpoint.HandleAsync(context, config, context.RequestServices.GetRequiredService<SyncServer>()));
        app.MapMethods("/health", new[] { "HEAD" }, () => Results.Json(new { status = "ok" }));

        app.Run();
        }
        return Ok;
    }

    private static Action<KestrelServerOptions> ConfigureKestrel(RuntimeConfig config, LoadedCertificate certificate)
    {
        return options =>
        {
            options.Listen(IPAddress.Parse(config.Server.Bind), config.Server.Port, listenOptions =>
            {
                listenOptions.UseHttps(certificate.Certificate, httpsOptions =>
                {
                    httpsOptions.ServerCertificateChain = certificate.Chain;
                });
            });
        };
    }
}

internal static class CertificateLoader
{
    public static LoadedCertificate Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("server.certificate_path must not be empty.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("TLS certificate file was not found.", path);
        }

        LoadedCertificate certificate;
        if (path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".crt", StringComparison.OrdinalIgnoreCase))
        {
            certificate = LoadPemCertificate(path);
        }
        else if (path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
                 || path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
        {
            var chain = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password: null, X509KeyStorageFlags.EphemeralKeySet);
            var certificateWithKey = chain.Cast<X509Certificate2>().FirstOrDefault(item => item.HasPrivateKey);
            if (certificateWithKey is null)
            {
                DisposeChain(chain);
                throw new InvalidOperationException("TLS PFX must include an unencrypted private key.");
            }

            certificate = new LoadedCertificate(certificateWithKey, chain);
        }
        else
        {
            throw new InvalidOperationException("TLS certificate must use .pem, .crt, .pfx, or .p12 extension.");
        }

        return certificate;
    }

    private static LoadedCertificate LoadPemCertificate(string certificatePath)
    {
        var keyPath = File.Exists(Path.ChangeExtension(certificatePath, ".key"))
            ? Path.ChangeExtension(certificatePath, ".key")
            : certificatePath;

        var chain = new X509Certificate2Collection();
        try
        {
            chain.ImportFromPemFile(certificatePath);
            if (chain.Count == 0)
            {
                throw new InvalidOperationException("PEM file does not contain a certificate.");
            }

            var certificateWithKey = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            var originalLeaf = chain.Cast<X509Certificate2>().FirstOrDefault(c => c.Equals(certificateWithKey));
            if (originalLeaf is not null)
            {
                var originalIndex = chain.IndexOf(originalLeaf);
                chain[originalIndex] = certificateWithKey;
                originalLeaf.Dispose();
            }
            else
            {
                chain.Insert(0, certificateWithKey);
            }

            return new LoadedCertificate(certificateWithKey, chain);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or CryptographicException
            or InvalidOperationException
            or IOException)
        {
            DisposeChain(chain);
            throw new InvalidOperationException(
                $"Unable to load PEM certificate '{certificatePath}': {exception.Message}",
                exception);
        }
    }

    private static void DisposeChain(X509Certificate2Collection chain)
    {
        foreach (var certificate in chain)
        {
            certificate.Dispose();
        }
    }
}

internal sealed class LoadedCertificate : IDisposable
{
    public LoadedCertificate(X509Certificate2 certificate, X509Certificate2Collection chain)
    {
        Certificate = certificate;
        Chain = chain;
    }

    public X509Certificate2 Certificate { get; }

    public X509Certificate2Collection Chain { get; }

    public void Dispose()
    {
        foreach (var certificate in Chain)
        {
            certificate.Dispose();
        }
    }
}

public sealed class HeartbeatScannerService : IHostedService, IDisposable
{
    private Timer? timer;

    private readonly SyncServer syncServer;

    public HeartbeatScannerService(SyncServer syncServer)
    {
        this.syncServer = syncServer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = new Timer(Scan, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    private void Scan(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        syncServer.ScanHeartbeats(now);

        var recoveryEnd = syncServer.ProcessStartTime.AddSeconds(
            syncServer.Config.Limits.SnapshotWindowSeconds);
        if (now < recoveryEnd)
        {
            return;
        }

        foreach (var pair in syncServer.Registry.All)
        {
            pair.Value.CloseRecoveryWindow(now);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.Infinite, 0);
        await syncServer.ShutdownAsync(TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        timer?.Dispose();
    }
}

