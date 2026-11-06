using System.Net.Sockets;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>Server-origin diagnostics only. Never represents a real Wi-Fi client's DHCP/DNS performance.</summary>
public sealed class NetworkDiagnosticsService(IDbContextFactory<NetworkDbContext> factory,IConfiguration configuration)
{
    public async Task<ServicePathProbe> CheckRegisteredDeviceAsync(Guid deviceId,int port,string actor,CancellationToken ct=default)
    {
        if(port is not (22 or 443 or 4343)) throw new InvalidOperationException("Only approved management ports may be inspected.");
        await using var db=await factory.CreateDbContextAsync(ct);
        var device=await db.Devices.FindAsync([deviceId],ct) ?? throw new KeyNotFoundException("Device not found");
        var approved=configuration.GetSection("Jcf:AllowedManagementCidrs").Get<string[]>()??[];
        if(!AddressPolicy.IsAllowed(device.ManagementIp,approved)) throw new InvalidOperationException("Not in approved management subnet");
        string state,detail;
        try
        {
            using var socket=new TcpClient();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(device.ManagementIp,port,deadline.Token);
            state="Reachable";detail="TCP handshake succeeded from management application host. Device API authentication not tested.";
        }
        catch(Exception ex) when(ex is SocketException or OperationCanceledException)
        {
            if(ct.IsCancellationRequested)throw;
            state="Unavailable";detail="Management TCP probe failed: "+ex.GetType().Name;
        }
        var record=new ServicePathProbe{DeviceId=deviceId,Type="TCP",Target=$"{device.ManagementIp}:{port}",
            Status=state,Detail=detail,TestAgent="JCF Network Manager server"};
        db.ServicePathProbes.Add(record);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Read-only TCP management diagnostic",Target=deviceId.ToString(),Outcome=state});
        await db.SaveChangesAsync(ct);
        return record;
    }
}
