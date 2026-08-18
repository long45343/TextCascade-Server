using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        try
        {
            var configPath = args.Length > 0 && args[0] == "--config" ? args[1] : "textcascade.toml";
            config = Config.CreateDefaultConfig();
            config = Config.LoadTomlConfig(configPath, config);
            config = Config.ApplyEnvironmentOverrides(config);
            Config.ValidateConfig(config);
            var users = UsersFile.LoadUsers(config.Files.UsersFile);
            SyncServer.Instance.Initialize(config, users);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or DecoderFallbackException)
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
        RuntimeConfigAccessor.Current = config;
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseKestrel(ConfigureKestrel(config, certificate));
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
        });
        builder.Services.AddHostedService<HeartbeatScannerService>();
        var app = builder.Build();
        SyncServer.Instance.Logger = app.Logger;
        app.UseWebSockets();

        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapPost("/api/v1/login", async context => await AuthService.HandleLoginAsync(context, config, app.Logger));
        app.MapGet("/api/v1/sync", async context => await SyncEndpoint.HandleAsync(context, config));
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
            var certificatePem = File.ReadAllText(certificatePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
            foreach (Match match in Regex.Matches(certificatePem, "-----BEGIN CERTIFICATE-----(?<data>.*?)-----END CERTIFICATE-----", RegexOptions.Singleline))
            {
                var base64 = Regex.Replace(match.Groups["data"].Value, "\\s", string.Empty);
                var certificateBytes = Convert.FromBase64String(base64);
                chain.Add(X509CertificateLoader.LoadCertificate(certificateBytes));
            }

            if (chain.Count == 0)
            {
                throw new InvalidOperationException("PEM file does not contain a certificate.");
            }
        }
        catch (Exception exception)
        {
            DisposeChain(chain);
            throw new InvalidOperationException($"Unable to parse PEM certificate '{certificatePath}': {exception.Message}", exception);
        }

        string keyPem;
        try
        {
            keyPem = File.ReadAllText(keyPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (Exception exception)
        {
            DisposeChain(chain);
            throw new InvalidOperationException($"Unable to read PEM private key '{keyPath}': {exception.Message}", exception);
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(keyPem);
            var certificateWithKey = chain[0].CopyWithPrivateKey(rsa);
            chain[0].Dispose();
            chain[0] = certificateWithKey;
            return new LoadedCertificate(certificateWithKey, chain);
        }
        catch (Exception rsaException)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(keyPem);
                var certificateWithKey = chain[0].CopyWithPrivateKey(ecdsa);
                chain[0].Dispose();
                chain[0] = certificateWithKey;
                return new LoadedCertificate(certificateWithKey, chain);
            }
            catch (Exception ecdsaException)
            {
                DisposeChain(chain);
                throw new InvalidOperationException($"Unable to bind PEM private key. RSA: {rsaException.Message}; ECDSA: {ecdsaException.Message}", ecdsaException);
            }
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = new Timer(Scan, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    private static void Scan(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        SyncServer.Instance.ScanHeartbeats(now);

        var recoveryEnd = SyncServer.Instance.ProcessStartTime.AddSeconds(
            SyncServer.Instance.Config.Limits.SnapshotWindowSeconds);
        if (now < recoveryEnd)
        {
            return;
        }

        foreach (var pair in SyncServer.Instance.Registry.All)
        {
            pair.Value.CloseRecoveryWindow(now);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.Infinite, 0);
        await SyncServer.Instance.ShutdownAsync(TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        timer?.Dispose();
    }
}
