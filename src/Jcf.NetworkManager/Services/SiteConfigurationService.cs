using System.Text.Json;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// SQL-persistent site membership and global-policy inheritance. Changes represent desired
/// configuration only; NEVER connects to devices and NEVER invokes ChangeWorkflow execution.
/// Global policy edits affect every linked site immediately on the NEXT effective-policy read.
/// </summary>
public sealed class SiteConfigurationService(IDbContextFactory<NetworkDbContext> factory)
{
    public const string SiteGroupKind="SiteConfig";
    public const string PortCountOperation="switch.physical-port-count";
    public sealed record SiteLink(Guid GlobalTemplateId,string OverridesJson);
    public sealed record EffectiveSite(Guid LocationId,Guid GlobalTemplateId,string GlobalName,int GlobalVersion,
        int SiteVersion,string GlobalJson,string OverrideJson,string EffectiveJson);

    private static string SafeName(string name)
    {
        name=(name??"").Trim();
        if(name.Length is <3 or >120 || !Regex.IsMatch(name,@"^[\p{L}\p{N}_ .()-]{3,120}$"))
            throw new ArgumentException("Configuration group name must be 3–120 readable non-secret characters.");
        return name;
    }
    private static string Actor(string value) =>
        !string.IsNullOrWhiteSpace(value)&&value.Length<=140?value:throw new UnauthorizedAccessException("Authenticated operator required.");

