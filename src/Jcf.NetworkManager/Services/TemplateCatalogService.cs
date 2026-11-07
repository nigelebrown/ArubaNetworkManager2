using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Sprint-1 catalog, per-device template assignment, immutable revisions and explicit
/// approval-gated propagation. Reuses existing SQL PolicyTemplate/Recommendation/Audit tables
/// until the larger multi-entity schema migration is reviewed. Never calls device APIs.
/// </summary>
public sealed class TemplateCatalogService(IDbContextFactory<NetworkDbContext> factory)
{
    public const string ActiveScope="Catalog";
    public const string RetiredScope="RetiredCatalog";
    public const string HistoryScope="CatalogHistory";
    public const string AssignmentScope="DeviceCatalog";
    public const string RolloutScope="CatalogRolloutRequest";

    public sealed record Definition(string DeviceKind,int? AccessPorts,string PolicyJson,
        string[] MandatoryPaths,string Notes);
    public sealed record Binding(Guid TemplateId,int PinnedVersion,string DeviceOverrideJson,
        string OptimizationOverrideJson,Guid? OptimizationRecommendationId,
        string AssignedBy,string Reason,DateTime UpdatedUtc,string? PropagationApprovalId);
    public sealed record Rollout(Guid TemplateId,int TargetVersion,string RequestedBy,
        string Reason,string State,string? FirstApprovedBy,string? SecondApprovedBy,
        string ImpactSha256);
    public sealed record DeviceEffective(Device Device,PolicyTemplate Template,Definition Definition,
        Binding Binding,DeviceTemplateResolver.Result Resolved,string? SiteName);
    public sealed record Impact(Guid DeviceId,string DeviceName,string? Site,int PinnedVersion,
        int ProposedVersion,IReadOnlyList<string> ChangedPaths,string? Blocker);
    static readonly string Empty=SiteConfigurationPolicy.EmptyOverride;
    static string ValidActor(string actor)=>!string.IsNullOrWhiteSpace(actor) && actor.Length<=140?
        actor:throw new UnauthorizedAccessException("Authenticated actor is required.");
    static string ValidReason(string reason)=>!string.IsNullOrWhiteSpace(reason) && reason.Trim().Length>=10 &&
        reason.Length<=180?reason.Trim():throw new ArgumentException("Provide a change reason (10–180 characters).");
    static string ValidName(string name)=>!string.IsNullOrWhiteSpace(name) && name.Trim().Length<=120?
        name.Trim():throw new ArgumentException("Template name must be 1–120 characters.");
    static string VersionKey(Guid id,int version)=>"version/"+id.ToString("N")+"/"+version;
    static string AssignmentKey(Guid id)=>"device/"+id.ToString("N");
    static string RolloutKey(Guid id)=>"rollout/"+id.ToString("N");

