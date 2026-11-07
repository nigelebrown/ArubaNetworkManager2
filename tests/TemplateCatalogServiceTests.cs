using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Jcf.NetworkManager.Tests;

public sealed class TemplateCatalogServiceTests
{
    sealed class Factory(DbContextOptions<NetworkDbContext> o):IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext()=>new(o);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    private static (Factory Factory,TemplateCatalogService Catalog) New()
    {
        var f=new Factory(new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return(f,new TemplateCatalogService(f));
    }
    static TemplateCatalogService.Definition Switch(DeviceKind kind,int ports,int vlan=100,string lldp="tx-rx")
        =>new(kind.ToString(),ports,
        """{"switch":{"dataVlan":""" + vlan +
        ""","voiceVlan":200,"managementVlan":300,"nativeVlan":100,"allowedVlans":[100,200,300,400,500],"lldpMode":""""+
        lldp+""""}}""",new[]{"switch.managementVlan"},"Standard switch ports");
    static TemplateCatalogService.Definition Ap(DeviceKind kind)
        =>new(kind.ToString(),null,
        """{"wireless":{"width24GhzMhz":20,"width5GhzMhz":20,"dfsEnabled":false,"minTxPowerDbm":6,"maxTxPowerDbm":12}}""",
        new[]{"wireless.width24GhzMhz","wireless.dfsEnabled"},"Standard wireless");

