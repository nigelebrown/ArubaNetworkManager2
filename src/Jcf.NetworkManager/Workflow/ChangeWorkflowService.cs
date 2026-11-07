using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Workflow;

public sealed record ChangeExecutionOutcome(bool Success,bool Restored,bool ManualIntervention,string Detail);
public interface IApprovedChangeGateway
{
    Task<ChangeExecutionOutcome> ExecuteAsync(Device device, ConfigProposal proposal, CancellationToken ct);
}

/// <summary>Mock execution for SAMPLE (simulated) devices only.</summary>
public sealed class DemoChangeGateway : IApprovedChangeGateway
{
    public Task<ChangeExecutionOutcome> ExecuteAsync(Device device,ConfigProposal proposal,CancellationToken ct) =>
        Task.FromResult(device.IsSimulated
            ? new ChangeExecutionOutcome(true,true,false,"SIMULATION ONLY - sample device; configuration applied to mock state. No physical device contacted.")
            : new ChangeExecutionOutcome(false,true,false,"Refused: the simulation gateway never handles real equipment."));
}

/// <summary>Sends each approved change to the simulator (sample devices) or to the real Aruba gateway (registered devices).</summary>
public sealed class RoutingChangeGateway(DemoChangeGateway simulation,ArubaLabApprovedChangeGateway live) : IApprovedChangeGateway
{
    public Task<ChangeExecutionOutcome> ExecuteAsync(Device device,ConfigProposal proposal,CancellationToken ct) =>
        device.IsSimulated ? simulation.ExecuteAsync(device,proposal,ct) : live.ExecuteAsync(device,proposal,ct);
}

