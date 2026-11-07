using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>Persist guided site/formation/building/addressing information without changing the
/// existing Device, Location or SQL schema. A wizard is a saved desired-state workflow only.
/// No network discovery or device writes occur in this service.</summary>
public sealed class SiteWizardService(IDbContextFactory<NetworkDbContext> factory)
{
    public const string MetadataScope="SiteWizardMetadata";
    public const string ProgressScope="SiteWizardProgress";
    public sealed record Details(string Code,string Name,string Type,string? Address,
        string Formation,string[] Buildings,string[] ManagementCidrs,string Notes);
    public sealed record Progress(Guid LocationId,int Step,string UpdatedBy,DateTime UpdatedUtc,
        string[] Confirmations);
    public sealed record Status(Guid LocationId,string SiteName,int CurrentStep,
        int Devices,int TemplatesAssigned,int ConfirmedLinks,int UncertainLinks,
        IReadOnlyList<string> Blockers);

    static string Key(string prefix,Guid id)=>prefix+"/"+id.ToString("N");
    static string Actor(string text)=>!string.IsNullOrWhiteSpace(text)&&text.Length<=140
        ?text:throw new UnauthorizedAccessException("Authenticated operator required.");
    public static Details Validate(Details d)
    {
        if(d is null)throw new ArgumentNullException(nameof(d));
        string Limited(string? v,int max,string label,bool required=false)
        {
            var normalized=(v??"").Trim();
            if(normalized.Length>max||(required&&normalized.Length==0)||
              normalized.Any(c=>char.IsControl(c)))
                throw new ArgumentException(label+" is missing or invalid.");
            return normalized;
        }
        var code=Limited(d.Code,24,"Site code",true).ToUpperInvariant();
        if(!Regex.IsMatch(code,@"^[A-Z0-9][A-Z0-9_-]{2,23}$"))
            throw new ArgumentException("Site code must be 3–24 uppercase letters/digits/underscore/hyphen.");
        var name=Limited(d.Name,120,"Site name",true);
        var kind=Limited(d.Type,40,"Site type",true);
        if(kind is not("Station" or "Branch" or "HQ" or "Division" or "Building" or "Zone" or "Area"))
            throw new ArgumentException("Unknown site category.");
        var buildings=(d.Buildings??Array.Empty<string>())
            .Select(x=>Limited(x,80,"Building name",true)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(buildings.Length>30)throw new ArgumentException("Maximum 30 buildings per site.");
        var networks=(d.ManagementCidrs??Array.Empty<string>())
            .Select(x=>Limited(x,49,"Management CIDR",true)).Distinct(StringComparer.Ordinal).ToArray();
        if(networks.Length>16)throw new ArgumentException("Maximum 16 management CIDRs.");
        foreach(var cidr in networks)
        {
            var parts=cidr.Split('/');
            if(parts.Length!=2 || !IPAddress.TryParse(parts[0],out var address) ||
               !int.TryParse(parts[1],out var bits) ||
               (address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork && bits is <0 or >32) ||
               (address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6 && bits is <0 or >128))
                throw new ArgumentException("Invalid site CIDR: "+cidr);
        }
        return new Details(code,name,kind,Limited(d.Address,200,"Address"),
            Limited(d.Formation,120,"Formation",true),buildings,networks,Limited(d.Notes,300,"Notes"));
    }
    public async Task<Location> SaveDetailsAsync(Guid? locationId,Details details,string actor,CancellationToken ct=default)
    {
        actor=Actor(actor);var data=Validate(details);
        await using var db=await factory.CreateDbContextAsync(ct);
        var otherRecords=await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==MetadataScope).ToListAsync(ct);
        if(otherRecords.Any(x=>x.LocationId!=locationId &&
            JsonSerializer.Deserialize<Details>(x.NonSecretJson)?.Code==data.Code))
            throw new InvalidOperationException("Site code is already registered.");
        Location site;
        if(locationId is Guid id)
            site=await db.Locations.SingleOrDefaultAsync(x=>x.Id==id,ct)
                ??throw new KeyNotFoundException("Site not found.");
        else
        {
            site=new Location();db.Locations.Add(site);
        }
        site.Name=data.Name;site.Type=data.Type;site.Address=data.Address;
        var existing=await db.PolicyTemplates.SingleOrDefaultAsync(
            p=>p.Scope==MetadataScope&&p.LocationId==site.Id,ct);
        if(existing is null)
        {
            existing=new PolicyTemplate{Scope=MetadataScope,LocationId=site.Id,
                Name=Key("metadata",site.Id),CreatedBy=actor};db.PolicyTemplates.Add(existing);
        }
        else existing.Version++;
        var body=JsonSerializer.Serialize(data);
        if(body.Length>6900)throw new ArgumentException("Site metadata exceeds capacity.");
        existing.NonSecretJson=body;existing.CreatedUtc=DateTime.UtcNow;existing.CreatedBy=actor;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Site wizard metadata",
            Target=site.Id.ToString("N"),Outcome="Saved metadata v"+existing.Version+"; no device writes"});
        await db.SaveChangesAsync(ct);
        return site;
    }
    public async Task<Details?> ReadDetailsAsync(Guid siteId,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var value=await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==MetadataScope&&x.LocationId==siteId)
            .Select(x=>x.NonSecretJson).SingleOrDefaultAsync(ct);
        return value is null?null:JsonSerializer.Deserialize<Details>(value);
    }
    public async Task<Progress> SaveProgressAsync(Guid siteId,int step,string[] confirmations,
        string actor,CancellationToken ct=default)
    {
        actor=Actor(actor);
        if(step is <1 or >9)throw new ArgumentException("Wizard step must be between 1 and 9.");
        var valid=new[]{"site-info","inventory","topology-reviewed","template-choice",
            "site-variance","device-variance","impact-reviewed","deployment-approval","readback-reviewed"};
        if(confirmations is null||confirmations.Length>9||confirmations.Any(x=>!valid.Contains(x)))
            throw new ArgumentException("Wizard confirmations must be allowlisted.");
        await using var db=await factory.CreateDbContextAsync(ct);
        if(!await db.Locations.AnyAsync(x=>x.Id==siteId,ct))throw new KeyNotFoundException("Site does not exist.");
        var value=new Progress(siteId,step,actor,DateTime.UtcNow,confirmations.Distinct().ToArray());
        var row=await db.PolicyTemplates.SingleOrDefaultAsync(p=>p.Scope==ProgressScope&&p.LocationId==siteId,ct);
        if(row is null)db.PolicyTemplates.Add(new PolicyTemplate{Scope=ProgressScope,
            Name=Key("wizard",siteId),LocationId=siteId,NonSecretJson=JsonSerializer.Serialize(value),CreatedBy=actor});
        else {row.Version++;row.NonSecretJson=JsonSerializer.Serialize(value);row.CreatedUtc=DateTime.UtcNow;row.CreatedBy=actor;}
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Site wizard progress",
            Target=siteId.ToString("N"),Outcome="Step "+step+"; desired-state only"});
        await db.SaveChangesAsync(ct);
        return value;
    }
    public async Task<Progress?> ReadProgressAsync(Guid siteId,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var value=await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==ProgressScope&&x.LocationId==siteId)
            .Select(x=>x.NonSecretJson).SingleOrDefaultAsync(ct);
        return value is null?null:JsonSerializer.Deserialize<Progress>(value);
    }
    public async Task<TopologyLink> ConfirmObservedLinkAsync(Guid linkId,string actor,
        string reason,CancellationToken ct=default)
    {
        actor=Actor(actor);
        if(string.IsNullOrWhiteSpace(reason)||reason.Trim().Length<10||reason.Length>180)
            throw new ArgumentException("Explain the manual topology verification (10–180 characters).");
        await using var db=await factory.CreateDbContextAsync(ct);
        var link=await db.TopologyLinks.SingleOrDefaultAsync(x=>x.Id==linkId,ct)
            ??throw new KeyNotFoundException("No observed topology link found.");
        if(link.NeighborDeviceId is not Guid peer || link.DeviceId==peer ||
            string.IsNullOrWhiteSpace(link.LocalPort)||string.IsNullOrWhiteSpace(link.NeighborPort))
            throw new InvalidOperationException("Link endpoints and interface identifiers must be known before confirmation.");
        var a=await db.Devices.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==link.DeviceId,ct);
        var b=await db.Devices.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==peer,ct);
        if(a is null||b is null || a.LocationId is null || b.LocationId is null ||
           a.LocationId!=b.LocationId)
            throw new InvalidOperationException("Both confirmed endpoints must exist in the same site inventory.");
        if(link.ObservedUtc<DateTime.UtcNow.AddDays(-7))
            throw new InvalidOperationException("Stale topology evidence: refresh device discovery first.");
        if(link.Evidence=="Confirmed")return link;
        var old=link.Evidence;
        link.Evidence="Confirmed";
        db.AuditEvents.Add(new AuditEvent {Actor=actor,Action="Confirm topology link",
            Target=link.Id.ToString("N"),Outcome="From "+old+"; "+reason.Trim()[..Math.Min(80,reason.Trim().Length)]});
        await db.SaveChangesAsync(ct);
        return link;
    }

    public async Task<Status> ReviewAsync(Guid siteId,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var location=await db.Locations.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==siteId,ct)
            ??throw new KeyNotFoundException("Site not found.");
        var details=await ReadDetailsAsync(siteId,ct);
        var devices=await db.Devices.AsNoTracking().Where(d=>d.LocationId==siteId).ToListAsync(ct);
        var deviceIds=devices.Select(x=>x.Id).ToHashSet();
        var catalog=await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==TemplateCatalogService.AssignmentScope).ToListAsync(ct);
        var assigned=catalog.Count(x=>x.Name.StartsWith("device/") &&
            Guid.TryParseExact(x.Name[7..],"N",out var id)&&deviceIds.Contains(id));
        var links=await db.TopologyLinks.AsNoTracking().Where(x=>deviceIds.Contains(x.DeviceId)).ToListAsync(ct);
        var confirmed=links.Count(x=>x.NeighborDeviceId is not null&&
            x.Evidence.Equals("Confirmed",StringComparison.OrdinalIgnoreCase));
        var uncertain=links.Count(x=>x.NeighborDeviceId is null||
            !x.Evidence.Equals("Confirmed",StringComparison.OrdinalIgnoreCase));
        var blockers=new List<string>();
        if(details is null)blockers.Add("Site metadata incomplete.");
        if(devices.Count==0)blockers.Add("No devices assigned.");
        if(assigned!=devices.Count)blockers.Add($"{devices.Count-assigned} devices need a global template.");
        if(uncertain>0)blockers.Add($"{uncertain} topology links need independent confirmation.");
        if(devices.Any(d=>!d.IsSimulated))blockers.Add("Physical-device deployment requires firmware acceptance, backups and a certified recovery pathway.");
        var step=(await ReadProgressAsync(siteId,ct))?.Step??1;
        return new Status(siteId,location.Name,step,devices.Count,assigned,confirmed,uncertain,blockers);
    }
}