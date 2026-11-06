using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// A deliberately non-mutating acceptance path: actual Aruba REST -> DeviceService -> independent
/// SQL Server DbContext -> persisted evidence. This is NOT certification of any device WRITE.
/// Only a platform administrator may invoke it, and only against an explicit approved IP allowlist.
/// </summary>
public sealed class HardwareAcceptanceService(
    IDbContextFactory<NetworkDbContext> factory,
    DeviceService devices,
    IConfiguration configuration)
{
    public async Task<HardwareAcceptanceRun> CaptureAsync(Guid deviceId, string actor, CancellationToken ct = default)
    {
        if (!configuration.GetValue<bool>("Jcf:PilotAcceptance:Enabled"))
            throw new InvalidOperationException("Real hardware acceptance is disabled. An administrator must explicitly enable the approved pilot.");
        await using (var preflight = await factory.CreateDbContextAsync(ct))
        {
            if (!preflight.Database.IsSqlServer())
                throw new InvalidOperationException("The hardware acceptance workflow requires a real SQL Server, not the demo or in-memory store.");
            var target = await preflight.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, ct)
                ?? throw new KeyNotFoundException("Device is not registered.");
            var ips = configuration.GetSection("Jcf:PilotAcceptance:AllowedDeviceIps").Get<string[]>() ?? [];
            if (!ips.Contains(target.ManagementIp, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The management address is not on the approved pilot acceptance allowlist.");
            if (target.LocationId is null)
                throw new InvalidOperationException("Assign the device to a valid physical location before running acceptance.");
            if (!await preflight.Locations.AnyAsync(x => x.Id == target.LocationId, ct))
                throw new InvalidOperationException("The assigned physical location is missing.");
            if (!await preflight.DevicePlacements.AnyAsync(x => x.DeviceId == deviceId &&
                x.LocationId == target.LocationId && x.ThroughUtc == null, ct))
                throw new InvalidOperationException("An active effective-dated device placement is required before acceptance.");
        }

        var started = DateTime.UtcNow;
        // DeviceService performs Aruba authentication and read-only REST collection, then commits telemetry,
        // API status, members and audit entries before returning. It has no device write adapter.
        var probe = await devices.ProbeAsync(deviceId, actor, ct);

        // A fresh independent DbContext must be able to find the new rows; do NOT count objects still tracked
        // in the probing context or rely on an in-memory projection.
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!db.Database.IsSqlServer()) throw new InvalidOperationException("SQL provider changed during pilot acceptance.");
        var targetAfter = await db.Devices.AsNoTracking().SingleAsync(x => x.Id == deviceId, ct);
        var snapshot = await db.TelemetrySnapshots.AsNoTracking().Where(x => x.DeviceId == deviceId && x.AtUtc >= started.AddSeconds(-2))
            .OrderByDescending(x => x.AtUtc).FirstOrDefaultAsync(ct);
        var freshMembers = await db.ClusterMembers.AsNoTracking().Where(x => x.ConductorDeviceId == deviceId && x.ObservedUtc >= started.AddSeconds(-2)).CountAsync(ct);
        var freshPorts = await db.PortObservations.AsNoTracking().Where(x => x.DeviceId == deviceId && x.AtUtc >= started.AddSeconds(-2)).CountAsync(ct);
        var snapshotPersisted = snapshot is not null && snapshot.ApiConnected == probe.Connected;
        var inventoryPersisted = targetAfter.IsAccessPoint
            ? probe.Members.Count > 0 && freshMembers == probe.Members.Count
            : probe.PortReadings is { Count: > 0 } && freshPorts == probe.PortReadings.Count;
        var apiHealthy = probe.Connected && probe.Issue is null && targetAfter.LastStatus == "Healthy";
        var passed = apiHealthy && snapshotPersisted && inventoryPersisted;
        var criteria = new
        {
            sqlServerProvider = true,
            physicalLocationAssigned = targetAfter.LocationId.HasValue,
            deviceApiAuthenticated = probe.Connected,
            apiReadOperationsHealthy = probe.Issue is null,
            freshTelemetryLoadedFromSql = snapshotPersisted,
            freshClusterOrPortInventoryLoadedFromSql = inventoryPersisted,
            noDeviceConfigurationWritesRequested = true,
            note = "Only authentication POST and documented monitoring GETs are used; configuration mutation was not attempted."
        };
        var run = new HardwareAcceptanceRun
        {
            DeviceId = deviceId,
            Actor = actor,
            StartedUtc = started,
            CompletedUtc = DateTime.UtcNow,
            Verdict = passed ? "ReadPathVerified" : "NotVerified",
            Summary = passed ? "Live Aruba API observations persisted to SQL and reloaded." :
                "API or expected persisted inventory incomplete; inspect source and device model.",
            SqlServerVerified = true,
            DeviceApiAuthenticated = probe.Connected,
            FreshSnapshotPersisted = snapshotPersisted,
            ExpectedInventoryPersisted = inventoryPersisted,
            ReportedApCount = probe.ApCount,
            StoredClusterMembers = freshMembers,
            StoredPortSamples = freshPorts,
            Firmware = targetAfter.Firmware,
            EvidenceJson = JsonSerializer.Serialize(criteria)
        };
        db.HardwareAcceptanceRuns.Add(run);
        db.AuditEvents.Add(new AuditEvent
        {
            Actor = actor,
            Action = "Pilot hardware read acceptance",
            Target = deviceId.ToString(),
            Outcome = run.Verdict + "; zero configuration writes"
        });
        await db.SaveChangesAsync(ct);
        return run;
    }
}
