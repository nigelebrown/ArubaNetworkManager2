using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jcf.NetworkManager.Tests;
public sealed class SiteWizardAndTopologyTests
{
    sealed class Factory(DbContextOptions<NetworkDbContext> options):IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext()=>new(options);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    [Fact]
    public async Task Step_one_site_registration_saves_formation_buildings_and_management_networks()
    {
        var f=new Factory(new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var wizard=new SiteWizardService(f);
        var data=new SiteWizardService.Details("jcf-sta-01","Sample Station",
            "Station","Main Road","St Andrew Division",new[]{"Main Building","Annexe"},
            new[]{"10.103.20.0/24"},"HQ and interview annexe");
        var created=await wizard.SaveDetailsAsync(null,data,"operator");
        var saved=await wizard.ReadDetailsAsync(created.Id);
        Assert.Equal("JCF-STA-01",saved!.Code);
        Assert.Equal(2,saved.Buildings.Length);
        Assert.Equal("St Andrew Division",saved.Formation);
        Assert.Equal("10.103.20.0/24",Assert.Single(saved.ManagementCidrs));
        var progress=await wizard.SaveProgressAsync(created.Id,3,
            new[]{"site-info","inventory"},"operator");
        Assert.Equal(3,(await wizard.ReadProgressAsync(created.Id))!.Step);
        await using var db=f.CreateDbContext();
        Assert.Empty(await db.ConfigExecutionEvents.ToListAsync());
        Assert.Contains(await db.AuditEvents.ToListAsync(),
            x=>x.Action=="Site wizard metadata");
    }
    [Fact]
    public async Task Fresh_observed_links_require_real_peers_and_manual_confirmation()
    {
        var f=new Factory(new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var wizard=new SiteWizardService(f);
        var site=new Location{Name="Station Site"};
        var cx=new Device{Name="CX6100",Kind=DeviceKind.Cx6100,LocationId=site.Id};
        var ap=new Device{Name="AP505",Kind=DeviceKind.Ap505,LocationId=site.Id};
        var link=new TopologyLink{DeviceId=cx.Id,LocalPort="1/1/1",NeighborDeviceId=ap.Id,
            NeighborPort="eth0",Evidence="LLDP",ObservedUtc=DateTime.UtcNow};
        await using(var db=f.CreateDbContext()){
            db.Locations.Add(site);db.Devices.AddRange(cx,ap);db.TopologyLinks.Add(link);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<ArgumentException>(()=>
            wizard.ConfirmObservedLinkAsync(link.Id,"operator","short"));
        var confirmed=await wizard.ConfirmObservedLinkAsync(link.Id,"operator",
            "Verified LLDP neighbor and switch patching from onsite register");
        Assert.Equal("Confirmed",confirmed.Evidence);
        var status=await wizard.ReviewAsync(site.Id);
        Assert.Equal(1,status.ConfirmedLinks);
        Assert.Equal(0,status.UncertainLinks);
        await using var verify=f.CreateDbContext();
        Assert.Contains(await verify.AuditEvents.ToListAsync(),x=>x.Action=="Confirm topology link");
    }
    [Theory]
    [InlineData("10.103.20.0/33")]
    [InlineData("not-a-network")]
    public void Invalid_site_management_networks_fail_before_sql_storage(string cidr)
    {
        var d=new SiteWizardService.Details("JCF-123","Example","Station","",
            "Formation",Array.Empty<string>(),new[]{cidr},"");
        Assert.Throws<ArgumentException>(()=>SiteWizardService.Validate(d));
    }
}