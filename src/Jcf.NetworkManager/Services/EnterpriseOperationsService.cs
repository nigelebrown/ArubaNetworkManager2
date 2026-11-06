using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>Application boundary for location, monitoring, compliance and policy operations.</summary>
public sealed class EnterpriseOperationsService(IDbContextFactory<NetworkDbContext> factory)
{
    public async Task<DevicePlacement> PlaceAsync(Guid deviceId,Guid locationId,string actor,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(actor)) throw new UnauthorizedAccessException();
        await using var db=await factory.CreateDbContextAsync(ct);
        var device=await db.Devices.FindAsync([deviceId],ct) ?? throw new KeyNotFoundException("Device not found");
        if(!await db.Locations.AnyAsync(l=>l.Id==locationId,ct)) throw new KeyNotFoundException("Location not found");
        var now=DateTime.UtcNow;
        var old=await db.DevicePlacements.Where(p=>p.DeviceId==deviceId && p.ThroughUtc==null).ToListAsync(ct);
        foreach(var p in old) p.ThroughUtc=now;
        device.LocationId=locationId;
        var placement=new DevicePlacement{DeviceId=deviceId,LocationId=locationId,FromUtc=now,ChangedBy=actor};
        db.DevicePlacements.Add(placement);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Move device location",Target=deviceId.ToString(),Outcome=locationId.ToString()});
        await db.SaveChangesAsync(ct);
        return placement;
    }

    public async Task<PhysicalSpace> CreateSpaceAsync(Guid locationId,Guid? parentId,string type,string name,string actor,CancellationToken ct=default)
    {
        if(!new[]{"Building","Floor","Zone","Room"}.Contains(type,StringComparer.Ordinal))
            throw new ArgumentException("Invalid physical space type");
        if(string.IsNullOrWhiteSpace(name) || name.Length>120) throw new ArgumentException("Invalid name");
        await using var db=await factory.CreateDbContextAsync(ct);
        if(!await db.Locations.AnyAsync(x=>x.Id==locationId,ct)) throw new KeyNotFoundException("Location does not exist");
        if(parentId is Guid pid)
        {
            var parent=await db.PhysicalSpaces.FindAsync([pid],ct);
            if(parent is null || parent.LocationId!=locationId) throw new InvalidOperationException("Parent space must be in the same location");
        }
        var space=new PhysicalSpace {LocationId=locationId,ParentSpaceId=parentId,Type=type,Name=name.Trim()};
        db.PhysicalSpaces.Add(space);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Create physical space",Target=locationId.ToString(),Outcome=type});
        await db.SaveChangesAsync(ct);
        return space;
    }

    public async Task<ConfigurationSnapshot> SnapshotAsync(Guid deviceId,string actor,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var device=await db.Devices.FindAsync([deviceId],ct) ?? throw new KeyNotFoundException("Device not found");
        var sample=await db.TelemetrySnapshots.AsNoTracking().Where(x=>x.DeviceId==deviceId)
            .OrderByDescending(x=>x.AtUtc).FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException("No existing API observation");
        if(!sample.ApiConnected) throw new InvalidOperationException("Latest observation did not authenticate");
        // Only the device adapter's explicitly whitelisted non-secret fields are snapshotted.
        var normalized=JsonSerializer.Serialize(new{firmware=device.Firmware,source=sample.ObservationSource,
            radioChannel=sample.RadioChannel,channelWidthMhz=sample.ChannelWidthMhz,
            arm=JsonSerializer.Deserialize<Dictionary<string,string>>(sample.ArmJson),timestampUtc=sample.AtUtc});
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var snapshot=new ConfigurationSnapshot{DeviceId=deviceId,Firmware=device.Firmware??"Unknown",CapturedBy=actor,
            SanitizedJson=normalized,DataSha256=hash};
        db.ConfigurationSnapshots.Add(snapshot);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Capture nonsecret snapshot",Target=deviceId.ToString(),Outcome=hash[..16]});
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    public async Task<int> EvaluateComplianceAsync(string actor,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var latest=await db.TelemetrySnapshots.AsNoTracking().GroupBy(x=>x.DeviceId)
            .Select(g=>g.OrderByDescending(x=>x.AtUtc).First()).ToListAsync(ct);
        int findings=0;
        foreach(var sample in latest)
        {
            var checks=new List<(string Rule,string Evidence,string Severity)>();
            if(sample.ChannelWidthMhz is int width && width!=20 && width!=40)
                checks.Add(("RADIO_WIDTH","Observed "+width+" MHz; JCF allows 20/40 MHz only.","Critical"));
            if(sample.RadioChannel is int channel && NetworkChangePolicy.ExcludedDfs5GhzChannels.Contains(channel))
                checks.Add(("DFS_COMPATIBILITY","Observed excluded DFS channel "+channel+"; verify regulatory setting and approved radio profile.","Warning"));
            if(sample.ApiConnected)
            {
                try
                {
                    var arm=JsonSerializer.Deserialize<Dictionary<string,string>>(sample.ArmJson);
                    if(arm is not null && arm.TryGetValue("80mhz-support",out var enabled) &&
                       enabled.Equals("enabled",StringComparison.OrdinalIgnoreCase))
                        checks.Add(("EIGHTY_MHZ_ADVERTISED","ARM configuration advertises 80 MHz support; this is outside approved policy.","Critical"));
                }
                catch(JsonException) { }
            }
            foreach(var (rule,evidence,severity) in checks)
            {
                var existing=await db.ComplianceFindings.FirstOrDefaultAsync(x=>x.DeviceId==sample.DeviceId && x.Rule==rule && x.State=="Open",ct);
                if(existing is null)
                    db.ComplianceFindings.Add(new ComplianceFinding{DeviceId=sample.DeviceId,Rule=rule,Evidence=evidence,Severity=severity});
                findings++;
            }
            // Only auto-resolve when data actually establishes a valid current value.
            var open=await db.ComplianceFindings.Where(x=>x.DeviceId==sample.DeviceId && x.State=="Open").ToListAsync(ct);
            foreach(var old in open)
                if((old.Rule=="RADIO_WIDTH" && sample.ChannelWidthMhz is 20 or 40) ||
                   (old.Rule=="DFS_COMPATIBILITY" && sample.RadioChannel is int safe && !NetworkChangePolicy.ExcludedDfs5GhzChannels.Contains(safe)))
                { old.State="Resolved";old.ClosedUtc=DateTime.UtcNow; }
        }
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Evaluate RF compliance",Target="Fleet",Outcome=findings+" observations; no device writes"});
        await db.SaveChangesAsync(ct);
        return findings;
    }

    public async Task<int> CreateRollupsAsync(CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var now=DateTime.UtcNow;
        var start=now.AddDays(-2);
        var samples=await db.TelemetrySnapshots.AsNoTracking().Where(x=>x.AtUtc>=start && x.AtUtc<now).ToListAsync(ct);
        var groups=samples.GroupBy(x=>new{x.DeviceId,Bucket=new DateTime(x.AtUtc.Year,x.AtUtc.Month,x.AtUtc.Day,x.AtUtc.Hour,0,0,DateTimeKind.Utc)});
        int created=0;
        foreach(var group in groups)
        {
            if(await db.TelemetryRollups.AnyAsync(x=>x.DeviceId==group.Key.DeviceId && x.BucketUtc==group.Key.Bucket && x.BucketMinutes==60,ct))continue;
            var util=group.Where(x=>x.ChannelUtilizationPct.HasValue).Select(x=>x.ChannelUtilizationPct!.Value).ToList();
            var noise=group.Where(x=>x.NoiseFloorDbm.HasValue).Select(x=>x.NoiseFloorDbm!.Value).ToList();
            db.TelemetryRollups.Add(new TelemetryRollup{DeviceId=group.Key.DeviceId,BucketUtc=group.Key.Bucket,
                SampleCount=group.Count(),AverageUtilizationPct=util.Count>0?util.Average():null,
                MaxUtilizationPct=util.Count>0?util.Max():null,
                AverageNoiseFloorDbm=noise.Count>0?noise.Average():null});
            created++;
        }
        await db.SaveChangesAsync(ct);
        return created;
    }

    public async Task<ShadowPrediction> EvaluateShadowRiskAsync(Guid deviceId,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        if(!await db.Devices.AnyAsync(x=>x.Id==deviceId,ct))throw new KeyNotFoundException("Device not found");
        var samples=await db.TelemetrySnapshots.AsNoTracking().Where(x=>x.DeviceId==deviceId)
            .OrderByDescending(x=>x.AtUtc).Take(10).ToListAsync(ct);
        var verified=samples.Where(x=>x.ChannelUtilizationPct is >=0 and <=100 &&
            x.ObservationSource.Contains("labeled values",StringComparison.OrdinalIgnoreCase))
            .Select(x=>x.ChannelUtilizationPct!.Value).ToList();
        decimal? risk=verified.Count>=3 ? verified.Average()/100m : null;
        var prediction=new ShadowPrediction{DeviceId=deviceId,CongestionRisk=risk,
            Reason=risk is null?"Insufficient firmware-certified samples; no model score.":"Illustrative shadow score from observed channel utilization; not a device change or a trained ML prediction."};
        db.ShadowPredictions.Add(prediction);
        await db.SaveChangesAsync(ct);
        return prediction;
    }
}
