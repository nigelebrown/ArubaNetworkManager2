using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Visual desired port records, audited bulk naming and reusable role profiles.
/// Actual port identifiers are discovered from the REST inventory; declared access-bank
/// ports shown before discovery are labelled EXPECTED, never hardware-certified.
/// Changes never invoke device-write adapters.
/// </summary>
public sealed class SwitchPortManagerService(IDbContextFactory<NetworkDbContext> factory)
{
    public const string Scope="PortMapping";
    public const string HistoryScope="PortMappingHistory";
    public const string RetiredScope="PortMappingRetired";
    public const string ProfileScope="CxPortProfile";
    public sealed record Profile(string Name,string Role,string Mode,int? AccessVlan,
        int? NativeVlan,int[] AllowedVlans,string PoeMode,string LldpMode);
    public sealed record Mapping(Guid DeviceId,string Port,string DisplayName,string Role,
        string ProfileName,string Reason,string Author,DateTime UpdatedUtc,int Version);
    public sealed record PortView(string Port,bool Discovered,bool ExpectedOnly,
        string? LinkState,string? NeighborName,string? NeighborPort,string Evidence,
        string? SuggestedName,Mapping? Mapping,CxSwitchReadback.Interface? Readback,
        decimal? UtilizationPct,decimal? PoeWatts,long? RxErrors,long? TxErrors);
    public sealed record BulkItem(string Port,string DisplayName,string Role,string ProfileName);
    private static readonly Regex PortPattern=new(
        @"^1/[1-9][0-9]?/[1-9][0-9]{0,2}$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly Regex NamePattern=new(
        @"^[A-Z0-9][A-Z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly string[] Roles={"UPLINK","AP","DATA","VOICE","PRINTER","CAMERA","SPARE"};
    static string Actor(string name)=>!string.IsNullOrWhiteSpace(name)&&name.Length<=140
       ?name:throw new UnauthorizedAccessException("Operator identity missing.");
    static string Reason(string reason)=>!string.IsNullOrWhiteSpace(reason)&&reason.Trim().Length>=10&&reason.Length<=180
       ?reason.Trim():throw new ArgumentException("Operational justification requires 10–180 characters.");
    static string Key(Guid deviceId,string port)=>"port/"+deviceId.ToString("N")+"/"+port.Replace('/','_');
    static string VersionName(Guid rowId,int version)=>"history/"+rowId.ToString("N")+"/"+version;
    private static string ValidPort(string value)
    {
        if(!PortPattern.IsMatch(value??""))throw new ArgumentException("Unsupported physical interface identifier.");
        return value;
    }
    private static string ValidName(string value)
    {
        value=(value??"").Trim().ToUpperInvariant();
        if(!NamePattern.IsMatch(value))throw new ArgumentException("Port name must be 1–64 characters: A-Z, 0-9, underscore and hyphen.");
        return value;
    }
    public static Profile ValidateProfile(Profile p)
    {
        var name=ValidName(p.Name);
        if(!Roles.Contains(p.Role))throw new ArgumentException("Unrecognized port role.");
        if(p.Mode is not("access" or "trunk"))throw new ArgumentException("Port mode must be access or trunk.");
        if(p.PoeMode is not("enabled" or "disabled" or "inherit-device"))
            throw new ArgumentException("Invalid PoE mode.");
        if(p.LldpMode is not("tx-rx" or "tx-only" or "rx-only" or "disabled"))
            throw new ArgumentException("Invalid LLDP mode.");
        static int V(int x)=>x is >=2 and <=4094?x:throw new ArgumentException("VLAN outside 2–4094.");
        var access=p.AccessVlan;
        var native=p.NativeVlan;
        var allowed=(p.AllowedVlans??Array.Empty<int>()).Distinct().Order().ToArray();
        if(allowed.Length>64)throw new ArgumentException("A port profile may contain at most 64 VLANs.");
        if(p.Mode=="access")
        {
            if(access is null || native is not null || allowed.Length>0)
                throw new ArgumentException("Access profile requires one VLAN and no trunk settings.");
            V(access.Value);
        }
        else
        {
            if(access is not null || native is null || allowed.Length==0)
                throw new ArgumentException("Trunk requires native VLAN and explicit allowed list.");
            V(native.Value);foreach(var vlan in allowed)V(vlan);
            if(!allowed.Contains(native.Value))throw new ArgumentException("Native VLAN must be in allowed set.");
        }
        return p with{Name=name,AllowedVlans=allowed};
    }
    public async Task<PolicyTemplate> SaveProfileAsync(Profile profile,string actor,
        string reason,CancellationToken ct=default)
    {
        actor=Actor(actor);reason=Reason(reason);
        var p=ValidateProfile(profile);
        await using var db=await factory.CreateDbContextAsync(ct);
        var name="profile/"+p.Name;
        var row=await db.PolicyTemplates.SingleOrDefaultAsync(x=>x.Scope==ProfileScope&&x.Name==name,ct);
        if(row is null){row=new PolicyTemplate{Scope=ProfileScope,Name=name,CreatedBy=actor};db.PolicyTemplates.Add(row);}
        else{Snapshot(db,row);row.Version++;}
        row.NonSecretJson=JsonSerializer.Serialize(p);row.CreatedUtc=DateTime.UtcNow;row.CreatedBy=actor;
        db.AuditEvents.Add(new AuditEvent {Actor=actor,Action="Save port role profile",
            Target=row.Id.ToString("N"),Outcome="Desired only; "+reason[..Math.Min(70,reason.Length)]});
        await db.SaveChangesAsync(ct);return row;
    }
    public async Task<IReadOnlyList<Profile>> ProfilesAsync(CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var rows=await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==ProfileScope).ToListAsync(ct);
        return rows.Select(x=>JsonSerializer.Deserialize<Profile>(x.NonSecretJson)!).ToArray();
    }

