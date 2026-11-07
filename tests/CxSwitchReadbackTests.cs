using System.Text.Json;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class CxSwitchReadbackTests
{
    const string Interfaces = """
    {
      "1/1/1":{"vlan_mode":"access","vlan_tag":"/rest/v10.13/system/vlans/120",
        "vlan_trunks":[],"admin_state":"up","link_state":"up",
        "description":"Reception AP", "password":"NEVER_STORE_CX_PASSWORD"},
      "1/1/25":{"vlan_mode":"native-untagged","vlan_tag":"/rest/v10.13/system/vlans/100",
        "vlan_trunks":["/rest/v10.13/system/vlans/200","/rest/v10.13/system/vlans/300"],
        "admin_state":"up","description":"Upstream WAN","api_token":"NEVER_STORE_TOKEN"},
      "1/1/26":{"vlan_mode":"unrecognized","vlan_tag":null},
      "vlan10":{"vlan_mode":"access","vlan_tag":"/rest/v10.13/system/vlans/10"}
    }
    """;

    [Fact]
    public void CX_interface_readback_exposes_only_typed_nonsecret_properties()
    {
        using var doc=JsonDocument.Parse(Interfaces);
        var parsed=CxSwitchReadback.Parse(doc.RootElement);
        Assert.True(parsed.IsUsable);
        Assert.Equal(3,parsed.Interfaces.Count);
        var access=parsed.Interfaces.Single(x=>x.Port=="1/1/1");
        Assert.Equal(120,access.NativeOrAccessVlan);
        Assert.Equal("access",access.Mode);
        Assert.Equal("Reception AP",access.Description);
        Assert.Equal("up",access.AdminState);
        Assert.Empty(access.TaggedVlans!);
        var trunk=parsed.Interfaces.Single(x=>x.Port=="1/1/25");
        Assert.Equal("native-untagged",trunk.Mode);
        Assert.Equal(100,trunk.NativeOrAccessVlan);
        Assert.Equal(new[]{200,300},trunk.TaggedVlans);
        Assert.Null(parsed.Interfaces.Single(x=>x.Port=="1/1/26").Mode);
        var saved=JsonSerializer.Serialize(parsed);
        Assert.DoesNotContain("NEVER_STORE_CX_PASSWORD",saved);
        Assert.DoesNotContain("NEVER_STORE_TOKEN",saved);
        Assert.DoesNotContain("api_token",saved);
        Assert.DoesNotContain("password",saved,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_or_invalid_fields_remain_unknown_not_false_compliance()
    {
        using var doc=JsonDocument.Parse("""{"1/1/1":{"vlan_mode":"trunk","vlan_trunks":["bad"]},"1/1/2":{"vlan_mode":"unexpected"}}""");
        var p=CxSwitchReadback.Parse(doc.RootElement);
        var first=p.Interfaces.Single(x=>x.Port=="1/1/1");
        Assert.Null(first.TaggedVlans);
        Assert.Null(first.NativeOrAccessVlan);
        Assert.Equal("trunk",first.Mode);
        Assert.Null(p.Interfaces.Single(x=>x.Port=="1/1/2").Mode);
    }

    [Fact]
    public void CX_readback_flags_outside_site_vlan_pool_without_guessing_undisclosed_membership()
    {
        using var doc=JsonDocument.Parse(Interfaces.Replace("/vlans/120","/vlans/999"));
        var p=CxSwitchReadback.Parse(doc.RootElement);
        var effective=SiteConfigurationPolicy.Normalize(
            """{"switch":{"allowedVlans":[100,200,300]}}""");
        var findings=CxConfigurationReview.Compare(effective,p.Interfaces);
        Assert.Contains(findings,x=>x.Rule=="UNEXPECTED_NATIVE_OR_ACCESS_VLAN"&&x.Port=="1/1/1");
        Assert.Contains(findings,x=>x.Rule=="VLAN_MAPPING_UNKNOWN"&&x.Port=="1/1/26");
        Assert.DoesNotContain(findings,x=>x.Rule=="UNEXPECTED_TAGGED_VLAN");
    }

    [Fact]
    public void Missing_global_allowed_vlan_list_produces_unverified_not_compliant()
    {
        using var doc=JsonDocument.Parse(Interfaces);
        var p=CxSwitchReadback.Parse(doc.RootElement);
        var result=CxConfigurationReview.Compare(
            """{"switch":{"lldpMode":"tx-rx"}}""",p.Interfaces);
        Assert.Contains(result,x=>x.Rule=="SITE_VLAN_POLICY_UNKNOWN");
    }

    private sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext()=>new(options);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    private sealed class ReadAdapter(CxSwitchReadback.Result readback):IDeviceConnector
    {
        public bool Supports(DeviceKind kind)=>kind is DeviceKind.Cx6100 or DeviceKind.Cx6200;
        public Task<ProbeResult> ProbeAsync(Device device,CancellationToken ct)
            =>Task.FromResult(new ProbeResult(true,"10.13.0001",null,null,10,48,
                new(),new(),new(),null,new(){"PASS mock CX read"},null,null,null,null,readback));
    }
    [Fact]
    public async Task CX_REST_probe_persists_only_sanitized_deduplicated_sql_configuration_snapshot()
    {
        var factory=new Factory(new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var sw=new Device{Name="Real CX",Kind=DeviceKind.Cx6200,ManagementIp="10.101.20.11",
            Firmware="10.13.0001",SecretRef="LAB",IsSimulated=false};
        await using(var db=factory.CreateDbContext())
        {db.Devices.Add(sw);await db.SaveChangesAsync();}
        using var json=JsonDocument.Parse(Interfaces);
        var connector=new ReadAdapter(CxSwitchReadback.Parse(json.RootElement));
        var config=new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> {["Jcf:AllowedManagementCidrs:0"]="10.0.0.0/8"}).Build();
        var service=new DeviceService(factory,new[]{connector},config);
        await service.ProbeAsync(sw.Id,"operator");
        await service.ProbeAsync(sw.Id,"operator");
        await using var check=factory.CreateDbContext();
        var snapshot=Assert.Single(check.ConfigurationSnapshots);
        Assert.Equal("cx-interface-readonly",snapshot.CapturedBy);
        Assert.Contains("1/1/25",snapshot.SanitizedJson);
        Assert.DoesNotContain("NEVER_STORE_CX_PASSWORD",snapshot.SanitizedJson);
        Assert.DoesNotContain("NEVER_STORE_TOKEN",snapshot.SanitizedJson);
    }
}
