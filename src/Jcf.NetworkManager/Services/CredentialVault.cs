using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Encrypted device credential profiles. Operators enter a username/password once (per device or per shared
/// profile, e.g. one Instant cluster); the values are encrypted with ASP.NET Core Data Protection and stored in
/// SQL. Nothing in the application can read them back except the device connectors through <see cref="SecretResolver"/>.
/// Back up the Data Protection key directory together with the database: without the keys the profiles cannot be
/// decrypted and must be re-entered (devices then report "credentials cannot be decrypted").
/// </summary>
public sealed class CredentialVault(IDbContextFactory<NetworkDbContext> dbFactory, IDataProtectionProvider dataProtection)
{
    public const string Purpose = "Jcf.NetworkManager.DeviceCredentials.v1";
    private static readonly Regex ReferencePattern = new("^[A-Za-z0-9_]{1,40}$", RegexOptions.CultureInvariant);

    public sealed record CredentialView(string Reference, string? Description, string Source, string? UpdatedBy, DateTime? UpdatedUtc, int DevicesUsing);

    private IDataProtector Protector => dataProtection.CreateProtector(Purpose);

    public static bool IsValidReference(string? reference) => reference is not null && ReferencePattern.IsMatch(reference);

    public static string Normalize(string reference) => reference.Trim().ToUpperInvariant();

    public async Task SetAsync(string reference, string username, string password, string? description, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new UnauthorizedAccessException("Authenticated operator required.");
        reference = Normalize(reference ?? "");
        if (!IsValidReference(reference)) throw new ArgumentException("Credential profile name must be 1-40 letters, numbers or underscores.");
        username = (username ?? "").Trim();
        if (username.Length is 0 or > 128 || username.Any(char.IsControl)) throw new ArgumentException("Device username is required (max 128 characters).");
        if (string.IsNullOrEmpty(password) || password.Length > 256 || password.Any(c => c is '\r' or '\n' or '\0'))
            throw new ArgumentException("Device password is required (max 256 characters, single line).");
        if (description is { Length: > 200 }) throw new ArgumentException("Description is limited to 200 characters.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.DeviceCredentials.SingleOrDefaultAsync(x => x.Reference == reference, ct);
        var created = row is null;
        row ??= db.DeviceCredentials.Add(new DeviceCredential { Reference = reference }).Entity;
        row.ProtectedUsername = Protector.Protect(username);
        row.ProtectedPassword = Protector.Protect(password);
        row.Description = string.IsNullOrWhiteSpace(description) ? row.Description : description.Trim();
        row.UpdatedBy = actor;
        row.UpdatedUtc = DateTime.UtcNow;
        db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = created ? "Create device credential profile" : "Rotate device credential profile",
            Target = reference, Outcome = "Encrypted at rest; secret not logged" });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string reference, string actor, CancellationToken ct = default)
    {
        reference = Normalize(reference ?? "");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.DeviceCredentials.SingleOrDefaultAsync(x => x.Reference == reference, ct)
            ?? throw new KeyNotFoundException("Credential profile not found.");
        var users = await db.Devices.CountAsync(d => d.SecretRef == reference && !d.IsSimulated, ct);
        if (users > 0) throw new InvalidOperationException($"Credential profile is used by {users} device(s); reassign or remove them first.");
        db.DeviceCredentials.Remove(row);
        db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = "Delete device credential profile", Target = reference, Outcome = "Removed" });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Lists profile names and where each device's credentials come from. Never returns secrets.</summary>
    public async Task<List<CredentialView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.DeviceCredentials.AsNoTracking().OrderBy(x => x.Reference)
            .Select(x => new { x.Reference, x.Description, x.UpdatedBy, x.UpdatedUtc }).ToListAsync(ct);
        var usage = (await db.Devices.AsNoTracking().Where(d => !d.IsSimulated).Select(d => d.SecretRef).ToListAsync(ct))
            .GroupBy(r => r.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Count());
        var result = stored.Select(x => new CredentialView(x.Reference, x.Description, "Encrypted profile", x.UpdatedBy, x.UpdatedUtc,
            usage.GetValueOrDefault(x.Reference))).ToList();
        foreach (var (reference, count) in usage.Where(u => stored.All(s => s.Reference != u.Key)))
        {
            var env = SecretResolver.EnvironmentHasCredentials(reference);
            result.Add(new CredentialView(reference, null, env ? "Service environment variables" : "MISSING - enter credentials", null, null, count));
        }
        return result.OrderBy(x => x.Reference).ToList();
    }

    internal async Task<(string User, string Password)?> TryGetAsync(string reference, CancellationToken ct)
    {
        reference = Normalize(reference);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.DeviceCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Reference == reference, ct);
        if (row is null) return null;
        try { return (Protector.Unprotect(row.ProtectedUsername), Protector.Unprotect(row.ProtectedPassword)); }
        catch (CryptographicException)
        {
            throw new InvalidOperationException($"Credential profile {reference} cannot be decrypted (the Data Protection key ring changed). Re-enter the device credentials.");
        }
    }
}