    public async Task<IReadOnlyList<PortView>> InventoryAsync(Guid deviceId,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var device=await db.Devices.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==deviceId,ct)
            ??throw new KeyNotFoundException("Switch not registered.");
        if(device.Kind is not(DeviceKind.Cx6100 or DeviceKind.Cx6200))
            throw new ArgumentException("Port manager supports CX 6100/6200 only.");
        var cap=await db.DeviceCapabilities.AsNoTracking().SingleOrDefaultAsync(x=>
            x.DeviceId==deviceId&&x.Operation==SiteConfigurationService.PortCountOperation,ct);
        var declared=cap is null?null:SwitchPortInventoryPolicy.DeclaredCount(cap);
        var samples=await db.PortObservations.AsNoTracking().Where(x=>x.DeviceId==deviceId)
            .OrderByDescending(x=>x.AtUtc).Take(300).ToListAsync(ct);
        var newest=samples.GroupBy(x=>x.Port).ToDictionary(x=>x.Key,x=>x.First(),StringComparer.Ordinal);
        var links=await db.TopologyLinks.AsNoTracking().Where(x=>x.DeviceId==deviceId).ToListAsync(ct);
        var linkByPort=links.GroupBy(x=>x.LocalPort).ToDictionary(x=>x.Key,x=>x.OrderByDescending(y=>y.ObservedUtc).First(),StringComparer.Ordinal);
        var rows=await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==Scope&&x.Name.StartsWith("port/"+deviceId.ToString("N")+"/"))
            .ToListAsync(ct);
        var mappings=rows.Select(x=>JsonSerializer.Deserialize<Mapping>(x.NonSecretJson)!)
            .ToDictionary(x=>x.Port,StringComparer.Ordinal);
        var snap=await db.ConfigurationSnapshots.AsNoTracking()
            .Where(x=>x.DeviceId==deviceId&&x.CapturedBy=="cx-interface-readonly")
            .OrderByDescending(x=>x.CapturedUtc).FirstOrDefaultAsync(ct);
        var readback=new Dictionary<string,CxSwitchReadback.Interface>(StringComparer.Ordinal);
        if(snap is not null)
        {
            try {
                using var json=JsonDocument.Parse(snap.SanitizedJson);
                foreach(var p in json.RootElement.GetProperty("interfaces").Deserialize<List<CxSwitchReadback.Interface>>()??new())
                    readback[p.Port]=p;
            }catch(JsonException){}
             catch(KeyNotFoundException){}
        }
        var discovered=new HashSet<string>(newest.Keys.Where(PortPattern.IsMatch),StringComparer.Ordinal);
        discovered.UnionWith(readback.Keys.Where(PortPattern.IsMatch));
        discovered.UnionWith(linkByPort.Keys.Where(PortPattern.IsMatch));
        var expected=new HashSet<string>(StringComparer.Ordinal);
        for(var i=1;i<=declared.GetValueOrDefault();i++)expected.Add($"1/1/{i}");
        // Only expected access interfaces may be rendered as placeholders. Uplinks/stack
        // members require real REST/LLDP evidence, so no 1/1/49+ is invented.
        var all=discovered.Union(expected,StringComparer.Ordinal).OrderBy(x=>int.TryParse(x.Split('/').Last(),out var n)?n:9999).ToArray();
        var siteNames=await db.Devices.AsNoTracking().Where(x=>x.LocationId==device.LocationId)
            .Select(x=>new{x.Id,x.Name,x.Kind}).ToListAsync(ct);
        var names=siteNames.ToDictionary(x=>x.Id,x=>x.Name);
        return all.Select(port=>{
            newest.TryGetValue(port,out var sample);
            linkByPort.TryGetValue(port,out var link);
            mappings.TryGetValue(port,out var mapping);
            readback.TryGetValue(port,out var current);
            var suggestion=SuggestName(link,siteNames.Select(x=>(x.Id,x.Kind,x.Name)).ToArray());
            return new PortView(port,discovered.Contains(port),!discovered.Contains(port),
                sample?.LinkState??current?.LinkState,
                link?.NeighborDeviceId is Guid peer?names.GetValueOrDefault(peer)??link.NeighborName:link?.NeighborName,
                link?.NeighborPort,link?.Evidence??"No LLDP evidence",
                suggestion,mapping,current,sample?.UtilizationPct,sample?.PoeWatts,sample?.RxErrors,sample?.TxErrors);
        }).ToArray();
    }

    public static string? SuggestName(TopologyLink? link,
        IReadOnlyList<(Guid Id,DeviceKind Kind,string Name)> knownDevices)
    {
        if(link is null || link.Evidence!="Confirmed"||link.NeighborDeviceId is not Guid neighbor)
            return null;
        var known=knownDevices.FirstOrDefault(x=>x.Id==neighbor);
        if(known.Id==Guid.Empty)return null;
        var prefix=known.Kind is DeviceKind.Ap505 or DeviceKind.Ap535?"AP":"UPLINK-TO";
        string Safe(string value)
        {
            var encoded=Regex.Replace(value.ToUpperInvariant(),@"[^A-Z0-9]+","-").Trim('-');
            return encoded.Length>40?encoded[..40]:encoded;
        }
        var suggested=prefix=="AP"?"AP-"+Safe(known.Name):prefix+"-"+Safe(known.Name);
        return suggested.Length<=64?suggested:suggested[..64];
    }

    public async Task<IReadOnlyList<Mapping>> SaveBulkAsync(Guid deviceId,
        IEnumerable<BulkItem> items,string actor,string reason,CancellationToken ct=default)
    {
        actor=Actor(actor);reason=Reason(reason);
        var values=items.ToList();
        if(values.Count==0 || values.Count>128)throw new ArgumentException("Bulk port change requires 1–128 entries.");
        if(values.GroupBy(x=>x.Port,StringComparer.Ordinal).Any(x=>x.Count()>1))
            throw new ArgumentException("Duplicate target interfaces.");
        foreach(var x in values){ValidPort(x.Port);ValidName(x.DisplayName);
          if(!Roles.Contains(x.Role))throw new ArgumentException("Unknown port role: "+x.Role);
          if(x.ProfileName.Length>64)throw new ArgumentException("Port profile reference too long.");}
        var inventory=await InventoryAsync(deviceId,ct);
        var supported=inventory.Select(x=>x.Port).ToHashSet(StringComparer.Ordinal);
        foreach(var p in values)
            if(!supported.Contains(p.Port))
                throw new InvalidOperationException("Cannot name an interface not in the declared/discovered inventory: "+p.Port);
        await using var db=await factory.CreateDbContextAsync(ct);
        var profiles=(await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==ProfileScope)
            .Select(x=>x.Name).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach(var item in values){
            if(item.ProfileName.Length>0 && !profiles.Contains("profile/"+ValidName(item.ProfileName)))
                throw new InvalidOperationException("Unknown reusable port role profile: "+item.ProfileName);
        }
        var now=DateTime.UtcNow;
        var saved=new List<Mapping>();
        foreach(var item in values){
            var key=Key(deviceId,item.Port);
            var row=await db.PolicyTemplates.SingleOrDefaultAsync(x=>x.Scope==Scope&&x.Name==key,ct);
            if(row is null) {row=new PolicyTemplate{Name=key,Scope=Scope,CreatedBy=actor};db.PolicyTemplates.Add(row);}
            else {Snapshot(db,row);row.Version++;}
            var mapping=new Mapping(deviceId,item.Port,ValidName(item.DisplayName),item.Role,
                item.ProfileName.Length==0?"":ValidName(item.ProfileName),reason,actor,now,row.Version);
            row.NonSecretJson=JsonSerializer.Serialize(mapping);row.CreatedBy=actor;row.CreatedUtc=now;
            saved.Add(mapping);
        }
        db.AuditEvents.Add(new AuditEvent {Actor=actor,Action="Bulk port naming/profile",
            Target=deviceId.ToString("N"),Outcome="Desired port changes "+values.Count+"; not deployed"});
        await db.SaveChangesAsync(ct);
        return saved;
    }
    public async Task ResetAsync(Guid deviceId,string port,string actor,string reason,
        CancellationToken ct=default)
    {
        actor=Actor(actor);reason=Reason(reason);ValidPort(port);
        await using var db=await factory.CreateDbContextAsync(ct);
        var existing=await db.PolicyTemplates.SingleOrDefaultAsync(x=>
            x.Scope==Scope&&x.Name==Key(deviceId,port),ct)
            ??throw new KeyNotFoundException("No local port override.");
        Snapshot(db,existing);existing.Scope=RetiredScope;existing.Version++;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Reset port override",
            Target=deviceId.ToString("N"),Outcome=port+" back to inheritance; not deployed"});
        await db.SaveChangesAsync(ct);
    }
    private static void Snapshot(NetworkDbContext db,PolicyTemplate row)
        =>db.PolicyTemplates.Add(new PolicyTemplate{Name=VersionName(row.Id,row.Version),
            Scope=HistoryScope,LocationId=row.LocationId,
            NonSecretJson=row.NonSecretJson,Version=row.Version,
            CreatedBy=row.CreatedBy,CreatedUtc=row.CreatedUtc});

    public static string ExportCsv(IEnumerable<Mapping> records)
    {
        static string Quote(string text)=>"\""+(text??"").Replace("\"","\"\"")+"\"";
        var s=new StringBuilder("Port,DisplayName,Role,ProfileName\r\n");
        foreach(var r in records.OrderBy(x=>x.Port,StringComparer.Ordinal)){
            s.Append(Quote(r.Port)).Append(',').Append(Quote(r.DisplayName)).Append(',')
             .Append(Quote(r.Role)).Append(',').Append(Quote(r.ProfileName)).Append("\r\n");
        }
        return s.ToString();
    }
    public static IReadOnlyList<BulkItem> ImportCsv(string csv)
    {
        if(string.IsNullOrWhiteSpace(csv)||csv.Length>35_000)throw new ArgumentException("CSV empty or too large.");
        var lines=csv.Replace("\r\n","\n").Split('\n').Where(x=>x.Length>0).ToArray();
        if(lines.Length is <2 or >129)
            throw new ArgumentException("Expected CSV header and up to 128 port rows.");
        static string[] Cells(string line)
        {
            var values=new List<string>();
            var cell=new StringBuilder();bool quote=false;
            for(int i=0;i<line.Length;i++){
                var c=line[i];
                if(c=='"'){
                    if(quote && i+1<line.Length&&line[i+1]=='"'){cell.Append('"');i++;}
                    else quote=!quote;
                }else if(c==','&&!quote){values.Add(cell.ToString());cell.Clear();}
                else cell.Append(c);
            }
            if(quote)throw new ArgumentException("Unclosed CSV quotation.");
            values.Add(cell.ToString());return values.ToArray();
        }
        if(!Cells(lines[0]).SequenceEqual(new[]{"Port","DisplayName","Role","ProfileName"}))
            throw new ArgumentException("Header must be Port,DisplayName,Role,ProfileName.");
        var result=new List<BulkItem>();
        foreach(var line in lines.Skip(1)){
            var v=Cells(line);
            if(v.Length!=4)throw new ArgumentException("Each CSV row must have four fields.");
            result.Add(new BulkItem(v[0],v[1],v[2],v[3]));
        }
        return result;
    }
}