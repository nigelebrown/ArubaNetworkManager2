using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;

namespace Jcf.NetworkManager.Connectors;

public static class DevicePorts
{
    /// <summary>Aruba Instant REST listens on 4343; AOS-CX REST on 443.</summary>
    public static int Management(DeviceKind kind) => kind is DeviceKind.Ap505 or DeviceKind.Ap535 ? 4343 : 443;
}

public sealed record DeviceCertificateInfo(string Sha256, string Subject, string Issuer, DateTime NotBeforeUtc, DateTime NotAfterUtc,
    bool TrustedByServer, string ChainStatus, int Port, bool MatchesCurrentPin);

/// <param name="handlerOverride">Test seam only: supplies a simulated device transport. Never registered in DI.</param>
public sealed class DeviceHttpFactory(SecretResolver secrets, Func<Device,int,HttpMessageHandler>? handlerOverride = null)
{
    public HttpClient Create(Device device, int port)
    {
        var pinned = secrets.PinnedSha256(device) is { Length: > 0 } raw
            ? SecretResolver.NormalizeFingerprint(raw) ?? throw new InvalidOperationException("Device TLS fingerprint must be 64 hexadecimal characters (SHA-256).")
            : null;
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer(), AllowAutoRedirect = false, UseProxy = false };
        if (pinned is not null)
        {
            // Pinned: accept exactly this leaf certificate (Aruba management certificates are normally self-signed).
            handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(pinned));
        }
        // Not pinned: normal operating-system chain and host-name validation (works with CA-issued device certificates).
        HttpMessageHandler transport = handlerOverride is null ? handler : handlerOverride(device, port);
        if (handlerOverride is not null) handler.Dispose();
        var client = new HttpClient(transport) { BaseAddress = new Uri($"https://{device.ManagementIp}:{port}/"), Timeout = TimeSpan.FromSeconds(18) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "JCF-NetworkManager/"+AppInfo.Version);
        return client;
    }

    /// <summary>
    /// Reads the certificate a device presents on its management port WITHOUT trusting it and without sending
    /// credentials or any HTTP request: TCP connect, TLS handshake, record the certificate, close. The operator
    /// compares the fingerprint out-of-band (device console / web UI) before pinning it.
    /// </summary>
    public async Task<DeviceCertificateInfo> CaptureCertificateAsync(Device device, CancellationToken ct)
    {
        var port = DevicePorts.Management(device.Kind);
        X509Certificate2? captured = null;
        SslPolicyErrors policy = SslPolicyErrors.None;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(device.ManagementIp, port, timeout.Token);
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, errors) =>
            {
                if (cert is not null) captured = new X509Certificate2(cert);
                policy = errors;
                return true; // capture only; the connection is closed immediately and no data is sent
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = device.ManagementIp }, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            if (captured is null)
                throw new InvalidOperationException($"Could not read a TLS certificate from {device.ManagementIp}:{port} ({ex.GetType().Name}). Check reachability and that the management API is enabled.");
        }
        if (captured is null) throw new InvalidOperationException("The device did not present a certificate.");
        using (captured)
        {
            var sha = Convert.ToHexString(SHA256.HashData(captured.RawData));
            var current = secrets.PinnedSha256(device) is { } p ? SecretResolver.NormalizeFingerprint(p) : null;
            return new DeviceCertificateInfo(sha, captured.Subject, captured.Issuer, captured.NotBefore.ToUniversalTime(), captured.NotAfter.ToUniversalTime(),
                policy == SslPolicyErrors.None, policy.ToString(), port, current == sha);
        }
    }
}
