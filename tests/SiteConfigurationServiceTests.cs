using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class SiteConfigurationServiceTests
{
    const string Baseline = """
        {"switch":{"dataVlan":100,"voiceVlan":200,"managementVlan":300,
        "nativeVlan":100,"allowedVlans":[100,200,300],"lldpMode":"tx-rx"},
        "wireless":{"width24GhzMhz":20,"width5GhzMhz":40,
        "allowed5GhzChannels":[36,40],"dfsEnabled":false}}
        """;
    sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext()=>new(options);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    static (Factory db,SiteConfigurationService svc) New()
    {
        var options=new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var factory=new Factory(options);
        return(factory,new SiteConfigurationService(factory));
    }

    [Fact]
    public async Task Global_site_groups_assign_24_and_48_port_switches_and_APs_without_device_writes()
    {
        var (factory,svc)=New();
        var station=new Location{Name="Station A",Type="Station"};
        var branch=new Location{Name="Station B",Type="Station"};
        var cx24=new Device{Name="CX24",ManagementIp="10.20.0.2",Kind=DeviceKind.Cx6100};
        var cx48=new Device{Name="CX48",ManagementIp="10.20.0.3",Kind=DeviceKind.Cx6200};
        var ap=new Device{Name="AP535",ManagementIp="10.20.0.4",Kind=DeviceKind.Ap535};
        await using(var db=factory.CreateDbContext()) {
            db.Locations.AddRange(station,branch);db.Devices.AddRange(cx24,cx48,ap);
            await db.SaveChangesAsync();
        }
        var global=await svc.CreateGlobalAsync("JCF Global Baseline",Baseline,"admin");
        var site=await svc.CreateSiteAsync(station.Id,global.Id,
            """{"switch":{"dataVlan":110,"allowedVlans":[100,110,200,300]}}""","operator");
        Assert.Equal("Site",site.Scope);
        await svc.AssignDeviceAsync(cx24.Id,station.Id,24,"operator");
        await svc.AssignDeviceAsync(cx48.Id,station.Id,48,"operator");
        await svc.AssignDeviceAsync(ap.Id,station.Id,null,"operator");
        await using(var db=factory.CreateDbContext())
        {
            var locationDevices=await db.Devices.Where(d=>d.LocationId==station.Id).ToListAsync();
            Assert.Equal(3,locationDevices.Count);
            var siteGroup=Assert.Single(await db.DeviceGroups.Where(g=>g.Kind=="SiteConfig").ToListAsync());
            Assert.Equal(station.Id,siteGroup.LocationId);
            Assert.Equal(3,await db.GroupMemberships.CountAsync(m=>m.GroupId==siteGroup.Id));
            var p24=await db.DeviceCapabilities.SingleAsync(c=>c.DeviceId==cx24.Id);
            var p48=await db.DeviceCapabilities.SingleAsync(c=>c.DeviceId==cx48.Id);
            Assert.Equal("operator-declared:24",p24.EvidenceReference);
            Assert.Equal("operator-declared:48",p48.EvidenceReference);
            Assert.Equal(ApiCapabilityState.Unknown,p48.State);
            Assert.Equal(3,await db.DevicePlacements.CountAsync());
            Assert.Empty(await db.ConfigProposals.ToListAsync());
            Assert.Empty(await db.ConfigExecutionEvents.ToListAsync());
        }
        var resolved=await svc.ResolveSiteAsync(station.Id);
        Assert.Equal(global.Id,resolved.GlobalTemplateId);
        using var doc=JsonDocument.Parse(resolved.EffectiveJson);
        Assert.Equal(110,doc.RootElement.GetProperty("switch").GetProperty("dataVlan").GetInt32());
        Assert.Equal(200,doc.RootElement.GetProperty("switch").GetProperty("voiceVlan").GetInt32());

        // The same global group can be inherited by another station without copy/paste.
        await svc.CreateSiteAsync(branch.Id,global.Id,SiteConfigurationPolicy.EmptyOverride,"operator");
        var globalUpdated=await svc.UpdateGlobalAsync(global.Id,
            Baseline.Replace("tx-rx","tx-only"),"admin");
        Assert.Equal(2,globalUpdated.Version);
        var after=await svc.ResolveSiteAsync(station.Id);
        Assert.Equal(2,after.GlobalVersion);
        using var next=JsonDocument.Parse(after.EffectiveJson);
        Assert.Equal("tx-only",next.RootElement.GetProperty("switch").GetProperty("lldpMode").GetString());
        Assert.Equal(110,next.RootElement.GetProperty("switch").GetProperty("dataVlan").GetInt32());
        await using(var db=factory.CreateDbContext())
        {
            Assert.Single(await db.PolicyTemplates.Where(p=>p.Scope=="GlobalHistory").ToListAsync());
        }
    }

    [Fact]
    public async Task Moving_device_between_sites_removes_old_site_membership_but_retains_placement_history()
    {
        var (factory,svc)=New();
        var first=new Location{Name="HQ site",Type="HQ"};
        var second=new Location{Name="Other site",Type="Station"};
        var sw=new Device{Name="CX6100",ManagementIp="10.20.0.5",Kind=DeviceKind.Cx6100,IsSimulated=true};
        await using(var db=factory.CreateDbContext())
        {db.Locations.AddRange(first,second);db.Devices.Add(sw);await db.SaveChangesAsync();}
        var g=await svc.CreateGlobalAsync("JCF All Sites",Baseline,"admin");
        await svc.CreateSiteAsync(first.Id,g.Id,SiteConfigurationPolicy.EmptyOverride,"admin");
        await svc.CreateSiteAsync(second.Id,g.Id,SiteConfigurationPolicy.EmptyOverride,"admin");
        await svc.AssignDeviceAsync(sw.Id,first.Id,24,"operator");
        await svc.AssignDeviceAsync(sw.Id,second.Id,48,"operator");
        await using var check=factory.CreateDbContext();
        Assert.Equal(second.Id,(await check.Devices.SingleAsync()).LocationId);
        var groups=await check.DeviceGroups.Where(x=>x.Kind=="SiteConfig").ToListAsync();
        Assert.Single(await check.GroupMemberships.ToListAsync());
        var membership=await check.GroupMemberships.SingleAsync();
        Assert.Equal(groups.Single(gp=>gp.LocationId==second.Id).Id,membership.GroupId);
        var placements=await check.DevicePlacements.OrderBy(x=>x.FromUtc).ToListAsync();
        Assert.Equal(2,placements.Count);
        Assert.Single(placements.Where(x=>x.ThroughUtc is null && x.LocationId==second.Id));
        Assert.Single(placements.Where(x=>x.ThroughUtc is not null && x.LocationId==first.Id));
    }

    [Fact]
    public async Task Site_edit_can_change_parent_without_storing_unapproved_device_configuration()
    {
        var (factory,svc)=New();
        var loc=new Location{Name="Admin Office",Type="Station"};
        await using(var db=factory.CreateDbContext())
        {db.Locations.Add(loc);await db.SaveChangesAsync();}
        var a=await svc.CreateGlobalAsync("JCF Standard A",Baseline,"admin");
        var b=await svc.CreateGlobalAsync("JCF Standard B",Baseline.Replace("tx-rx","disabled"),"admin");
        await svc.CreateSiteAsync(loc.Id,a.Id,SiteConfigurationPolicy.EmptyOverride,"operator");
        var site=await svc.UpdateSiteAsync(loc.Id,b.Id,
            """{"switch":{"poePriority":"high"}}""","operator");
        Assert.Equal(2,site.Version);
        var effective=await svc.ResolveSiteAsync(loc.Id);
        Assert.Equal(b.Id,effective.GlobalTemplateId);
        Assert.Contains("disabled",effective.EffectiveJson);
        Assert.Contains("high",effective.EffectiveJson);
        await using var check=factory.CreateDbContext();
        Assert.Single(await check.PolicyTemplates.Where(x=>x.Scope=="SiteHistory").ToListAsync());
        Assert.Empty(await check.ConfigProposals.ToListAsync());
    }

    [Fact]
    public async Task Missing_site_template_and_invalid_hardware_port_count_fail_closed()
    {
        var (factory,svc)=New();
        var location=new Location{Name="Unconfigured Station",Type="Station"};
        var sw=new Device{Name="CX48",Kind=DeviceKind.Cx6200};
        await using(var db=factory.CreateDbContext()) {
            db.Locations.Add(location);db.Devices.Add(sw);await db.SaveChangesAsync();
        }
        // Wizard Step 2 intentionally assigns an existing device before choosing site policy.
        await svc.AssignDeviceAsync(sw.Id,location.Id,48,"operator");
        await using(var verify=factory.CreateDbContext())
        {
            Assert.Equal(location.Id,(await verify.Devices.SingleAsync()).LocationId);
            Assert.Single(await verify.DeviceGroups.Where(x=>x.Kind=="SiteConfig").ToListAsync());
            Assert.Empty(await verify.PolicyTemplates.Where(x=>x.Scope=="Site").ToListAsync());
        }
        var global=await svc.CreateGlobalAsync("JCF Baseline",Baseline,"admin");
        await svc.CreateSiteAsync(location.Id,global.Id,SiteConfigurationPolicy.EmptyOverride,"operator");
        await Assert.ThrowsAsync<ArgumentException>(
            ()=>svc.AssignDeviceAsync(sw.Id,location.Id,32,"operator"));
        await Assert.ThrowsAsync<ArgumentException>(
            ()=>svc.AssignDeviceAsync(sw.Id,location.Id,null,"operator"));
    }
}
