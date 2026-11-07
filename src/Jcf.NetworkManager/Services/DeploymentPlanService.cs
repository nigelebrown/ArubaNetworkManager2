using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Approval-gated, dependency-aware deployment orchestration with actual SQL-persisted
/// simulation, verification and rollback. No live CX/Instant write adapter is authorized
/// by this module. Real deployments are BLOCKED until firmware-certified backup/checkpoint,
/// management-path reachability, readback, service verification and recovery are implemented.
/// </summary>
public sealed class DeploymentPlanService(
    IDbContextFactory<NetworkDbContext> factory,
    TemplateCatalogService templates)
{
    public const string PlanScope="DeploymentPlanV1";
    public const string SimulatorScope="DeploymentSimulatorState";
    public sealed record Step(Guid DeviceId,string Name,string DesiredSha256,int Sequence,
        string State,string? BeforeSha256,string? AfterSha256,string? Detail);
    public sealed record Record(Guid Id,Guid SiteId,string SiteName,
        string ChangeClass,string RequestedBy,string Reason,DateTime CreatedUtc,
        string State,string PlanHash,string? FirstApprovedBy,string? SecondApprovedBy,
        IReadOnlyList<Step> Steps,IReadOnlyList<string> Blockers);
    private static readonly ConcurrentDictionary<Guid,SemaphoreSlim> Gates=new();
    private static readonly string[] Active={"PendingApproval","PendingSecondApproval","ApprovedSimulation"};
    private static string Key(Guid id)=>"plan/"+id.ToString("N");
    private static string SimulatorKey(Guid id)=>"simulator/"+id.ToString("N");
    private static string Actor(string text)=>!string.IsNullOrWhiteSpace(text)&&text.Length<=140?text:
       throw new UnauthorizedAccessException("Authenticated operator required.");
    private static string Reason(string text)=>!string.IsNullOrWhiteSpace(text)&&text.Trim().Length>=10&&text.Length<=200
       ?text.Trim():throw new ArgumentException("Provide a detailed deployment reason (10–200 characters).");
    private static string Digest(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Encode(Record record)
    {
        var json=JsonSerializer.Serialize(record);
        if(json.Length>6800)throw new InvalidOperationException(
            "Deployment plan exceeds SQL metadata capacity. Split into approved batches.");
        return json;
    }
    private static Record Decode(PolicyTemplate row)=>
        JsonSerializer.Deserialize<Record>(row.NonSecretJson)??throw new InvalidOperationException("Invalid deployment record.");

    private async Task<Record> DraftAsync(Guid siteId,DeploymentGraphPlanner.ChangeClass change,
        string requestedBy,string reason,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var site=await db.Locations.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==siteId,ct)
            ??throw new KeyNotFoundException("Site not found.");
        var devices=await db.Devices.AsNoTracking().Where(x=>x.LocationId==siteId)
            .OrderBy(x=>x.Name).ToListAsync(ct);
        if(devices.Count==0)throw new InvalidOperationException("Site has no assigned equipment.");
        if(devices.Count>16)throw new InvalidOperationException("Limit each controlled deployment batch to 16 devices.");
        var ids=devices.Select(x=>x.Id).ToHashSet();
        var evidence=await db.TopologyLinks.AsNoTracking().Where(x=>ids.Contains(x.DeviceId)).ToListAsync(ct);
        var mapping=await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==SwitchPortManagerService.Scope)
            .ToListAsync(ct);
        var roleLookup=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var row in mapping)
        {
            try {
                var item=JsonSerializer.Deserialize<SwitchPortManagerService.Mapping>(row.NonSecretJson);
                if(item is not null)roleLookup[item.DeviceId.ToString("N")+"/"+item.Port]=item.Role;
            }catch(JsonException){}
        }
        var topology=evidence.Where(x=>x.NeighborDeviceId.HasValue)
            .Select(x=>new DeploymentGraphPlanner.Link(x.DeviceId,x.NeighborDeviceId!.Value,
                x.LocalPort,roleLookup.GetValueOrDefault(x.DeviceId.ToString("N")+"/"+x.LocalPort)??"",
                x.Evidence.Equals("Confirmed",StringComparison.OrdinalIgnoreCase))).ToArray();
        var graph=DeploymentGraphPlanner.Calculate(devices,topology,change);
        var blockers=new List<string>(graph.Blockers);
        var hashes=new Dictionary<Guid,string>();
        foreach(var device in devices)
        {
            try {
                var resolved=await templates.ResolveDeviceAsync(device.Id,ct);
                hashes[device.Id]=Digest(resolved.Resolved.DesiredJson);
            }
            catch(Exception ex) when(ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                blockers.Add(device.Name+": unresolved template ("+ex.GetType().Name+").");
                hashes[device.Id]="UNKNOWN";
            }
        }
        if(devices.Any(x=>!x.IsSimulated))
        {
            blockers.Add("Real hardware requires per-SKU firmware certification, current readback, encrypted backup and tested rollback. No live write adapter is enabled.");
        }
        var order=graph.OrderedDevices.Count==devices.Count?graph.OrderedDevices:
           devices.OrderBy(x=>x.Name).Select(x=>x.Id).ToArray();
        var steps=order.Select((id,i)=>new Step(id,devices.First(x=>x.Id==id).Name,
            hashes[id],i+1,"Pending",null,null,null)).ToArray();
        var signature=Digest(siteId+"|"+change+"|"+
            string.Join("|",steps.Select(x=>x.DeviceId+"="+x.DesiredSha256))+"|"+
            string.Join("|",graph.Dependencies.Select(x=>x.Upstream+"->"+x.Downstream))+"|"+
            string.Join("|",blockers.OrderBy(x=>x,StringComparer.Ordinal)));
        return new Record(Guid.NewGuid(),siteId,site.Name,change.ToString(),
            requestedBy,reason,DateTime.UtcNow,"Draft",signature,
            null,null,steps,blockers.Distinct().ToArray());
    }

    public async Task<Record> CreateAsync(Guid siteId,DeploymentGraphPlanner.ChangeClass change,
        string actor,string reason,CancellationToken ct=default)
    {
        actor=Actor(actor);reason=Reason(reason);
        var draft=await DraftAsync(siteId,change,actor,reason,ct);
        await using var db=await factory.CreateDbContextAsync(ct);
        var row=new PolicyTemplate{
            Id=draft.Id,Name=Key(draft.Id),Scope=PlanScope,LocationId=siteId,
            NonSecretJson=Encode(draft with{State=draft.Blockers.Count==0?
                "PendingApproval":"BlockedPreflight"}),CreatedBy=actor
        };
        db.PolicyTemplates.Add(row);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Draft deployment sequence",
            Target=draft.Id.ToString("N"),Outcome="Dry run "+draft.Steps.Count+" devices; blockers "+draft.Blockers.Count});
        await db.SaveChangesAsync(ct);
        return Decode(row);
    }

    public async Task<Record> ApproveAsync(Guid planId,string actor,CancellationToken ct=default)
    {
        actor=Actor(actor);
        await using var db=await factory.CreateDbContextAsync(ct);
        var row=await db.PolicyTemplates.SingleOrDefaultAsync(p=>p.Id==planId&&p.Scope==PlanScope,ct)
            ??throw new KeyNotFoundException("Deployment plan not found.");
        var record=Decode(row);
        if(record.State is not("PendingApproval" or "PendingSecondApproval"))
            throw new InvalidOperationException("Plan is blocked or not pending independent approvals.");
        if(actor.Equals(record.RequestedBy,StringComparison.OrdinalIgnoreCase) ||
           actor.Equals(record.FirstApprovedBy,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Plan requester and the two approvers must be different users.");
        var current=await DraftAsync(record.SiteId,
            Enum.Parse<DeploymentGraphPlanner.ChangeClass>(record.ChangeClass),
            record.RequestedBy,record.Reason,ct);
        if(current.PlanHash!=record.PlanHash || current.Blockers.Count>0)
            throw new InvalidOperationException("Site topology, effective configuration or readiness changed since review.");
        record=record.State=="PendingApproval" ?
            record with{State="PendingSecondApproval",FirstApprovedBy=actor} :
            record with{State="ApprovedSimulation",SecondApprovedBy=actor};
        row.Version++;row.NonSecretJson=Encode(record);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Approve deployment dry run",
            Target=planId.ToString("N"),Outcome=record.State+"; no real device writes"});
        await db.SaveChangesAsync(ct);
        return record;
    }

    private static async Task<string> SimulatorStateAsync(NetworkDbContext db,Guid deviceId,CancellationToken ct)
    {
        var key=SimulatorKey(deviceId);
        return await db.PolicyTemplates.AsNoTracking().Where(p=>p.Scope==SimulatorScope&&p.Name==key)
            .Select(x=>x.NonSecretJson).SingleOrDefaultAsync(ct)??"UNCONFIGURED";
    }
    private static async Task SetSimulatedAsync(NetworkDbContext db,Guid deviceId,string sha,CancellationToken ct)
    {
        var key=SimulatorKey(deviceId);
        var row=await db.PolicyTemplates.SingleOrDefaultAsync(x=>x.Scope==SimulatorScope&&x.Name==key,ct);
        if(row is null)db.PolicyTemplates.Add(new PolicyTemplate{
            Name=key,Scope=SimulatorScope,NonSecretJson=sha,CreatedBy="simulation"
        });
        else{row.NonSecretJson=sha;row.Version++;row.CreatedUtc=DateTime.UtcNow;}
    }

    /// <summary>
    /// Full stop/verify/rollback loop for simulated records only.
    /// If a device is not simulated, this routine fails BEFORE contacting ANY equipment.
    /// The caller can inject a verification failure for recovery acceptance tests.
    /// </summary>
    public async Task<Record> RunSimulationAsync(Guid planId,string actor,
        Guid? injectFailureOnDevice=null,CancellationToken ct=default)
    {
        actor=Actor(actor);
        var gate=Gates.GetOrAdd(planId,_=>new SemaphoreSlim(1,1));
        if(!await gate.WaitAsync(0,ct))
            throw new InvalidOperationException("This deployment plan is already executing.");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            var row=await db.PolicyTemplates.SingleOrDefaultAsync(p=>p.Id==planId&&p.Scope==PlanScope,ct)
                ??throw new KeyNotFoundException("Deployment not found.");
            var record=Decode(row);
            if(record.State!="ApprovedSimulation"||record.Blockers.Count>0)
                throw new InvalidOperationException("Only fully reviewed and independently approved simulation plans may execute.");
            var nodes=await db.Devices.AsNoTracking().Where(x=>x.LocationId==record.SiteId).ToListAsync(ct);
            if(nodes.Count!=record.Steps.Count||nodes.Any(x=>!x.IsSimulated))
                throw new InvalidOperationException("Real hardware writes strictly blocked. Revalidate simulator site inventory.");
            var current=await DraftAsync(record.SiteId,
                Enum.Parse<DeploymentGraphPlanner.ChangeClass>(record.ChangeClass),
                record.RequestedBy,record.Reason,ct);
            if(current.PlanHash!=record.PlanHash || current.Blockers.Count>0)
                throw new InvalidOperationException("Template/topology/target changed after independent approval.");
            var executed=new List<Step>();
            var next=new List<Step>();
            record=record with{State="Simulating"};
            row.NonSecretJson=Encode(record);row.Version++;
            await db.SaveChangesAsync(ct);
            bool failed=false;
            foreach(var step in record.Steps)
            {
                ct.ThrowIfCancellationRequested();
                var before=await SimulatorStateAsync(db,step.DeviceId,ct);
                var work=step with{BeforeSha256=before,State="BackedUp"};
                // No REST / no physical API. The simulation applies only SQL hash state.
                await SetSimulatedAsync(db,step.DeviceId,step.DesiredSha256,ct);
                await db.SaveChangesAsync(ct); // readback must come from persisted SQL, not the in-memory tracking graph
                var after=await SimulatorStateAsync(db,step.DeviceId,ct);
                var verify=after==step.DesiredSha256 && injectFailureOnDevice!=step.DeviceId;
                work=work with{AfterSha256=after,State=verify?"Verified":"VerificationFailed",
                    Detail=verify?"Simulated readback matched desired fingerprint":"Simulated verification failed; recovery required"};
                executed.Add(work);next.Add(work);
                if(!verify){failed=true;break;}
                row.NonSecretJson=Encode(record with{Steps=next.Concat(
                    record.Steps.Skip(next.Count)).ToArray()});
                row.Version++;await db.SaveChangesAsync(ct);
            }
            if(failed)
            {
                var rollbackComplete=true;
                for(int i=executed.Count-1;i>=0;i--)
                {
                    var step=executed[i];
                    await SetSimulatedAsync(db,step.DeviceId,step.BeforeSha256??"UNCONFIGURED",ct);
                    await db.SaveChangesAsync(ct);
                    var restored=await SimulatorStateAsync(db,step.DeviceId,ct);
                    if(restored!=step.BeforeSha256)rollbackComplete=false;
                    executed[i]=step with{State=restored==step.BeforeSha256?"SimulatedRollbackVerified":"RecoveryRequired",
                        Detail="Simulation stop and reverse restore; no real hardware contacted"};
                }
                next=executed.Concat(record.Steps.Skip(executed.Count)).ToList();
                record=record with{State=rollbackComplete?"SimulationFailedRolledBack":"RecoveryRequired",
                    Steps=next.ToArray()};
            }
            else record=record with{State="SimulationVerified",Steps=next.ToArray()};
            row.NonSecretJson=Encode(record);row.Version++;
            db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Simulate dependency deployment",
                Target=planId.ToString("N"),Outcome=record.State+"; no hardware writes"});
            await db.SaveChangesAsync(ct);
            return record;
        }
        finally{gate.Release();}
    }
    public async Task<IReadOnlyList<Record>> ListAsync(Guid? siteId=null,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var rows=await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==PlanScope).ToListAsync(ct);
        return rows.Select(Decode).Where(x=>siteId is null||x.SiteId==siteId)
            .OrderByDescending(x=>x.CreatedUtc).ToArray();
    }
}