/// <summary>Immutable-payload, separation-of-duties and lab-gated orchestration.
/// All callers share this same application service; analysis/scheduler services have no gateway reference.</summary>
public sealed class ChangeWorkflowService(IDbContextFactory<NetworkDbContext> factory,IApprovedChangeGateway gateway,IConfiguration configuration)
{
    public async Task<ConfigProposal> SubmitAsync(Guid deviceId,ConfigAction action,string json,string actor,string reason,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(actor)) throw new UnauthorizedAccessException("Authenticated operator required.");
        var normalized=NetworkChangePolicy.Normalize(action,json);
        await using var db=await factory.CreateDbContextAsync(ct);
        var device=await db.Devices.FindAsync([deviceId],ct) ?? throw new KeyNotFoundException("Target device not found.");
        NetworkChangePolicy.CheckModel(device,action);
        var request=new ConfigProposal{DeviceId=deviceId,Action=action,PayloadJson=normalized,PayloadSha256=NetworkChangePolicy.Hash(normalized),
            RequestedBy=actor,Justification=reason,BaselineFirmware=device.Firmware};
        // Blast radius recorded for the approvers: the cluster size from the latest successful read of the VC.
        var snapshot=await db.TelemetrySnapshots.Where(x=>x.DeviceId==deviceId && x.ApiConnected).OrderByDescending(x=>x.AtUtc).FirstOrDefaultAsync(ct);
        request.BaselineClusterMemberCount=device.IsAccessPoint?snapshot?.ApCount:null;
        db.ConfigProposals.Add(request);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Submit change proposal",Target=request.Id.ToString(),Outcome=action.ToString()+" (no device write)"});
        await db.SaveChangesAsync(ct);
        return request;
    }
    public async Task<ConfigProposal> DecideAsync(Guid id,string decision,string actor,bool approverRole,CancellationToken ct=default)
    {
        if(!approverRole) throw new UnauthorizedAccessException("ChangeApprover role required.");
        await using var db=await factory.CreateDbContextAsync(ct);
        var p=await db.ConfigProposals.FindAsync([id],ct) ?? throw new KeyNotFoundException("Proposal not found.");
        if(p.State is not (ConfigProposalState.PendingApproval or ConfigProposalState.PendingSecondApproval))
            throw new InvalidOperationException("Proposal is not awaiting approval.");
        if(string.IsNullOrWhiteSpace(actor) || actor.Equals(p.RequestedBy,StringComparison.OrdinalIgnoreCase) || actor.Equals(p.ApprovedBy,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Approval must be by a different authorized person.");
        if(decision.Equals("reject",StringComparison.OrdinalIgnoreCase)) { p.State=ConfigProposalState.Rejected; }
        else if(decision.Equals("approve",StringComparison.OrdinalIgnoreCase)) {
            if(p.State==ConfigProposalState.PendingApproval) {
                p.ApprovedBy=actor;
                p.State=NetworkChangePolicy.RequiresSecondApprover(p.Action)?ConfigProposalState.PendingSecondApproval:ConfigProposalState.Approved;
            } else {p.SecondApprovedBy=actor;p.State=ConfigProposalState.Approved;}
            p.ApprovedPayloadSha256=p.PayloadSha256;
            p.ApprovedUtc=DateTime.UtcNow;
        } else throw new ArgumentException("Decision must be approve or reject.");
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Review change proposal",Target=p.Id.ToString(),Outcome=p.State.ToString()});
        await SaveOrConflictAsync(db,ct);
        return p;
    }
    public async Task<ConfigProposal> ExecuteAsync(Guid id,string actor,bool operatorRole,CancellationToken ct=default)
    {
        if(!operatorRole) throw new UnauthorizedAccessException("Operator/engineer role required.");
        await using var db=await factory.CreateDbContextAsync(ct);
        var p=await db.ConfigProposals.FindAsync([id],ct) ?? throw new KeyNotFoundException("Proposal not found.");
        if(p.State!=ConfigProposalState.Approved) throw new InvalidOperationException("Proposal is not fully approved.");
        if(string.IsNullOrWhiteSpace(actor)) throw new UnauthorizedAccessException("Authenticated executor required.");
        if(string.IsNullOrWhiteSpace(p.ApprovedBy) || p.RequestedBy.Equals(p.ApprovedBy,StringComparison.OrdinalIgnoreCase) ||
            (NetworkChangePolicy.RequiresSecondApprover(p.Action) && (string.IsNullOrWhiteSpace(p.SecondApprovedBy) ||
             p.SecondApprovedBy.Equals(p.ApprovedBy,StringComparison.OrdinalIgnoreCase))))
            throw new UnauthorizedAccessException("Independent approval(s) missing.");
        if(NetworkChangePolicy.Hash(p.PayloadJson)!=p.ApprovedPayloadSha256 || p.PayloadSha256!=p.ApprovedPayloadSha256)
            throw new InvalidOperationException("Proposal payload changed after approval.");
        if(p.ApprovedUtc is null || DateTime.UtcNow-p.ApprovedUtc>TimeSpan.FromHours(24))
            throw new InvalidOperationException("Approval expired; revalidate and approve again.");
        var device=await db.Devices.FindAsync([p.DeviceId],ct) ?? throw new KeyNotFoundException("Device not found.");
        NetworkChangePolicy.CheckModel(device,p.Action);
        // No firmware-certified Aruba write/readback/rollback adapter for these new planning controls.
        // They may be approval-tested against simulated devices, but NEVER executed on live equipment.
        if(!device.IsSimulated && ApProfilePolicy.ProposalOnly(p.Action))
            throw new InvalidOperationException("This WLAN/RF feature is proposal-only on real APs until firmware-certified readback and rollback tests pass.");
        if(!device.Enabled) throw new InvalidOperationException("Device administratively disabled.");
        if(p.BaselineFirmware!=device.Firmware) throw new InvalidOperationException("Firmware drift since proposal; re-probe and resubmit.");
        // Gates follow the TARGET, not the deployment mode: a sample (simulated) device only ever reaches the simulation
        // gateway, while a real device registered in a demo installation gets exactly the production controls.
        if(!device.IsSimulated) {
            var policy=DeviceWritePolicy.From(configuration);
            if(!policy.Enabled) throw new InvalidOperationException("Live device writes are disabled (Jcf:DeviceWrites:Enabled).");
            if(!policy.Permits(device.ManagementIp)) throw new InvalidOperationException("Target is not on the device-write allowlist (Jcf:DeviceWrites:AllowedCidrs/AllowedIps).");
            if(device.LastStatus!="Healthy" || device.LastSeenUtc is null || DateTime.UtcNow-device.LastSeenUtc>TimeSpan.FromMinutes(15))
                throw new InvalidOperationException("Fresh healthy device/API preflight required (probe the device, then execute within 15 minutes).");
            var capability=await db.DeviceCapabilities.FirstOrDefaultAsync(c=>c.DeviceId==device.Id && c.Operation==NetworkChangePolicy.Operation(p.Action),ct);
            if(capability is null || capability.State!=ApiCapabilityState.WriteVerified || capability.Firmware!=device.Firmware ||
                string.IsNullOrWhiteSpace(capability.EvidenceReference))
                throw new InvalidOperationException("Device+firmware write capability has not been certified.");
            if(device.IsAccessPoint) {
                var latest=await db.TelemetrySnapshots.Where(s=>s.DeviceId==device.Id && s.ApiConnected).OrderByDescending(s=>s.AtUtc).FirstOrDefaultAsync(ct);
                if(latest?.ApCount is not int live || p.BaselineClusterMemberCount is not int baseline)
                    throw new InvalidOperationException("Instant cluster size is unknown; re-probe the virtual controller and resubmit.");
                if(live!=baseline)
                    throw new InvalidOperationException($"Instant cluster membership changed since the proposal ({baseline} -> {live} APs); resubmit for approval.");
                if(live!=1 && !policy.AllowClusterWide)
                    throw new InvalidOperationException($"This change applies to all {live} APs in the Instant cluster; cluster-wide writes are disabled (Jcf:DeviceWrites:AllowClusterWide).");
            }
        }
        // Persist the in-progress state before the external call. Never retry a possibly applied write automatically.
        p.State=ConfigProposalState.Executing;
        db.ConfigExecutionEvents.Add(new ConfigExecutionEvent{ProposalId=p.Id,Stage="Preflight",Status="Pass",Detail="Approval, hash, firmware and lab controls validated."});
        // Claims the proposal atomically; a concurrent Execute for the same proposal fails here, before any device call.
        await SaveOrConflictAsync(db,ct);
        ChangeExecutionOutcome result;
        try { result=await gateway.ExecuteAsync(device,p,ct); }
        catch(Exception ex) { result=new(false,false,true,"Execution interrupted or failed. MANUAL INSPECTION REQUIRED: "+ex.GetType().Name); }
        p.State=result.Success?ConfigProposalState.Applied:result.ManualIntervention?ConfigProposalState.RecoveryRequired:ConfigProposalState.Failed;
        p.LastOutcome=result.Detail.Length>600?result.Detail[..600]:result.Detail;
        p.FinishedUtc=DateTime.UtcNow;
        db.ConfigExecutionEvents.Add(new ConfigExecutionEvent{ProposalId=p.Id,Stage="Execution/verification",Status=p.State.ToString(),Detail=p.LastOutcome});
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Execute approved change",Target=p.Id.ToString(),Outcome=p.State.ToString()});
        if(result.ManualIntervention) db.NetworkAlerts.Add(new NetworkAlert{DeviceId=device.Id,Fingerprint="RESTORE:"+p.Id.ToString("N"),
           Category="Configuration recovery required",Severity="Critical",Detail="Inspect target manually: "+p.Id});
        // A cancellation after a write attempt must still leave a recovery/audit record.
        await db.SaveChangesAsync(CancellationToken.None);
        return p;
    }

    static async Task SaveOrConflictAsync(NetworkDbContext db,CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch(DbUpdateConcurrencyException)
        { throw new InvalidOperationException("Proposal was changed by another user at the same time. Reload and check its current state."); }
    }
}
