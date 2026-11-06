using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

public sealed class DeviceService(IDbContextFactory<NetworkDbContext> dbFactory,IEnumerable<IDeviceConnector> connectors,IConfiguration configuration,
    DeviceHttpFactory? http=null)
{
    public const string PollingActor="monitoring-worker";
    public sealed record ImportRow(int Line,string Name,string Outcome,bool Success,Guid? DeviceId);

    private string[] ManagementCidrs => configuration.GetSection("Jcf:AllowedManagementCidrs").Get<string[]>() ?? Array.Empty<string>();

    public async Task<Device> RegisterAsync(string name,DeviceKind kind,string ip,string secretRef,Guid? locationId,string actor,CancellationToken ct=default,
        string? clusterName=null)
    {
        ip=(ip??"").Trim();
        if (!AddressPolicy.IsAllowed(ip,ManagementCidrs)) throw new InvalidOperationException("Device IP is not inside an approved management subnet.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length>120) throw new InvalidOperationException("Name is required (1-120 characters).");
        // Secret identifiers are NOT credentials: they name an encrypted credential profile (or environment variables).
        secretRef=(secretRef??"").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(secretRef, @"^[A-Z0-9_]{1,40}$"))
            throw new InvalidOperationException("Credential profile must be letters, numbers or underscores.");
        clusterName=NormalizeCluster(clusterName);
        await using var db=await dbFactory.CreateDbContextAsync(ct);
        if (await db.Devices.AnyAsync(d=>d.ManagementIp==ip,ct)) throw new InvalidOperationException("IP address is already registered.");
        if(locationId.HasValue && !await db.Locations.AnyAsync(l=>l.Id==locationId,ct)) throw new InvalidOperationException("Location does not exist.");
        var d=new Device{Name=name.Trim(),Kind=kind,ManagementIp=ip,SecretRef=secretRef,LocationId=locationId,ClusterName=clusterName};
        db.Devices.Add(d);
        if (locationId is Guid loc)
            db.DevicePlacements.Add(new DevicePlacement {DeviceId=d.Id,LocationId=loc,ChangedBy=actor});
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Register device",Target=d.Id.ToString(),Outcome=$"{d.Kind} at {ip}"});
        await db.SaveChangesAsync(ct);
        return d;
    }

    static string? NormalizeCluster(string? cluster)
    {
        if(string.IsNullOrWhiteSpace(cluster)) return null;
        cluster=cluster.Trim();
        if(cluster.Length>60 || !Regex.IsMatch(cluster,@"^[\w. -]+$")) throw new InvalidOperationException("Cluster/VC name: up to 60 letters, numbers, spaces, dots, dashes or underscores.");
        return cluster;
    }

    public async Task<Device> UpdateAsync(Guid id,string name,string secretRef,string? clusterName,string actor,CancellationToken ct=default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length>120) throw new InvalidOperationException("Name is required (1-120 characters).");
        secretRef=(secretRef??"").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(secretRef, @"^[A-Z0-9_]{1,40}$")) throw new InvalidOperationException("Credential profile must be letters, numbers or underscores.");
        await using var db=await dbFactory.CreateDbContextAsync(ct);
        var d=await db.Devices.SingleOrDefaultAsync(x=>x.Id==id,ct) ?? throw new KeyNotFoundException("Device not found.");
        var changes=new List<string>();
        if(d.Name!=name.Trim()){changes.Add("name");d.Name=name.Trim();}
        if(!d.IsSimulated && d.SecretRef!=secretRef){changes.Add("credential profile");d.SecretRef=secretRef;}
        var cluster=NormalizeCluster(clusterName);
        if(d.ClusterName!=cluster){changes.Add("cluster");d.ClusterName=cluster;}
        if(changes.Count==0) return d;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Edit device",Target=d.Id.ToString(),Outcome="Changed "+string.Join(", ",changes)});
        await db.SaveChangesAsync(ct);
        return d;
    }

    public async Task SetEnabledAsync(Guid id,bool enabled,string actor,CancellationToken ct=default)
    {
        await using var db=await dbFactory.CreateDbContextAsync(ct);
        var d=await db.Devices.SingleOrDefaultAsync(x=>x.Id==id,ct) ?? throw new KeyNotFoundException("Device not found.");
        if(d.Enabled==enabled) return;
        d.Enabled=enabled;
        // Re-enabled devices show Unknown until the next read; never leave a stale "Disabled" status behind.
        d.LastStatus=enabled ? "Unknown" : "Disabled";
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action=enabled?"Enable device monitoring":"Disable device monitoring",Target=d.Id.ToString(),Outcome=d.Name});
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Decommissions a device: removes inventory and its observations. Change proposals, execution events,
    /// placements history and the audit trail are retained.</summary>
    public async Task RemoveAsync(Guid id,string actor,CancellationToken ct=default)
    {
        await using var db=await dbFactory.CreateDbContextAsync(ct);
        var d=await db.Devices.SingleOrDefaultAsync(x=>x.Id==id,ct) ?? throw new KeyNotFoundException("Device not found.");
        if(await db.ConfigProposals.AnyAsync(p=>p.DeviceId==id && p.State==ConfigProposalState.Executing,ct))
            throw new InvalidOperationException("A change is executing on this device; wait for it to finish.");
        await RemoveDeviceDataAsync(db,[id],ct);
        db.Devices.Remove(d);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Remove device",Target=id.ToString(),Outcome=$"{d.Name} {d.Kind} {d.ManagementIp}"});
        await db.SaveChangesAsync(ct);
    }

    internal static async Task RemoveDeviceDataAsync(NetworkDbContext db,IReadOnlyCollection<Guid> ids,CancellationToken ct)
    {
        db.TelemetrySnapshots.RemoveRange(await db.TelemetrySnapshots.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        db.RadioObservations.RemoveRange(await db.RadioObservations.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        db.PortObservations.RemoveRange(await db.PortObservations.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        db.ClientSessions.RemoveRange(await db.ClientSessions.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        db.ClusterMembers.RemoveRange(await db.ClusterMembers.Where(x=>ids.Contains(x.ConductorDeviceId)).ToListAsync(ct));
        db.DeviceCapabilities.RemoveRange(await db.DeviceCapabilities.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        db.GroupMemberships.RemoveRange(await db.GroupMemberships.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        db.ComplianceFindings.RemoveRange(await db.ComplianceFindings.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        db.TelemetryRollups.RemoveRange(await db.TelemetryRollups.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        db.ShadowPredictions.RemoveRange(await db.ShadowPredictions.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        db.TopologyLinks.RemoveRange(await db.TopologyLinks.Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct));
        foreach(var r in await db.Recommendations.Where(x=>ids.Contains(x.DeviceId) && x.State==RecommendationState.Pending).ToListAsync(ct))
            r.State=RecommendationState.Superseded;
        foreach(var a in await db.NetworkAlerts.Where(x=>x.DeviceId!=null && ids.Contains(x.DeviceId.Value) && x.State!="Resolved").ToListAsync(ct))
        { a.State="Resolved"; a.LastSeenUtc=DateTime.UtcNow; }
        foreach(var p in await db.DevicePlacements.Where(x=>ids.Contains(x.DeviceId) && x.ThroughUtc==null).ToListAsync(ct))
            p.ThroughUtc=DateTime.UtcNow;
        foreach(var p in await db.ConfigProposals.Where(x=>ids.Contains(x.DeviceId) &&
            (x.State==ConfigProposalState.PendingApproval || x.State==ConfigProposalState.PendingSecondApproval || x.State==ConfigProposalState.Approved)).ToListAsync(ct))
        { p.State=ConfigProposalState.Rejected; p.LastOutcome="Target device removed from inventory."; }
    }

    /// <summary>Reads the device's management certificate without trusting it (no credentials sent).</summary>
    public async Task<DeviceCertificateInfo> CaptureCertificateAsync(Guid id,CancellationToken ct=default)
    {
        if(http is null) throw new InvalidOperationException("Certificate capture is not available in this host.");
        await using var db=await dbFactory.CreateDbContextAsync(ct);
        var d=await db.Devices.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct) ?? throw new KeyNotFoundException("Device not found.");
        if(d.IsSimulated) throw new InvalidOperationException("Sample devices are simulated and have no certificate.");
        if(!AddressPolicy.IsAllowed(d.ManagementIp,ManagementCidrs)) throw new InvalidOperationException("Device IP no longer within approved management subnets.");
        return await http.CaptureCertificateAsync(d,ct);
    }

    public async Task PinCertificateAsync(Guid id,string? sha256,string actor,CancellationToken ct=default)
    {
        string? pin=null;
        if(!string.IsNullOrWhiteSpace(sha256))
            pin=SecretResolver.NormalizeFingerprint(sha256) ?? throw new ArgumentException("Fingerprint must be a SHA-256 value (64 hexadecimal characters).");
        await using var db=await dbFactory.CreateDbContextAsync(ct);
        var d=await db.Devices.SingleOrDefaultAsync(x=>x.Id==id,ct) ?? throw new KeyNotFoundException("Device not found.");
        if(d.IsSimulated) throw new InvalidOperationException("Sample devices are simulated and have no certificate.");
        d.TlsCertSha256=pin; d.TlsPinnedBy=pin is null?null:actor; d.TlsPinnedUtc=pin is null?null:DateTime.UtcNow;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action=pin is null?"Clear device certificate pin":"Pin device certificate",
            Target=d.Id.ToString(),Outcome=pin is null?"System trust store validation":"SHA-256 "+pin[..16]+"..."});
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Bulk onboarding. One device per line: <c>name,model,managementIp,credentialProfile[,location[,cluster]]</c>.
    /// Model accepts AP-505, AP-535, CX6100, CX6200 (or Ap505/505 etc.). Location is matched by name. A header line
    /// starting with "name" is ignored. Each row is validated and registered independently.
    /// </summary>
    public async Task<List<ImportRow>> ImportCsvAsync(string csv,string actor,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(csv)) throw new ArgumentException("Paste at least one CSV line.");
        var lines=csv.Replace("\r","").Split('\n');
        if(lines.Length>2000) throw new ArgumentException("Import is limited to 2,000 lines per batch.");
        Dictionary<string,Guid> locations;
        await using(var db=await dbFactory.CreateDbContextAsync(ct))
            locations=(await db.Locations.AsNoTracking().Select(l=>new{l.Name,l.Id}).ToListAsync(ct))
                .GroupBy(l=>l.Name.Trim(),StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First().Id,StringComparer.OrdinalIgnoreCase);
        var results=new List<ImportRow>();
        for(var i=0;i<lines.Length;i++)
        {
            var line=lines[i].Trim();
            if(line.Length==0 || line.StartsWith('#')) continue;
            var cells=SplitCsv(line);
            if(i==0 && cells[0].Equals("name",StringComparison.OrdinalIgnoreCase)) continue;
            var name=cells.ElementAtOrDefault(0)??"";
            try
            {
                if(cells.Count<4) throw new ArgumentException("Expected name,model,managementIp,credentialProfile[,location[,cluster]].");
                var kind=ParseModel(cells[1]) ?? throw new ArgumentException("Unknown model '"+cells[1]+"' (AP-505, AP-535, CX6100, CX6200).");
                Guid? loc=null;
                var locationName=cells.ElementAtOrDefault(4);
                if(!string.IsNullOrWhiteSpace(locationName))
                    loc=locations.TryGetValue(locationName.Trim(),out var id) ? id : throw new ArgumentException("Location '"+locationName+"' not found; create it first.");
                var d=await RegisterAsync(name,kind,cells[2],cells[3],loc,actor,ct,cells.ElementAtOrDefault(5));
                results.Add(new ImportRow(i+1,d.Name,"Registered",true,d.Id));
            }
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
            { results.Add(new ImportRow(i+1,name,ex.Message,false,null)); }
        }
        return results;
    }

    public static DeviceKind? ParseModel(string? model)
    {
        var m=Regex.Replace((model??"").ToUpperInvariant(),"[^A-Z0-9]","");
        if(m.StartsWith("ARUBA")) m=m[5..];
        return m switch {
            "AP505" or "505" => DeviceKind.Ap505, "AP535" or "535" => DeviceKind.Ap535,
            "CX6100" or "6100" => DeviceKind.Cx6100, "CX6200" or "6200" => DeviceKind.Cx6200,
            _ => null };
    }

    static List<string> SplitCsv(string line)
    {
        var cells=new List<string>(); var cur=new StringBuilder(); var quoted=false;
        for(var i=0;i<line.Length;i++)
        {
            var c=line[i];
            if(quoted){ if(c=='"' && i+1<line.Length && line[i+1]=='"'){cur.Append('"');i++;} else if(c=='"') quoted=false; else cur.Append(c); }
            else if(c=='"') quoted=true;
            else if(c==',' || c==';' || c=='\t'){cells.Add(cur.ToString().Trim());cur.Clear();}
            else cur.Append(c);
        }
        cells.Add(cur.ToString().Trim());
        return cells;
    }

    private IDeviceConnector ConnectorFor(Device device) => device.IsSimulated
        ? connectors.FirstOrDefault(c=>c.IsSimulator) ?? throw new NotSupportedException("Simulator unavailable.")
        : connectors.FirstOrDefault(c=>!c.IsSimulator && c.Supports(device.Kind)) ?? throw new NotSupportedException("Device adapter unavailable.");

    // One read probe per device per application process at a time. This prevents a background
    // poll and an operator acceptance run from corrupting cluster inventory or exhausting
    // Aruba's limited API sessions. Distributed poll scheduling remains a production HA gate.
    private static readonly ConcurrentDictionary<Guid,SemaphoreSlim> ProbeLocks = new();
    public async Task<ProbeResult> ProbeAsync(Guid deviceId,string actor,CancellationToken ct=default)
    {
        var gate = ProbeLocks.GetOrAdd(deviceId, _ => new SemaphoreSlim(1,1));
        await gate.WaitAsync(ct);
        try { return await ProbeCoreAsync(deviceId,actor,ct); }
        finally { gate.Release(); }
    }

    private async Task<ProbeResult> ProbeCoreAsync(Guid deviceId,string actor,CancellationToken ct)
    {
        await using var db=await dbFactory.CreateDbContextAsync(ct);
        var device=await db.Devices.SingleOrDefaultAsync(d=>d.Id==deviceId,ct)
            ?? throw new KeyNotFoundException("Unknown device.");
        if (!device.Enabled) throw new InvalidOperationException("Device disabled.");
        // Sample devices are never contacted, so the management-subnet policy does not apply to them.
        if (!device.IsSimulated && !AddressPolicy.IsAllowed(device.ManagementIp,ManagementCidrs))
            throw new InvalidOperationException("Device IP no longer within approved management subnets.");
        var connector=ConnectorFor(device);
        ProbeResult result;
        try {result=await connector.ProbeAsync(device,ct);}
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        { result=new ProbeResult(false,null,null,null,null,null,new(),new(),new(),DeviceErrors.Describe(ex,"Device API"),new(){"FAIL connection"}); }
        device.LastProbeUtc=DateTime.UtcNow;
        if(result.Connected) device.LastSeenUtc=DateTime.UtcNow;
        device.LastStatus=result.Connected && result.Issue is null ? "Healthy" : result.Connected?"Degraded":"Offline";
        device.LastIssue=result.Issue is { Length: > 300 } longIssue ? longIssue[..300] : result.Issue;
        if (result.Firmware is not null) device.Firmware=result.Firmware;
        db.TelemetrySnapshots.Add(new TelemetrySnapshot {
            DeviceId=device.Id,ApiConnected=result.Connected,ApCount=result.ApCount,ClientCount=result.ClientCount,
            PortCount=result.PortCount,VlanCount=result.VlanCount,
            RadioChannel=result.Observation?.Channel,ChannelWidthMhz=result.Observation?.WidthMhz,
            ChannelUtilizationPct=result.Observation?.UtilizationPct,NoiseFloorDbm=result.Observation?.NoiseFloorDbm,
            ClientRetryPct=result.Observation?.ClientRetryPct,ObservationSource=result.Observation?.Source??"Unavailable",
            ArmJson=JsonSerializer.Serialize(result.Arm),
            MetricAvailabilityJson=JsonSerializer.Serialize(result.Fields),Problem=result.Issue is { Length: > 200 } p ? p[..200] : result.Issue
        });
        if(result.Observation is RfObservation r)
            db.RadioObservations.Add(new RadioObservation{DeviceId=device.Id,Channel=r.Channel,WidthMhz=r.WidthMhz,
                UtilizationPct=r.UtilizationPct,NoiseFloorDbm=r.NoiseFloorDbm,ErrorPct=r.ClientRetryPct,
                Source=r.Source,Quality=device.IsSimulated?"Simulated":"Heuristic-uncertified"});
        if(result.PortReadings is {Count:>0}) {
            foreach(var port in result.PortReadings.Take(200))
                db.PortObservations.Add(new PortObservation{DeviceId=device.Id,Port=port.Name,LinkState=port.State??"Unknown",
                    RxBytes=port.RxBytes,TxBytes=port.TxBytes,RxErrors=port.RxErrors,TxErrors=port.TxErrors,
                    PoeWatts=port.PoeWatts,Source=device.IsSimulated?"SIMULATED":"AOS-CX REST interfaces"});
        }
        // Client identifiers are never stored in plaintext. A private HMAC key must be configured,
        // otherwise the platform records count/metric availability only. Simulated MACs are synthetic.
        if(result.Clients is {Count:>0})
        {
            var key=device.IsSimulated ? "SIMULATED-SAMPLE-CLIENTS-NOT-PERSONAL-DATA" : Environment.GetEnvironmentVariable("JCF_CLIENT_HASH_KEY");
            if(!string.IsNullOrEmpty(key) && key.Length>=32)
            {
                foreach(var client in result.Clients.Take(500))
                {
                    var hash=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes(client.Mac)));
                    var current=await db.ClientSessions.FirstOrDefaultAsync(x=>x.DeviceId==device.Id && x.ClientHash==hash && x.Ssid==client.Ssid,ct);
                    if(current is null)db.ClientSessions.Add(new ClientSession{DeviceId=device.Id,ClientHash=hash,
                        Ssid=client.Ssid,Band=client.Band,RssiDbm=client.RssiDbm,SnrDb=client.SnrDb,
                        AssociatedVlan=client.Vlan,Source=device.IsSimulated?"SIMULATED":"Instant show clients; identity only (uncertified CLI parse)"});
                    else { current.LastSeenUtc=DateTime.UtcNow; current.RssiDbm=client.RssiDbm??current.RssiDbm; current.SnrDb=client.SnrDb??current.SnrDb; }
                }
            }
        }
        if(result.Members.Count>0)
        {
            var old=await db.ClusterMembers.Where(m=>m.ConductorDeviceId==deviceId).ToListAsync(ct);
            db.ClusterMembers.RemoveRange(old);
            foreach(var member in result.Members.DistinctBy(m=>m.Name)) db.ClusterMembers.Add(new ClusterMember {
                ConductorDeviceId=deviceId,Name=member.Name,Model=member.Model,Zone=member.Zone,
                IpMasked=member.IpMasked,DiscoveryConfidence=member.Confidence
            });
        }
        var outageKey="APIDOWN:"+device.Id.ToString("N");
        var outage=await db.NetworkAlerts.FirstOrDefaultAsync(x=>x.Fingerprint==outageKey && x.State!="Resolved",ct);
        if(device.LastStatus!="Healthy") {
            var detail=("Management API did not complete all checks. "+(result.Issue??"")).Trim();
            if(detail.Length>500) detail=detail[..500];
            if(outage is null) db.NetworkAlerts.Add(new NetworkAlert{DeviceId=device.Id,Fingerprint=outageKey,
                Severity=device.LastStatus=="Offline"?"Critical":"Warning",Category="Device API unavailable",Detail=detail});
            else {outage.LastSeenUtc=DateTime.UtcNow;outage.Detail=detail;}
        }else if(outage is not null) {outage.State="Resolved";outage.LastSeenUtc=DateTime.UtcNow;}
        // Operator probes are audited. Scheduled polls are not: at fleet scale they would add hundreds of thousands of
        // append-only rows a day; their evidence is the telemetry snapshot itself.
        if(actor!=PollingActor)
            db.AuditEvents.Add(new AuditEvent {Actor=actor,Action="Read-only device probe",Target=deviceId.ToString(),Outcome=device.LastStatus});
        await db.SaveChangesAsync(ct);
        return result;
    }
}