    public async Task<PolicyTemplate> CreateGlobalAsync(string name,string json,string actor,CancellationToken ct=default)
    {
        name=SafeName(name);actor=Actor(actor);
        var normalized=SiteConfigurationPolicy.Normalize(json);
        await using var db=await factory.CreateDbContextAsync(ct);
        if(await db.PolicyTemplates.AnyAsync(p=>p.Scope=="Global" && p.Name==name,ct))
            throw new InvalidOperationException("Global configuration group already exists.");
        var group=new PolicyTemplate{Name=name,Scope="Global",NonSecretJson=normalized,CreatedBy=actor};
        db.PolicyTemplates.Add(group);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Create global config group",Target=group.Id.ToString(),Outcome="Version 1, no device changes"});
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<PolicyTemplate> UpdateGlobalAsync(Guid globalId,string json,string actor,CancellationToken ct=default)
    {
        actor=Actor(actor);
        var normalized=SiteConfigurationPolicy.Normalize(json);
        await using var db=await factory.CreateDbContextAsync(ct);
        var global=await db.PolicyTemplates.SingleOrDefaultAsync(p=>p.Id==globalId && p.Scope=="Global",ct)
            ?? throw new KeyNotFoundException("Global config group not found.");
        if(global.NonSecretJson==normalized)return global;
        // Global updates must not turn existing sites into invalid effective configurations.
        var sites=await db.PolicyTemplates.Where(p=>p.Scope=="Site").ToListAsync(ct);
        foreach(var site in sites)
        {
            var link=ReadLink(site.NonSecretJson);
            if(link.GlobalTemplateId==globalId)
                SiteConfigurationPolicy.Resolve(normalized,link.OverridesJson);
        }
        db.PolicyTemplates.Add(new PolicyTemplate {
            Name=global.Name,Scope="GlobalHistory",NonSecretJson=global.NonSecretJson,
            Version=global.Version,CreatedBy=global.CreatedBy,CreatedUtc=global.CreatedUtc
        });
        global.Version++;
        global.NonSecretJson=normalized;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Revise global config",Target=globalId.ToString(),Outcome="Version "+global.Version+"; linked sites inherit on next read"});
        await db.SaveChangesAsync(ct);
        return global;
    }

    public async Task<PolicyTemplate> CreateSiteAsync(Guid locationId,Guid globalTemplateId,
        string overridesJson,string actor,CancellationToken ct=default)
    {
        actor=Actor(actor);
        var normalized=SiteConfigurationPolicy.Normalize(overridesJson);
        await using var db=await factory.CreateDbContextAsync(ct);
        var location=await db.Locations.FindAsync([locationId],ct)
            ?? throw new KeyNotFoundException("Site location not registered.");
        var global=await db.PolicyTemplates.SingleOrDefaultAsync(p=>p.Id==globalTemplateId && p.Scope=="Global",ct)
            ?? throw new KeyNotFoundException("Global config group not found.");
        if(await db.PolicyTemplates.AnyAsync(p=>p.Scope=="Site" && p.LocationId==locationId,ct))
            throw new InvalidOperationException("Site already has a config group; edit its existing inheritance.");
        SiteConfigurationPolicy.Resolve(global.NonSecretJson,normalized);
        var link=JsonSerializer.Serialize(new SiteLink(global.Id,normalized));
        if(link.Length>7000)throw new ArgumentException("Site overrides exceed SQL policy-template capacity.");
        var config=new PolicyTemplate{
            Name="Site: "+location.Name[..Math.Min(location.Name.Length,100)],
            Scope="Site",LocationId=locationId,NonSecretJson=link,CreatedBy=actor
        };
        var group=new DeviceGroup{
            Name="SITE-"+locationId.ToString("N"),
            Kind=SiteGroupKind,LocationId=locationId,
            Notes="Site configuration and device membership"
        };
        db.PolicyTemplates.Add(config);
        if(!await db.DeviceGroups.AnyAsync(g=>g.Kind==SiteGroupKind && g.LocationId==locationId,ct))
            db.DeviceGroups.Add(group);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Create site config group",Target=locationId.ToString(),
            Outcome="Inherits "+global.Name+" v"+global.Version+"; no device changes"});
        await db.SaveChangesAsync(ct);
        return config;
    }

    public async Task<PolicyTemplate> UpdateSiteAsync(Guid locationId,Guid globalTemplateId,
        string overridesJson,string actor,CancellationToken ct=default)
    {
        actor=Actor(actor);
        var overrides=SiteConfigurationPolicy.Normalize(overridesJson);
        await using var db=await factory.CreateDbContextAsync(ct);
        var site=await db.PolicyTemplates.SingleOrDefaultAsync(p=>p.Scope=="Site"&&p.LocationId==locationId,ct)
            ?? throw new KeyNotFoundException("Site configuration group not found.");
        var global=await db.PolicyTemplates.SingleOrDefaultAsync(p=>p.Id==globalTemplateId&&p.Scope=="Global",ct)
            ?? throw new KeyNotFoundException("Global configuration group not found.");
        SiteConfigurationPolicy.Resolve(global.NonSecretJson,overrides);
        var next=JsonSerializer.Serialize(new SiteLink(GlobalTemplateId:globalTemplateId,OverridesJson:overrides));
        if(next.Length>7000)throw new ArgumentException("Site policy exceeds SQL template capacity.");
        if(next==site.NonSecretJson)return site;
        db.PolicyTemplates.Add(new PolicyTemplate{
            Name=site.Name,Scope="SiteHistory",LocationId=locationId,
            NonSecretJson=site.NonSecretJson,Version=site.Version,
            CreatedBy=site.CreatedBy,CreatedUtc=site.CreatedUtc
        });
        site.NonSecretJson=next;site.Version++;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Revise site inheritance",Target=locationId.ToString(),
            Outcome="Site version "+site.Version+"; global "+global.Name+" v"+global.Version});
        await db.SaveChangesAsync(ct);
        return site;
    }

    public async Task<Device> AssignDeviceAsync(Guid deviceId,Guid locationId,int? physicalPortCount,string actor,CancellationToken ct=default)
    {
        actor=Actor(actor);
        await using var db=await factory.CreateDbContextAsync(ct);
        var device=await db.Devices.FindAsync([deviceId],ct)??throw new KeyNotFoundException("Device does not exist.");
        if(!await db.Locations.AnyAsync(l=>l.Id==locationId,ct))throw new KeyNotFoundException("Site not found.");
        if(!await db.PolicyTemplates.AnyAsync(p=>p.Scope=="Site"&&p.LocationId==locationId,ct))
            throw new InvalidOperationException("Create the site configuration group before assigning equipment.");
        var group=await db.DeviceGroups.SingleOrDefaultAsync(g=>g.Kind==SiteGroupKind&&g.LocationId==locationId,ct)
            ?? throw new InvalidOperationException("Site membership group missing; repair site configuration.");
        bool isCx=device.Kind is DeviceKind.Cx6100 or DeviceKind.Cx6200;
        if(isCx && physicalPortCount is not (24 or 48))
            throw new ArgumentException("CX 6100/6200 physical access-port count must be declared as 24 or 48.");
        if(!isCx && physicalPortCount.HasValue)
            throw new ArgumentException("Physical access-port count applies only to CX switches.");
        var oldGroups=await db.GroupMemberships.Where(x=>x.DeviceId==deviceId).ToListAsync(ct);
        var groupIds=oldGroups.Select(x=>x.GroupId).ToList();
        var oldSites=await db.DeviceGroups.Where(g=>groupIds.Contains(g.Id)&&g.Kind==SiteGroupKind).Select(g=>g.Id).ToListAsync(ct);
        foreach(var m in oldGroups.Where(m=>oldSites.Contains(m.GroupId)&&m.GroupId!=group.Id))
            db.GroupMemberships.Remove(m);
        if(!oldGroups.Any(m=>m.GroupId==group.Id))
            db.GroupMemberships.Add(new GroupMembership{DeviceId=device.Id,GroupId=group.Id});
        if(device.LocationId!=locationId)
        {
            var old=await db.DevicePlacements.Where(p=>p.DeviceId==deviceId&&p.ThroughUtc==null).ToListAsync(ct);
            var now=DateTime.UtcNow;
            foreach(var p in old)p.ThroughUtc=now;
            device.LocationId=locationId;
            db.DevicePlacements.Add(new DevicePlacement{
                DeviceId=deviceId,LocationId=locationId,FromUtc=now,ChangedBy=actor
            });
        }
        if(isCx)
        {
            var capability=await db.DeviceCapabilities.FirstOrDefaultAsync(c=>
                c.DeviceId==deviceId&&c.Operation==PortCountOperation,ct);
            if(capability is null)
            {
                capability=new DeviceCapability{DeviceId=deviceId,Operation=PortCountOperation};
                db.DeviceCapabilities.Add(capability);
            }
            capability.Firmware=device.Firmware??"Unknown";
            capability.State=ApiCapabilityState.Unknown;
            capability.EvidenceReference="operator-declared:"+physicalPortCount;
            capability.CheckedUtc=DateTime.UtcNow;
        }
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Assign device to site",Target=deviceId.ToString(),
            Outcome="Site "+locationId+"; "+(isCx?physicalPortCount+"-port CX":"AP")+
                "; desired policy only"});
        await db.SaveChangesAsync(ct);
        return device;
    }

    public async Task<EffectiveSite> ResolveSiteAsync(Guid locationId,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var site=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(
            p=>p.Scope=="Site"&&p.LocationId==locationId,ct)
            ??throw new KeyNotFoundException("No configuration group is assigned to this site.");
        var link=ReadLink(site.NonSecretJson);
        var global=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(
            p=>p.Id==link.GlobalTemplateId&&p.Scope=="Global",ct)
            ??throw new InvalidOperationException("Referenced global configuration no longer exists.");
        var effective=SiteConfigurationPolicy.Resolve(global.NonSecretJson,link.OverridesJson);
        return new EffectiveSite(locationId,global.Id,global.Name,global.Version,site.Version,
            global.NonSecretJson,link.OverridesJson,effective);
    }

    public static SiteLink ReadLink(string json)
    {
        try
        {
            var link=JsonSerializer.Deserialize<SiteLink>(json);
            if(link is null || link.GlobalTemplateId==Guid.Empty)throw new InvalidOperationException("Invalid site/global inheritance reference.");
            SiteConfigurationPolicy.Normalize(link.OverridesJson);
            return link;
        }
        catch(JsonException){throw new InvalidOperationException("Site config group contains an invalid global reference.");}
    }
}
