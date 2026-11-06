using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

public sealed class IncidentService(IDbContextFactory<NetworkDbContext> dbf)
{
    public async Task<OperationsIncident> OpenAsync(Guid? deviceId,Guid? locationId,string title,string description,string severity,string actor,CancellationToken ct=default)
    {
        description??="";
        if(string.IsNullOrWhiteSpace(title)||title.Length>120||description.Length>1200) throw new ArgumentException("Invalid incident description");
        if(!new[]{"Low","Medium","High","Critical"}.Contains(severity,StringComparer.Ordinal))throw new ArgumentException("Invalid severity");
        await using var db=await dbf.CreateDbContextAsync(ct);
        if(deviceId is Guid id && !await db.Devices.AnyAsync(x=>x.Id==id,ct)) throw new KeyNotFoundException("Device not registered");
        if(locationId is Guid loc && !await db.Locations.AnyAsync(x=>x.Id==loc,ct)) throw new KeyNotFoundException("Location not found");
        var incident=new OperationsIncident{DeviceId=deviceId,LocationId=locationId,Title=title,Description=description,
            Severity=severity};
        db.OperationsIncidents.Add(incident);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Open network incident",Target=incident.Id.ToString(),Outcome=severity});
        await db.SaveChangesAsync(ct);
        return incident;
    }
    public async Task<OperationsIncident> TransitionAsync(Guid id,string state,string? owner,string actor,CancellationToken ct=default)
    {
        if(!new[]{"Open","Acknowledged","Investigating","Resolved"}.Contains(state,StringComparer.Ordinal))
            throw new ArgumentException("Invalid incident status");
        await using var db=await dbf.CreateDbContextAsync(ct);
        var incident=await db.OperationsIncidents.FindAsync([id],ct)??throw new KeyNotFoundException("Incident not found");
        if(incident.Status=="Resolved")throw new InvalidOperationException("A resolved incident requires a new incident for subsequent faults.");
        incident.Status=state;
        if(owner is not null)incident.Owner=owner[..Math.Min(140,owner.Length)];
        if(state=="Resolved")incident.ClosedUtc=DateTime.UtcNow;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Update incident",Target=id.ToString(),Outcome=state});
        await db.SaveChangesAsync(ct);
        return incident;
    }
}