    [Fact]
    public async Task Six_different_switches_and_two_APs_at_one_site_can_pin_distinct_compatible_templates()
    {
        var(f,svc)=New();
        var site=new Location{Name="Eight Device Station",Type="Station"};
        var devices=new[]{
            new Device{Name="6100-24-A",Kind=DeviceKind.Cx6100},
            new Device{Name="6100-48-B",Kind=DeviceKind.Cx6100},
            new Device{Name="6200-24-C",Kind=DeviceKind.Cx6200},
            new Device{Name="6200-48-D",Kind=DeviceKind.Cx6200},
            new Device{Name="6100-24-E",Kind=DeviceKind.Cx6100},
            new Device{Name="6200-48-F",Kind=DeviceKind.Cx6200},
            new Device{Name="AP505",Kind=DeviceKind.Ap505},
            new Device{Name="AP535",Kind=DeviceKind.Ap535},
        };
        foreach(var d in devices){d.LocationId=site.Id;d.IsSimulated=true;d.ManagementIp="10.251.1."+ (Array.IndexOf(devices,d)+10);}
        await using(var db=f.CreateDbContext())
        {
            db.Locations.Add(site);db.Devices.AddRange(devices);
            for(int i=0;i<6;i++)
                db.DeviceCapabilities.Add(new DeviceCapability{
                    DeviceId=devices[i].Id,Operation=SiteConfigurationService.PortCountOperation,
                    EvidenceReference="operator-declared:"+(i is 0 or 2 or 4?24:48)
                });
            await db.SaveChangesAsync();
        }
        var catalog=new[]{
            await svc.CreateAsync("SW-6100-24",Switch(DeviceKind.Cx6100,24),"admin"),
            await svc.CreateAsync("SW-6100-48",Switch(DeviceKind.Cx6100,48),"admin"),
            await svc.CreateAsync("SW-6200-24",Switch(DeviceKind.Cx6200,24),"admin"),
            await svc.CreateAsync("SW-6200-48",Switch(DeviceKind.Cx6200,48),"admin"),
            await svc.CreateAsync("AP-505-GLOBAL",Ap(DeviceKind.Ap505),"admin"),
            await svc.CreateAsync("AP-535-GLOBAL",Ap(DeviceKind.Ap535),"admin"),
        };
        int[] selected={0,1,2,3,0,3,4,5};
        for(int i=0;i<8;i++)
            await svc.AssignAsync(devices[i].Id,catalog[selected[i]].Id,"operator","Standard site deployment");
        Assert.Equal(8,(await Task.WhenAll(devices.Select(d=>svc.ResolveDeviceAsync(d.Id)))).Length);
        await svc.SetDeviceOverrideAsync(devices[0].Id,
            """{"switch":{"dataVlan":400}}""","operator","Reception data VLAN");
        Assert.Contains("\"dataVlan\":400",(await svc.ResolveDeviceAsync(devices[0].Id)).Resolved.DesiredJson);
        Assert.Contains("\"dataVlan\":100",(await svc.ResolveDeviceAsync(devices[4].Id)).Resolved.DesiredJson);
        await using var check=f.CreateDbContext();
        Assert.Empty(await check.ConfigProposals.ToListAsync());
        Assert.Empty(await check.ConfigExecutionEvents.ToListAsync());
    }
    [Fact]
    public async Task Revision_is_pinned_until_two_independent_approvers_review_propagation()
    {
        var(f,svc)=New();
        var site=new Location{Name="Site A"};
        var d=new Device{Name="CX test",Kind=DeviceKind.Cx6100,LocationId=site.Id,ManagementIp="10.251.2.11"};
        await using(var db=f.CreateDbContext()){
            db.Locations.Add(site);db.Devices.Add(d);
            db.DeviceCapabilities.Add(new DeviceCapability{DeviceId=d.Id,
                Operation=SiteConfigurationService.PortCountOperation,
                EvidenceReference="operator-declared:24"});
            await db.SaveChangesAsync();
        }
        var old=await svc.CreateAsync("Standard24",Switch(DeviceKind.Cx6100,24),"admin");
        await svc.AssignAsync(d.Id,old.Id,"operator","Site standard baseline");
        await svc.ReviseAsync(old.Id,Switch(DeviceKind.Cx6100,24,100,"tx-only"),
            "admin","Updated LLDP security policy");
        Assert.Equal(1,(await svc.ResolveDeviceAsync(d.Id)).Binding.PinnedVersion);
        var impact=Assert.Single(await svc.ImpactAsync(old.Id));
        Assert.Contains("switch.lldpMode",impact.ChangedPaths);
        var req=await svc.RequestPropagationAsync(old.Id,"admin","Reviewed six switch impact");
        await Assert.ThrowsAsync<InvalidOperationException>(
            ()=>svc.DecidePropagationAsync(req.Id,"admin"));
        var one=await svc.DecidePropagationAsync(req.Id,"approver-A");
        Assert.Equal("PendingSecondApproval",TemplateCatalogService.ReadRollout(one).State);
        var two=await svc.DecidePropagationAsync(req.Id,"approver-B");
        Assert.Equal("ApprovedDesired",TemplateCatalogService.ReadRollout(two).State);
        var current=await svc.ResolveDeviceAsync(d.Id);
        Assert.Equal(2,current.Binding.PinnedVersion);
        Assert.Contains("\"lldpMode\":\"tx-only\"",current.Resolved.DesiredJson);
        await using var check=f.CreateDbContext();
        Assert.Single(await check.PolicyTemplates.Where(p=>p.Scope=="CatalogHistory").ToListAsync());
        Assert.Empty(await check.ConfigExecutionEvents.ToListAsync());
    }
    [Fact]
    public void Switch_and_AP_templates_reject_model_or_port_mismatch_and_raw_secrets()
    {
        Assert.Throws<ArgumentException>(()=>TemplateCatalogService.NormalizeDefinition(
            new("Cx6100",null,"""{"switch":{"dataVlan":100}}""",Array.Empty<string>(),"")));
        Assert.Throws<ArgumentException>(()=>TemplateCatalogService.NormalizeDefinition(
            new("Ap505",24,"""{"wireless":{"width24GhzMhz":20}}""",Array.Empty<string>(),"")));
        Assert.Throws<ArgumentException>(()=>TemplateCatalogService.NormalizeDefinition(
            new("Cx6200",48,"""{"switch":{"password":"abcd"}}""",Array.Empty<string>(),"")));
    }
}
