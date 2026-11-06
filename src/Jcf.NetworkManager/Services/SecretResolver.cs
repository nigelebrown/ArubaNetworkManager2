using System.Text.RegularExpressions;
using Jcf.NetworkManager.Domain;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Resolves device credentials by reference. Order: encrypted credential profile in the database (entered by an
/// operator), then the service environment (<c>JCF_DEVICE_&lt;REF&gt;_USER</c> / <c>_PASS</c>) for installations that
/// provision secrets outside the application. Plaintext is only ever handed to a device connector.
/// </summary>
public sealed class SecretResolver
{
    private readonly CredentialVault? vault;

    /// <summary>Environment-only resolver (tests and tooling).</summary>
    public SecretResolver() { }
    public SecretResolver(CredentialVault vault) { this.vault = vault; }

    private static void Validate(string? reference)
    {
        if (!Regex.IsMatch(reference ?? "", "^[A-Za-z0-9_]{1,40}$"))
            throw new InvalidOperationException("Invalid device secret reference; use letters, numbers and underscores.");
    }

    public async Task<(string User, string Password)> ResolveAsync(string reference, CancellationToken ct = default)
    {
        Validate(reference);
        if (vault is not null && await vault.TryGetAsync(reference, ct) is { } stored) return stored;
        if (FromEnvironment(reference) is { } env) return env;
        throw new InvalidOperationException($"Device credentials not configured for profile '{reference.ToUpperInvariant()}'. " +
            "Enter them under Devices > Credential profiles (or provision JCF_DEVICE_<PROFILE>_USER/_PASS in the service environment).");
    }

    internal static bool EnvironmentHasCredentials(string reference) => FromEnvironment(reference) is not null;

    private static (string, string)? FromEnvironment(string reference)
    {
        var prefix = "JCF_DEVICE_" + reference.ToUpperInvariant();
        var user = Environment.GetEnvironmentVariable(prefix + "_USER");
        var pass = Environment.GetEnvironmentVariable(prefix + "_PASS");
        return string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass) ? null : (user, pass);
    }

    /// <summary>Pinned certificate for a device: the operator-confirmed pin stored on the device wins over the
    /// legacy environment variable <c>JCF_DEVICE_&lt;REF&gt;_CERT_SHA256</c>.</summary>
    public string? PinnedSha256(Device device) =>
        !string.IsNullOrWhiteSpace(device.TlsCertSha256) ? device.TlsCertSha256 : PinnedSha256(device.SecretRef);

    public string? PinnedSha256(string reference) =>
        Environment.GetEnvironmentVariable("JCF_DEVICE_" + (reference ?? "").ToUpperInvariant() + "_CERT_SHA256");

    /// <summary>Canonical 64-character upper-case hex, accepting the colon/space formats browsers and openssl print.</summary>
    public static string? NormalizeFingerprint(string? value)
    {
        var hex = (value ?? "").Replace(":", "").Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }
}
