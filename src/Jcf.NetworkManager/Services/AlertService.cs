using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>Operator triage of alerts: Open -> Acknowledged -> Resolved. Device-API alerts also clear automatically
/// when the next probe is healthy; an acknowledged alert that recurs stays acknowledged.</summary>
public sealed class AlertService(IDbContextFactory<NetworkDbContext> factory)
{
    public async Task<NetworkAlert> TransitionAsync(Guid id,string state,string actor,CancellationToken ct=default)
    {
        if(state is not ("Acknowledged" or "Resolved")) throw new ArgumentException("Alert state must be Acknowledged or Resolved.");
        await using var db=await factory.CreateDbContextAsync(ct);
        var alert=await db.NetworkAlerts.SingleOrDefaultAsync(x=>x.Id==id,ct) ?? throw new KeyNotFoundException("Alert not found.");
        if(alert.State=="Resolved") throw new InvalidOperationException("Alert is already resolved.");
        if(alert.State==state) return alert;
        alert.State=state;
        alert.LastSeenUtc=DateTime.UtcNow;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action=state=="Resolved"?"Resolve alert":"Acknowledge alert",Target=id.ToString(),Outcome=alert.Category});
        await db.SaveChangesAsync(ct);
        return alert;
    }
}
