using System.Text.Json;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class InstantWlanReadbackTests
{
    const string Config = """
        hostname LAB-VC
        wlan ssid-profile JCF-STAFF
         type employee
         essid JCF-STAFF
         enable
         vlan 120
         wpa-passphrase NEVER_STORE_THIS_WIFI_SECRET
         opmode wpa2-psk-aes
         rf-band all
        wlan ssid-profile JCF-GUEST
        type guest
        essid JCF-GUEST
        enable
        vlan 200
        opmode opensystem
        rf-band 5.0
        deny-intra-vlan-traffic
        auth-server INTERNAL_SECRET_SERVER_NOT_FOR_EXPORT
        wlan access-rule GUEST_RULE
        arm
          80mhz-support
        """;

    [Fact]
    public void Aruba_AOS8_810_indented_and_flat_CLI_profiles_are_parsed_without_secrets()
    {
        var result=InstantWlanReadback.Parse(Config);
        Assert.True(result.IsUsable);
        Assert.Equal("Parsed",result.Status);
        Assert.Equal(2,result.Profiles.Count);
        var staff=result.Profiles[0];
        Assert.Equal("JCF-STAFF",staff.Essid);
        Assert.Equal(120,staff.Vlan);
        Assert.Equal("wpa2-psk-aes",staff.OpMode);
        Assert.Equal("all",staff.Band);
        Assert.True(staff.Enabled);
        var guest=result.Profiles[1];
        Assert.Equal("guest",guest.Purpose);
        Assert.Equal("opensystem",guest.OpMode);
        Assert.True(guest.IntraVlanTrafficDenied);
        var projection=JsonSerializer.Serialize(result);
        Assert.DoesNotContain("NEVER_STORE_THIS_WIFI_SECRET",projection);
        Assert.DoesNotContain("INTERNAL_SECRET_SERVER_NOT_FOR_EXPORT",projection);
        Assert.DoesNotContain("wpa-passphrase",projection);
        Assert.DoesNotContain("auth-server",projection);
    }

    [Fact]
    public void Duplicate_or_untrusted_profiles_do_not_generate_a_usable_snapshot()
    {
        Assert.Equal("Ambiguous",InstantWlanReadback.Parse(
            "wlan ssid-profile STAFF\nessid STAFF\nwlan ssid-profile STAFF\nessid STAFF2").Status);
        Assert.Equal("Ambiguous",InstantWlanReadback.Parse(
            "wlan ssid-profile GOOD\nessid GOOD\nwlan ssid-profile BAD/NAME\nessid BAD").Status);
        Assert.Equal("Oversize",InstantWlanReadback.Parse(new string('x',512001)).Status);
        Assert.Equal("Unavailable",InstantWlanReadback.Parse("").Status);
    }

    [Fact]
    public void Unknown_fields_are_not_invented_and_dynamic_vlan_is_not_assumed_static()
    {
        var result=InstantWlanReadback.Parse("""
          wlan ssid-profile LAB
          essid LAB
          set-vlan role equals visitor 220
          vlan $per-ap-vlan
          rf-band 5.0
          """);
        var wlan=Assert.Single(result.Profiles);
        Assert.True(wlan.DynamicVlan);
        Assert.Null(wlan.Vlan);
        Assert.Null(wlan.OpMode);
        Assert.Null(wlan.Enabled);
        Assert.Equal("5.0",wlan.Band);
        Assert.Contains(InstantWlanCompliance.Evaluate(result.Profiles),
            x=>x.Rule=="VLAN_UNKNOWN" && x.Level=="Unverified");
    }

    [Fact]
    public void Guest_open_auth_and_explicit_no_isolation_generate_advisories_only()
    {
        var wlan=InstantWlanReadback.Parse("""
          wlan ssid-profile GUEST
           type guest
           essid GUEST
           vlan 1
           opmode opensystem
           no deny-intra-vlan-traffic
          """);
        var findings=InstantWlanCompliance.Evaluate(wlan.Profiles);
        Assert.Contains(findings,x=>x.Rule=="OPEN_AUTH");
        Assert.Contains(findings,x=>x.Rule=="DEFAULT_VLAN");
        Assert.Contains(findings,x=>x.Rule=="GUEST_ISOLATION_DISABLED");
    }

    [Fact]
    public void Missing_guest_isolation_command_must_not_be_reported_as_explicitly_disabled()
    {
        var p=InstantWlanReadback.Parse("""
          wlan ssid-profile GUEST
          type guest
          essid GUEST
          vlan 120
          opmode wpa2-psk-aes
          """);
        Assert.Null(Assert.Single(p.Profiles).IntraVlanTrafficDenied);
        Assert.DoesNotContain(InstantWlanCompliance.Evaluate(p.Profiles),
            x=>x.Rule=="GUEST_ISOLATION_DISABLED");
    }

    private sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext()=>new(options);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }

    private sealed class ProbeAdapter(InstantWlanReadback.Result wlans) : IDeviceConnector
    {
        public int Calls;
        public bool Supports(DeviceKind kind)=>kind is DeviceKind.Ap505 or DeviceKind.Ap535;
        public Task<ProbeResult> ProbeAsync(Device device,CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ProbeResult(
                true,"8.10.0.21",2,5,null,null,new(),new(),new(),null,
                new(){"INFO sanitized WLAN projection"},null,null,null,wlans));
        }
    }

    [Fact]
    public async Task Real_AP_probe_persists_only_non_secret_SQL_snapshot_and_deduplicates_it()
    {
        var options=new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var f=new Factory(options);
        var ap=new Device {
            Name="REAL AP",Kind=DeviceKind.Ap505,ManagementIp="10.250.2.71",
            Firmware="8.10.0.21",SecretRef="LAB",IsSimulated=false
        };
        await using(var db=f.CreateDbContext())
        {
            db.Devices.Add(ap);await db.SaveChangesAsync();
        }
        var adapter=new ProbeAdapter(InstantWlanReadback.Parse(Config));
        var config=new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> {
                ["Jcf:AllowedManagementCidrs:0"]="10.0.0.0/8"
            }).Build();
        var service=new DeviceService(f,new[]{adapter},config);
        await service.ProbeAsync(ap.Id,"lab-operator");
        await service.ProbeAsync(ap.Id,"lab-operator");
        await using var check=f.CreateDbContext();
        var snapshot=Assert.Single(check.ConfigurationSnapshots);
        Assert.Equal("instant-wlan-readonly",snapshot.CapturedBy);
        Assert.DoesNotContain("NEVER_STORE_THIS_WIFI_SECRET",snapshot.SanitizedJson);
        Assert.DoesNotContain("INTERNAL_SECRET_SERVER_NOT_FOR_EXPORT",snapshot.SanitizedJson);
        Assert.Contains("JCF-STAFF",snapshot.SanitizedJson);
        Assert.Equal(2,adapter.Calls);
    }

    [Fact]
    public async Task Ambiguous_untrusted_parse_never_persists_a_snapshot()
    {
        var options=new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var f=new Factory(options);
        var ap=new Device {
            Name="REAL AP",Kind=DeviceKind.Ap535,ManagementIp="10.250.2.72",
            SecretRef="LAB",IsSimulated=false
        };
        await using(var db=f.CreateDbContext()) {db.Devices.Add(ap);await db.SaveChangesAsync();}
        var adapter=new ProbeAdapter(InstantWlanReadback.Parse(
            "wlan ssid-profile DUP\nwlan ssid-profile DUP"));
        var config=new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> {["Jcf:AllowedManagementCidrs:0"]="10.0.0.0/8"}).Build();
        await new DeviceService(f,new[]{adapter},config).ProbeAsync(ap.Id,"tester");
        await using var check=f.CreateDbContext();
        Assert.Empty(check.ConfigurationSnapshots);
    }
}