    public static Definition NormalizeDefinition(Definition d)
    {
        if(!Enum.TryParse<DeviceKind>(d.DeviceKind,out var kind) || !Enum.IsDefined(kind))
            throw new ArgumentException("Unsupported hardware family.");
        var isAp=kind is DeviceKind.Ap505 or DeviceKind.Ap535;
        if(isAp && d.AccessPorts is not null || !isAp && d.AccessPorts is not(24 or 48))
            throw new ArgumentException("CX template needs 24/48 ports; AP template must not declare switch ports.");
        var policy=SiteConfigurationPolicy.Normalize(d.PolicyJson);
        using var parsed=JsonDocument.Parse(policy);
        if(isAp && parsed.RootElement.TryGetProperty("switch",out var sw) && sw.EnumerateObject().Any())
            throw new ArgumentException("AP template must not contain CX switch settings.");
        if(!isAp && parsed.RootElement.TryGetProperty("wireless",out var wlan) && wlan.EnumerateObject().Any())
            throw new ArgumentException("CX template must not contain AP radio/SSID settings.");
        var keys=parsed.RootElement.EnumerateObject().SelectMany(s=>s.Value.EnumerateObject()
            .Select(p=>s.Name+"."+p.Name)).ToHashSet(StringComparer.Ordinal);
        var mandatory=(d.MandatoryPaths??Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if(mandatory.Length>50 || mandatory.Any(x=>!keys.Contains(x)))
            throw new ArgumentException("Mandatory keys must identify settings in the global template.");
        if((d.Notes??"").Length>250)
            throw new ArgumentException("Template description too long.");
        return new Definition(kind.ToString(),d.AccessPorts,policy,mandatory,d.Notes??"");
    }
    static string Encode<T>(T data)
    {
        var json=JsonSerializer.Serialize(data);
        if(json.Length>6900)throw new ArgumentException("Catalog record exceeds existing SQL column capacity.");
        return json;
    }
    static T Decode<T>(string json)=>JsonSerializer.Deserialize<T>(json)
        ??throw new InvalidOperationException("Invalid template-catalog SQL record.");

    public async Task<PolicyTemplate> CreateAsync(string name,Definition definition,string actor,
        CancellationToken ct=default)
    {
        name=ValidName(name);actor=ValidActor(actor);
        var payload=Encode(NormalizeDefinition(definition));
        await using var db=await factory.CreateDbContextAsync(ct);
        if(await db.PolicyTemplates.AnyAsync(p=>p.Name==name&&(p.Scope==ActiveScope||p.Scope==RetiredScope),ct))
            throw new InvalidOperationException("A template with this name already exists.");
        var catalog=new PolicyTemplate {Name=name,Scope=ActiveScope,Version=1,
            NonSecretJson=payload,CreatedBy=actor};
        db.PolicyTemplates.Add(catalog);
        Audit(db,actor,"Create catalog template",catalog.Id,"Version 1; desired policy only");
        await db.SaveChangesAsync(ct);
        return catalog;
    }
    public async Task<PolicyTemplate> CloneAsync(Guid id,string name,string actor,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var src=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(
            p=>p.Id==id&&p.Scope==ActiveScope,ct)
            ??throw new KeyNotFoundException("Source catalog template not found.");
        return await CreateAsync(name,Decode<Definition>(src.NonSecretJson),actor,ct);
    }
    public async Task<PolicyTemplate> ReviseAsync(Guid id,Definition changed,string actor,
        string reason,CancellationToken ct=default)
    {
        actor=ValidActor(actor);reason=ValidReason(reason);
        var next=Encode(NormalizeDefinition(changed));
        await using var db=await factory.CreateDbContextAsync(ct);
        var catalog=await db.PolicyTemplates.SingleOrDefaultAsync(
            p=>p.Id==id&&p.Scope==ActiveScope,ct)
            ??throw new KeyNotFoundException("Active template not found.");
        var prior=Decode<Definition>(catalog.NonSecretJson);
        var incoming=Decode<Definition>(next);
        if(prior.DeviceKind!=incoming.DeviceKind||prior.AccessPorts!=incoming.AccessPorts)
            throw new ArgumentException("Device model/form factor cannot be changed in-place; clone a new template.");
        if(catalog.NonSecretJson==next)return catalog;
        db.PolicyTemplates.Add(new PolicyTemplate{Name=VersionKey(id,catalog.Version),
            Scope=HistoryScope,Version=catalog.Version,NonSecretJson=catalog.NonSecretJson,
            CreatedBy=catalog.CreatedBy,CreatedUtc=catalog.CreatedUtc});
        catalog.Version++; catalog.NonSecretJson=next;catalog.CreatedBy=actor;catalog.CreatedUtc=DateTime.UtcNow;
        Audit(db,actor,"Revise catalog template",catalog.Id,
            "Version "+catalog.Version+"; pinned devices unchanged; reason "+reason[..Math.Min(80,reason.Length)]);
        await db.SaveChangesAsync(ct);
        return catalog;
    }
    public async Task RetireAsync(Guid id,string actor,string reason,CancellationToken ct=default)
    {
        actor=ValidActor(actor);reason=ValidReason(reason);
        await using var db=await factory.CreateDbContextAsync(ct);
        var c=await db.PolicyTemplates.SingleOrDefaultAsync(
            p=>p.Id==id&&p.Scope==ActiveScope,ct)
            ??throw new KeyNotFoundException("Active catalog template not found.");
        c.Scope=RetiredScope;
        Audit(db,actor,"Retire catalog template",id,"Assignments stay pinned; "+reason[..Math.Min(70,reason.Length)]);
        await db.SaveChangesAsync(ct);
    }
    public async Task<PolicyTemplate> AssignAsync(Guid deviceId,Guid templateId,
        string actor,string reason,CancellationToken ct=default)
    {
        actor=ValidActor(actor);reason=ValidReason(reason);
        await using var db=await factory.CreateDbContextAsync(ct);
        var d=await db.Devices.SingleOrDefaultAsync(x=>x.Id==deviceId,ct)
            ??throw new KeyNotFoundException("Device not found.");
        if(d.LocationId is null)throw new InvalidOperationException("Assign the device to a site before selecting its template.");
        var c=await db.PolicyTemplates.SingleOrDefaultAsync(
            x=>x.Id==templateId&&x.Scope==ActiveScope,ct)
            ??throw new KeyNotFoundException("Active template not found.");
        await CheckDeviceAsync(db,d,Decode<Definition>(c.NonSecretJson),ct);
        var binding=new Binding(c.Id,c.Version,Empty,Empty,null,actor,reason,DateTime.UtcNow,null);
        var name=AssignmentKey(d.Id);
        var existing=await db.PolicyTemplates.SingleOrDefaultAsync(
            x=>x.Scope==AssignmentScope&&x.Name==name,ct);
        if(existing is null)
        {
            existing=new PolicyTemplate {Name=name,Scope=AssignmentScope,LocationId=d.LocationId,
                CreatedBy=actor};
            db.PolicyTemplates.Add(existing);
        }
        else
        {
            SnapshotAssignment(db,existing);
            existing.Version++;
        }
        existing.NonSecretJson=Encode(binding);existing.CreatedBy=actor;
        existing.CreatedUtc=DateTime.UtcNow;existing.LocationId=d.LocationId;
        Audit(db,actor,"Assign device catalog",deviceId,
            "Pinned "+c.Name+" v"+c.Version+"; desired only");
        await db.SaveChangesAsync(ct);
        return existing;
    }
    private static async Task CheckDeviceAsync(NetworkDbContext db,Device device,Definition def,
        CancellationToken ct)
    {
        if(!string.Equals(device.Kind.ToString(),def.DeviceKind,StringComparison.Ordinal))
            throw new InvalidOperationException("Template is incompatible with device model "+device.Kind);
        if(device.Kind is DeviceKind.Cx6100 or DeviceKind.Cx6200)
        {
            var capability=await db.DeviceCapabilities.AsNoTracking().SingleOrDefaultAsync(x=>
                x.DeviceId==device.Id&&x.Operation==SiteConfigurationService.PortCountOperation,ct);
            var portCount=capability is null?null:
                Jcf.NetworkManager.Workflow.SwitchPortInventoryPolicy.DeclaredCount(capability);
            if(portCount!=def.AccessPorts)
                throw new InvalidOperationException("Template 24/48-port form factor does not match the declared switch chassis.");
        }
    }
    static void SnapshotAssignment(NetworkDbContext db,PolicyTemplate existing)
    {
        db.PolicyTemplates.Add(new PolicyTemplate{
            Name="revision/"+existing.Id.ToString("N")+"/"+existing.Version,
            Scope="DeviceCatalogHistory",LocationId=existing.LocationId,Version=existing.Version,
            NonSecretJson=existing.NonSecretJson,CreatedBy=existing.CreatedBy,
            CreatedUtc=existing.CreatedUtc
        });
    }
    public async Task<PolicyTemplate> SetDeviceOverrideAsync(Guid deviceId,string overrides,
        string actor,string reason,CancellationToken ct=default)
    {
        actor=ValidActor(actor);reason=ValidReason(reason);
        var json=SiteConfigurationPolicy.Normalize(overrides);
        await using var db=await factory.CreateDbContextAsync(ct);
        var assignment=await db.PolicyTemplates.SingleOrDefaultAsync(
            p=>p.Scope==AssignmentScope&&p.Name==AssignmentKey(deviceId),ct)
            ??throw new KeyNotFoundException("Assign a global template to the device first.");
        var old=Decode<Binding>(assignment.NonSecretJson);
        var next=old with {DeviceOverrideJson=json,AssignedBy=actor,Reason=reason,UpdatedUtc=DateTime.UtcNow};
        await ResolveForBindingAsync(db,deviceId,next,ct);
        SnapshotAssignment(db,assignment);
        assignment.Version++;assignment.NonSecretJson=Encode(next);
        Audit(db,actor,"Device template variance",deviceId,"Desired override saved; pending deployment");
        await db.SaveChangesAsync(ct);
        return assignment;
    }
    public async Task<PolicyTemplate> AddApprovedOptimizationAsync(Guid deviceId,string overrideJson,
        Guid approvedRecommendationId,string actor,string reason,CancellationToken ct=default)
    {
        actor=ValidActor(actor);reason=ValidReason(reason);
        var normalized=SiteConfigurationPolicy.Normalize(overrideJson);
        await using var db=await factory.CreateDbContextAsync(ct);
        var rec=await db.Recommendations.SingleOrDefaultAsync(r=>r.Id==approvedRecommendationId
             && r.DeviceId==deviceId && r.State==RecommendationState.Approved,ct)
            ??throw new InvalidOperationException("The optimization recommendation is not independently approved for this device.");
        if(string.Equals(rec.ReviewedBy,actor,StringComparison.OrdinalIgnoreCase)||
           string.IsNullOrWhiteSpace(rec.ReviewedBy))
            throw new InvalidOperationException("Optimization must be reviewed by a different authenticated user.");
        // A generic approved recommendation is not authorization for an arbitrary setting.
        // The exact canonical approved proposal must match the selected device override.
        string approved;
        try {approved=SiteConfigurationPolicy.Normalize(rec.ProposedSetting);}
        catch(ArgumentException){throw new InvalidOperationException(
            "Approved recommendation does not carry a canonical supported configuration payload.");}
        if(!string.Equals(approved,normalized,StringComparison.Ordinal))
            throw new InvalidOperationException("Optimization override differs from the independently approved payload.");
        var assignment=await db.PolicyTemplates.SingleOrDefaultAsync(
            p=>p.Scope==AssignmentScope&&p.Name==AssignmentKey(deviceId),ct)
            ??throw new KeyNotFoundException("A global template assignment is required first.");
        var old=Decode<Binding>(assignment.NonSecretJson);
        var next=old with {OptimizationOverrideJson=normalized,
            OptimizationRecommendationId=approvedRecommendationId,AssignedBy=actor,
            Reason=reason,UpdatedUtc=DateTime.UtcNow};
        await ResolveForBindingAsync(db,deviceId,next,ct);
        SnapshotAssignment(db,assignment);
        assignment.Version++;assignment.NonSecretJson=Encode(next);
        Audit(db,actor,"Approved optimization desired override",deviceId,
            "Recommendation "+approvedRecommendationId.ToString("N")[..12]+"; no device writes");
        await db.SaveChangesAsync(ct);
        return assignment;
    }

    public async Task<DeviceEffective> ResolveDeviceAsync(Guid deviceId,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var a=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(
            p=>p.Scope==AssignmentScope&&p.Name==AssignmentKey(deviceId),ct)
            ??throw new KeyNotFoundException("Device does not have a catalog template assignment.");
        return await ResolveForBindingAsync(db,deviceId,Decode<Binding>(a.NonSecretJson),ct);
    }
    private static async Task<DeviceEffective> ResolveForBindingAsync(NetworkDbContext db,Guid id,
        Binding binding,CancellationToken ct)
    {
        var device=await db.Devices.AsNoTracking().SingleOrDefaultAsync(d=>d.Id==id,ct)
            ??throw new KeyNotFoundException("Device not found.");
        var catalog=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(
            c=>c.Id==binding.TemplateId&&(c.Scope==ActiveScope||c.Scope==RetiredScope),ct)
            ??throw new InvalidOperationException("Template has been deleted; retire templates rather than deleting records.");
        var pinned=await PinnedDefinitionAsync(db,catalog,binding.PinnedVersion,ct);
        var version=pinned.Definition;
        await CheckDeviceAsync(db,device,version,ct);
        var site= device.LocationId is Guid locationId
            ?await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(x=>x.Scope=="Site"&&x.LocationId==locationId,ct)
            :null;
        var siteOverrides=site is null?Empty:SiteConfigurationService.ReadLink(site.NonSecretJson).OverridesJson;
        siteOverrides=FilterForDevice(siteOverrides,device.Kind);
        var baseLayer=new DeviceTemplateResolver.Layer(version.PolicyJson,"G",binding.PinnedVersion,
            pinned.Author,"Pinned catalog revision",binding.PropagationApprovalId,pinned.ModifiedUtc);
        var siteLayer=new DeviceTemplateResolver.Layer(siteOverrides,"S",site?.Version??1,
            site?.CreatedBy??"site","Site-specific configuration variance",null,site?.CreatedUtc??DateTime.MinValue);
        var deviceLayer=new DeviceTemplateResolver.Layer(binding.DeviceOverrideJson,"D",
            1,binding.AssignedBy,binding.Reason,null,binding.UpdatedUtc);
        var opt=string.IsNullOrWhiteSpace(binding.OptimizationOverrideJson)||
            binding.OptimizationRecommendationId is null?null:
            new DeviceTemplateResolver.Layer(binding.OptimizationOverrideJson,"O",1,
                binding.AssignedBy,binding.Reason,binding.OptimizationRecommendationId.ToString(),binding.UpdatedUtc);
        var result=DeviceTemplateResolver.Resolve(baseLayer,siteLayer,deviceLayer,opt,
            mandatoryPaths:version.MandatoryPaths);
        return new DeviceEffective(device,catalog,version,binding,result,
            device.LocationId is Guid loc?(await db.Locations.AsNoTracking().SingleOrDefaultAsync(l=>l.Id==loc,ct))?.Name:null);
    }
    private static string FilterForDevice(string json,DeviceKind kind)
    {
        var parsed=JsonNode.Parse(SiteConfigurationPolicy.Normalize(json))!.AsObject();
        var wanted=kind is DeviceKind.Ap505 or DeviceKind.Ap535?"wireless":"switch";
        return parsed[wanted] is JsonObject section?
            new JsonObject{[wanted]=section.DeepClone()}.ToJsonString():Empty;
    }
    private sealed record PinnedRevision(Definition Definition,string Author,DateTime ModifiedUtc);
    private static async Task<PinnedRevision> PinnedDefinitionAsync(NetworkDbContext db,
        PolicyTemplate catalog,int version,CancellationToken ct)
    {
        if(catalog.Version==version)
            return new(Decode<Definition>(catalog.NonSecretJson),catalog.CreatedBy,catalog.CreatedUtc);
        var history=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(p=>
            p.Scope==HistoryScope&&p.Name==VersionKey(catalog.Id,version),ct)
            ??throw new InvalidOperationException("Immutable template revision missing.");
        return new(Decode<Definition>(history.NonSecretJson),history.CreatedBy,history.CreatedUtc);
    }
    public async Task<IReadOnlyList<Impact>> ImpactAsync(Guid templateId,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var current=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(
            p=>p.Id==templateId&&p.Scope==ActiveScope,ct)
            ??throw new KeyNotFoundException("Active catalog template not found.");
        var latest=Decode<Definition>(current.NonSecretJson);
        var assignments=await db.PolicyTemplates.AsNoTracking().Where(p=>p.Scope==AssignmentScope).ToListAsync(ct);
        var results=new List<Impact>();
        foreach(var a in assignments)
        {
            var binding=Decode<Binding>(a.NonSecretJson);
            if(binding.TemplateId!=templateId||binding.PinnedVersion>=current.Version)continue;
            if(!a.Name.StartsWith("device/",StringComparison.Ordinal) ||
                !Guid.TryParseExact(a.Name[7..],"N",out var deviceId))continue;
            try
            {
                var before=await ResolveForBindingAsync(db,deviceId,binding,ct);
                var next=await ResolveForBindingAsync(db,deviceId,binding with{PinnedVersion=current.Version},ct);
                var oldValues=before.Resolved.Settings.ToDictionary(x=>x.Path,x=>x.ValueJson);
                var newValues=next.Resolved.Settings.ToDictionary(x=>x.Path,x=>x.ValueJson);
                var changed=oldValues.Keys.Union(newValues.Keys,StringComparer.Ordinal)
                    .Where(key=>!oldValues.TryGetValue(key,out var beforeValue) ||
                                !newValues.TryGetValue(key,out var afterValue) ||
                                beforeValue!=afterValue)
                    .OrderBy(x=>x,StringComparer.Ordinal).ToArray();
                results.Add(new Impact(deviceId,before.Device.Name,before.SiteName,
                    binding.PinnedVersion,current.Version,changed,null));
            }
            catch(Exception ex) when(ex is InvalidOperationException or ArgumentException)
            {
                results.Add(new Impact(deviceId,a.Name,null,binding.PinnedVersion,current.Version,
                    Array.Empty<string>(),ex.Message));
            }
        }
        return results;
    }
    private static string ImpactHash(IReadOnlyList<Impact> rows)
    {
        var canonical=string.Join("\n",rows.OrderBy(x=>x.DeviceId)
            .Select(x=>x.DeviceId.ToString("N")+"|"+x.PinnedVersion+"|"+x.ProposedVersion+"|"+
                string.Join(",",x.ChangedPaths.OrderBy(p=>p,StringComparer.Ordinal))+"|"+x.Blocker));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
    public async Task<PolicyTemplate> RequestPropagationAsync(Guid templateId,string actor,
        string reason,CancellationToken ct=default)
    {
        actor=ValidActor(actor);reason=ValidReason(reason);
        await using var db=await factory.CreateDbContextAsync(ct);
        var template=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(
            x=>x.Id==templateId&&x.Scope==ActiveScope,ct)
            ??throw new KeyNotFoundException("Active catalog template not found.");
        var impact=await ImpactAsync(templateId,ct);
        if(impact.Any(i=>i.Blocker is not null))
            throw new InvalidOperationException("Template impact has invalid effective configurations; resolve conflicts first.");
        var req=new PolicyTemplate {
            Name=RolloutKey(Guid.NewGuid()),Scope=RolloutScope,
            NonSecretJson=Encode(new Rollout(templateId,template.Version,actor,reason,
                "PendingApproval",null,null,ImpactHash(impact))),
            CreatedBy=actor
        };
        db.PolicyTemplates.Add(req);
        Audit(db,actor,"Request catalog propagation",templateId,
            "Version "+template.Version+", affected "+impact.Count+", no writes");
        await db.SaveChangesAsync(ct);
        return req;
    }
    public async Task<PolicyTemplate> DecidePropagationAsync(Guid requestId,string actor,CancellationToken ct=default)
    {
        actor=ValidActor(actor);
        await using var db=await factory.CreateDbContextAsync(ct);
        var row=await db.PolicyTemplates.SingleOrDefaultAsync(x=>x.Id==requestId&&x.Scope==RolloutScope,ct)
            ??throw new KeyNotFoundException("Template propagation request not found.");
        var req=Decode<Rollout>(row.NonSecretJson);
        if(req.State is not("PendingApproval" or "PendingSecondApproval"))
            throw new InvalidOperationException("Request already decided.");
        if(string.Equals(actor,req.RequestedBy,StringComparison.OrdinalIgnoreCase) ||
           string.Equals(actor,req.FirstApprovedBy,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Requester and approvers must be different users.");
        var template=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(
            x=>x.Id==req.TemplateId&&x.Scope==ActiveScope,ct)
            ??throw new KeyNotFoundException("Catalog template retired or missing.");
        if(template.Version!=req.TargetVersion)
            throw new InvalidOperationException("Template changed after approval request; create a new impact assessment.");
        var currentImpact=await ImpactAsync(req.TemplateId,ct);
        if(!string.Equals(ImpactHash(currentImpact),req.ImpactSha256,StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Template rollout scope or settings changed after request; obtain a new impact approval.");
        if(req.State=="PendingApproval")
        {
            row.NonSecretJson=Encode(req with{State="PendingSecondApproval",FirstApprovedBy=actor});
            row.Version++;
            Audit(db,actor,"First catalog rollout approval",req.TemplateId,"Awaiting independent second reviewer");
            await db.SaveChangesAsync(ct);
            return row;
        }
        var impact=currentImpact;
        if(impact.Any(x=>x.Blocker is not null))
            throw new InvalidOperationException("Current effective-policy impact contains conflicts; rollout blocked.");
        // The entire update affects SQL DESIRED version pins only: there are no device/API writes.
        var rows=await db.PolicyTemplates.Where(p=>p.Scope==AssignmentScope).ToListAsync(ct);
        foreach(var assign in rows)
        {
            var binding=Decode<Binding>(assign.NonSecretJson);
            if(binding.TemplateId!=req.TemplateId||binding.PinnedVersion>=req.TargetVersion)continue;
            SnapshotAssignment(db,assign);
            assign.Version++;
            assign.NonSecretJson=Encode(binding with{PinnedVersion=req.TargetVersion,
                PropagationApprovalId=req.TargetVersion+":"+requestId.ToString("N"),
                UpdatedUtc=DateTime.UtcNow});
        }
        row.NonSecretJson=Encode(req with{State="ApprovedDesired",SecondApprovedBy=actor});
        row.Version++;
        Audit(db,actor,"Approve template desired propagation",req.TemplateId,
            "Revision "+req.TargetVersion+", devices "+impact.Count+"; NO hardware writes");
        await db.SaveChangesAsync(ct);
        return row;
    }
    public async Task<IReadOnlyList<PolicyTemplate>> ListAsync(CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        return await db.PolicyTemplates.AsNoTracking()
            .Where(x=>x.Scope==ActiveScope||x.Scope==RetiredScope)
            .OrderBy(x=>x.Name).ToListAsync(ct);
    }
    public static Definition ReadDefinition(PolicyTemplate p)=>Decode<Definition>(p.NonSecretJson);
    public static Binding ReadBinding(PolicyTemplate p)=>Decode<Binding>(p.NonSecretJson);
    public static Rollout ReadRollout(PolicyTemplate p)=>Decode<Rollout>(p.NonSecretJson);
    private static void Audit(NetworkDbContext db,string actor,string action,Guid target,string outcome)=>
        db.AuditEvents.Add(new AuditEvent {Actor=actor,Action=action,Target=target.ToString("N"),
            Outcome=outcome[..Math.Min(200,outcome.Length)]});
}
