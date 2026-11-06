using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Workflow;

public sealed class CapabilityService(IDbContextFactory<NetworkDbContext> factory)
{
    public async Task RecordAsync(Guid deviceId,ConfigAction action,ApiCapabilityState state,string evidence,string actor,bool isAdmin,CancellationToken ct=default)
    {
        if(!isAdmin) throw new UnauthorizedAccessException("Only a platform administrator can certify a capability.");
        if(state==ApiCapabilityState.WriteVerified && (string.IsNullOrWhiteSpace(evidence) || evidence.Length<8))
            throw new InvalidOperationException("Write certification must reference independently reviewed lab evidence.");
        await using var db=await factory.CreateDbContextAsync(ct);
        var device=await db.Devices.FindAsync([deviceId],ct) ?? throw new KeyNotFoundException("Device not found.");
        NetworkChangePolicy.CheckModel(device,action);
        if(string.IsNullOrWhiteSpace(device.Firmware)) throw new InvalidOperationException("Device firmware must be discovered before certification.");
        var operation=NetworkChangePolicy.Operation(action);
        var record=await db.DeviceCapabilities.SingleOrDefaultAsync(x=>x.DeviceId==deviceId && x.Operation==operation,ct);
        if(record is null) {record=new DeviceCapability{DeviceId=deviceId,Operation=operation};db.DeviceCapabilities.Add(record);}
        record.State=state;record.Firmware=device.Firmware;record.EvidenceReference=evidence[..Math.Min(evidence.Length,150)];record.CheckedUtc=DateTime.UtcNow;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Capability certification",Target=deviceId.ToString(),Outcome=operation+": "+state});
        await db.SaveChangesAsync(ct);
    }
}
