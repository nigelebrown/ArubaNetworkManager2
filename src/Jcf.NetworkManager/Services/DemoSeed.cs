using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

public static class DemoSeed
{
    /// <summary>Reserved documentation addresses (RFC 5737 TEST-NET-1): never routable, never collide with JCF equipment.</summary>
    public const string SampleAddressPrefix = "192.0.2.";

    public static async Task InitialiseAsync(IServiceProvider services,bool demo,bool sampleData)
    {
        await using var scope=services.CreateAsyncScope();
        var factory=scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        await using var db=await factory.CreateDbContextAsync();
        // Production always uses SQL Server. A non-relational store can only be substituted by the
        // automated test host, which has no migrations to verify.
        if(demo || !db.Database.IsRelational()) await db.Database.EnsureCreatedAsync();
        else {
            // Database migrations are applied out-of-band by the DBA after a tested backup.
            // Do not automatically execute DDL on an operational SQL Server/AG at application startup.
            var pending=await db.Database.GetPendingMigrationsAsync();
            var applied=await db.Database.GetAppliedMigrationsAsync();
            if(!applied.Any() || pending.Any())
                throw new InvalidOperationException("SQL schema is not at the reviewed migration level. Apply source-controlled migrations before application startup. Pending: "+string.Join(", ",pending));
        }
        if(!await db.AnalysisSchedules.AnyAsync()) db.AnalysisSchedules.Add(new AnalysisSchedule {Enabled=false});
        await db.SaveChangesAsync();
        if(sampleData) await SampleDataService.SeedAsync(db);
    }
}

/// <summary>
/// Sample inventory for demonstrations and training. Sample devices are flagged <see cref="Device.IsSimulated"/>, use
/// RFC 5737 addresses, are served only by the simulator and can be removed in one action without touching real equipment.
/// </summary>
public sealed class SampleDataService(IDbContextFactory<NetworkDbContext> factory)
{
    public static async Task SeedAsync(NetworkDbContext db)
    {
        if(await db.Devices.AnyAsync(d=>d.IsSimulated)) return;
        var area=await db.Locations.FirstOrDefaultAsync(l=>l.Name=="Sample Branch (demo)") ?? db.Locations.Add(new Location{Name="Sample Branch (demo)",Type="Branch"}).Entity;
        var lab=await db.Locations.FirstOrDefaultAsync(l=>l.Name=="Sample Station (demo)") ?? db.Locations.Add(new Location{Name="Sample Station (demo)",Type="Station",ParentId=area.Id}).Entity;
        var ap=new Device{Name="Sample AP-505",Kind=DeviceKind.Ap505,ManagementIp=DemoSeed.SampleAddressPrefix+"71",LocationId=lab.Id,SecretRef="SAMPLE",ClusterName="SAMPLE-VC",IsSimulated=true,Firmware="8.10.0.21"};
        var ap2=new Device{Name="Sample AP-535",Kind=DeviceKind.Ap535,ManagementIp=DemoSeed.SampleAddressPrefix+"72",LocationId=lab.Id,SecretRef="SAMPLE",ClusterName="SAMPLE-VC-2",IsSimulated=true};
        var cx=new Device{Name="Sample CX 6100",Kind=DeviceKind.Cx6100,ManagementIp=DemoSeed.SampleAddressPrefix+"21",LocationId=lab.Id,SecretRef="SAMPLE",IsSimulated=true};
        db.Devices.AddRange(ap,ap2,cx);
        foreach(var d in new[]{ap,ap2,cx}) db.DevicePlacements.Add(new DevicePlacement{DeviceId=d.Id,LocationId=lab.Id,ChangedBy="sample-data"});
        var group=await db.DeviceGroups.FirstOrDefaultAsync(g=>g.Name=="Sample access points (demo)") ?? db.DeviceGroups.Add(new DeviceGroup{Name="Sample access points (demo)",LocationId=lab.Id,Notes="Sample data"}).Entity;
        db.GroupMemberships.AddRange(new GroupMembership{GroupId=group.Id,DeviceId=ap.Id},new GroupMembership{GroupId=group.Id,DeviceId=ap2.Id});
        // Sample-only certification so the full approval -> execution flow can be rehearsed against the simulator.
        foreach(var op in new[]{"ssid.create","arm.band-steering.write","arm.min-power.write","arm.client-match.write","arm.channel-policy.write"})
            db.DeviceCapabilities.Add(new DeviceCapability{DeviceId=ap.Id,Firmware=ap.Firmware!,Operation=op,State=ApiCapabilityState.WriteVerified,EvidenceReference="SAMPLE-SIMULATED-ONLY"});
        db.AuditEvents.Add(new AuditEvent{Actor="system",Action="Seed sample data",Target="Sample inventory",Outcome="3 simulated devices (192.0.2.0/24)"});
        await db.SaveChangesAsync();
    }

    public async Task<int> RemoveAsync(string actor,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var sample=await db.Devices.Where(d=>d.IsSimulated).ToListAsync(ct);
        if(sample.Count==0) return 0;
        var ids=sample.Select(d=>d.Id).ToList();
        await DeviceService.RemoveDeviceDataAsync(db,ids,ct);
        db.Devices.RemoveRange(sample);
        var groups=await db.DeviceGroups.Where(g=>g.Name=="Sample access points (demo)").ToListAsync(ct);
        db.GroupMemberships.RemoveRange(await db.GroupMemberships.Where(m=>groups.Select(g=>g.Id).Contains(m.GroupId)).ToListAsync(ct));
        db.DeviceGroups.RemoveRange(groups);
        foreach(var name in new[]{"Sample Station (demo)","Sample Branch (demo)"})
        {
            var loc=await db.Locations.FirstOrDefaultAsync(l=>l.Name==name,ct);
            if(loc is not null && !await db.Devices.AnyAsync(d=>d.LocationId==loc.Id && !d.IsSimulated,ct) &&
               !await db.Locations.AnyAsync(l=>l.ParentId==loc.Id && l.Name!="Sample Station (demo)",ct))
                db.Locations.Remove(loc);
        }
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Remove sample data",Target="Sample inventory",Outcome=$"{sample.Count} simulated devices removed; real devices untouched"});
        await db.SaveChangesAsync(ct);
        return sample.Count;
    }

    public async Task<int> RestoreAsync(string actor,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        if(await db.Devices.AnyAsync(d=>d.IsSimulated,ct)) return 0;
        await SeedAsync(db);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Load sample data",Target="Sample inventory",Outcome="Simulated devices added"});
        await db.SaveChangesAsync(ct);
        return await db.Devices.CountAsync(d=>d.IsSimulated,ct);
    }
}
