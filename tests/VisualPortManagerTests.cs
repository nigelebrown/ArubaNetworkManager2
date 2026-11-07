using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jcf.NetworkManager.Tests;
public sealed class VisualPortManagerTests
{
    sealed class Factory(DbContextOptions<NetworkDbContext> opts):IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext()=>new(opts);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    private static Factory New()=>new(new DbContextOptionsBuilder<NetworkDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    [Fact]
    public async Task The_24_port_switch_uses_unverified_access_placeholders_and_only_observed_uplinks()
    {
        var f=New();var svc=new SwitchPortManagerService(f);
        var sw=new Device{Name="CX 6100-24",Kind=DeviceKind.Cx6100};
        await using(var db=f.CreateDbContext()){
            db.Devices.Add(sw);
            db.DeviceCapabilities.Add(new DeviceCapability{DeviceId=sw.Id,
                Operation=SiteConfigurationService.PortCountOperation,EvidenceReference="operator-declared:24"});
            db.PortObservations.Add(new PortObservation{DeviceId=sw.Id,Port="1/1/25",LinkState="up"});
            await db.SaveChangesAsync();
        }
        var interfaces=await svc.InventoryAsync(sw.Id);
        Assert.Equal(25,interfaces.Count);
        Assert.True(interfaces.Single(x=>x.Port=="1/1/24").ExpectedOnly);
        Assert.True(interfaces.Single(x=>x.Port=="1/1/25").Discovered);
        Assert.DoesNotContain(interfaces,x=>x.Port=="1/1/26");
    }
    [Fact]
    public async Task Confirmed_LLDP_may_suggest_names_and_bulk_mapping_is_versioned_and_never_executed()
    {
        var f=New();var svc=new SwitchPortManagerService(f);
        var site=new Location{Name="JCF Test Station"};
        var sw=new Device{Name="CX6200",Kind=DeviceKind.Cx6200,LocationId=site.Id};
        var ap=new Device{Name="Reception 01",Kind=DeviceKind.Ap535,LocationId=site.Id};
        await using(var db=f.CreateDbContext()){
            db.Locations.Add(site);db.Devices.AddRange(sw,ap);
            db.DeviceCapabilities.Add(new DeviceCapability{DeviceId=sw.Id,
                Operation=SiteConfigurationService.PortCountOperation,EvidenceReference="operator-declared:48"});
            db.TopologyLinks.Add(new TopologyLink{DeviceId=sw.Id,LocalPort="1/1/1",
                NeighborDeviceId=ap.Id,NeighborPort="eth0",Evidence="Confirmed"});
            await db.SaveChangesAsync();
        }
        var port=Assert.Single((await svc.InventoryAsync(sw.Id)).Where(x=>x.Port=="1/1/1"));
        Assert.Equal("AP-RECEPTION-01",port.SuggestedName);
        var profile=new SwitchPortManagerService.Profile("AP-ACCESS","AP","access",120,null,
            Array.Empty<int>(),"inherit-device","tx-rx");
        await svc.SaveProfileAsync(profile,"operator","Approved JCF access template");
        await svc.SaveBulkAsync(sw.Id,
            new[]{new SwitchPortManagerService.BulkItem("1/1/1",port.SuggestedName!,"AP","AP-ACCESS")},
            "operator","Reception AP patch map");
        var mapping=(await svc.InventoryAsync(sw.Id)).Single(x=>x.Port=="1/1/1").Mapping!;
        Assert.Equal("AP-RECEPTION-01",mapping.DisplayName);
        var csv=SwitchPortManagerService.ExportCsv(new[]{mapping});
        var parsed=Assert.Single(SwitchPortManagerService.ImportCsv(csv));
        Assert.Equal("AP-ACCESS",parsed.ProfileName);
        await svc.SaveBulkAsync(sw.Id,new[]{
            new SwitchPortManagerService.BulkItem("1/1/1","AP-RECEPTION-02","AP","AP-ACCESS")
        },"operator","Replacement AP port mapping");
        await svc.ResetAsync(sw.Id,"1/1/1","operator","Port name inherited from approved site");
        await using var check=f.CreateDbContext();
        Assert.Empty(await check.PolicyTemplates.Where(x=>x.Scope==SwitchPortManagerService.Scope).ToListAsync());
        Assert.Equal(2,await check.PolicyTemplates.CountAsync(x=>x.Scope==SwitchPortManagerService.HistoryScope));
        Assert.Empty(await check.ConfigExecutionEvents.ToListAsync());
    }
    [Fact]
    public void Profile_validation_prevents_implicit_trunk_all_or_invalid_vlan()
    {
        Assert.Throws<ArgumentException>(()=>SwitchPortManagerService.ValidateProfile(
            new("TRUNK","UPLINK","trunk",null,100,new[]{200},"inherit-device","tx-rx")));
        Assert.Throws<ArgumentException>(()=>SwitchPortManagerService.ValidateProfile(
            new("CX-PORT","DATA","access",1,null,Array.Empty<int>(),"enabled","tx-rx")));
    }
    [Fact]
    public void CSV_formula_injection_and_unknown_roles_cannot_be_stored_as_port_names()
    {
        var sample="Port,DisplayName,Role,ProfileName\n1/1/1,=CMD,AP,\n";
        var parsed=Assert.Single(SwitchPortManagerService.ImportCsv(sample));
        Assert.Equal("=CMD",parsed.DisplayName);
        Assert.Throws<ArgumentException>(()=>SwitchPortManagerService.ImportCsv(
            "Port,Name,Role\n1/1/1,AP,AP"));
    }
}