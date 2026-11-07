using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class ApProfilePolicyTests
{
    const string ValidWlan = """
        {"ssid":"JCF-STAFF","vlan":120,"securityMode":"wpa2-enterprise",
         "band":"dual","purpose":"staff","pskSecretRef":"",
         "radiusProfileRef":"JCF_RADIUS","clientIsolation":false,
         "pmf":"required","enabled":true}
        """;

    [Fact]
    public void Enterprise_WLAN_is_canonical_and_stores_only_a_radius_reference()
    {
        var normalized=NetworkChangePolicy.Normalize(ConfigAction.UpsertWlanProfile,ValidWlan);
        using var j=JsonDocument.Parse(normalized);
        Assert.Equal("wpa2-enterprise",j.RootElement.GetProperty("securityMode").GetString());
        Assert.Equal("JCF_RADIUS",j.RootElement.GetProperty("radiusProfileRef").GetString());
        Assert.False(j.RootElement.TryGetProperty("pskSecretRef",out _));
        Assert.DoesNotContain("password",normalized,StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"ssid":"GUEST","vlan":200,"securityMode":"wpa2-personal","band":"dual","purpose":"guest","pskSecretRef":"SAMPLE_PSK","clientIsolation":false,"pmf":"capable","enabled":true}""")]
    [InlineData("""{"ssid":"STAFF","vlan":120,"securityMode":"wpa3-personal","band":"5ghz","purpose":"staff","pskSecretRef":"STAFF_PSK","clientIsolation":false,"pmf":"capable","enabled":true}""")]
    [InlineData("""{"ssid":"STAFF","vlan":120,"securityMode":"wpa2-enterprise","band":"dual","purpose":"staff","radiusProfileRef":"","clientIsolation":false,"pmf":"required","enabled":true}""")]
    [InlineData("""{"ssid":"STAFF","vlan":120,"securityMode":"wpa2-personal","band":"dual","purpose":"staff","pskSecretRef":"STAFF","radiusProfileRef":"EXTRA_RADIUS","clientIsolation":false,"pmf":"capable","enabled":true}""")]
    [InlineData("""{"ssid":"STAFF","vlan":120,"securityMode":"open","band":"dual","purpose":"staff","pskSecretRef":"STAFF","clientIsolation":false,"pmf":"capable","enabled":true}""")]
    [InlineData("""{"ssid":"STAFF","vlan":120,"securityMode":"wpa2-personal","band":"dual","purpose":"staff","pskSecretRef":"STAFF","clientIsolation":false,"pmf":"capable","enabled":true,"password":"plaintext"}""")]
    [InlineData("""{"ssid":"STAFF","vlan":120,"securityMode":"wpa2-personal","band":"dual","purpose":"staff","pskSecretRef":"STAFF","clientIsolation":false,"pmf":"capable","enabled":true,"channelWidthMhz":80}""")]
    public void Unsafe_WLAN_profile_payloads_are_rejected(string json)
        => Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(ConfigAction.UpsertWlanProfile,json));

    [Fact]
    public void Personal_and_guest_profile_uses_secret_reference_and_isolation()
    {
        var normalized=NetworkChangePolicy.Normalize(ConfigAction.UpsertWlanProfile,
            """{"ssid":"GUEST","vlan":230,"securityMode":"wpa2-wpa3-transition","band":"dual","purpose":"guest","pskSecretRef":"GUEST_VAULT","clientIsolation":true,"pmf":"capable","enabled":true}""");
        Assert.Contains("GUEST_VAULT",normalized);
        Assert.Contains("clientIsolation",normalized);
    }

    [Theory]
    [InlineData("""{"ssid":"JCF-STAFF","confirmSsid":"JCF-GUEST","impactConfirmed":true}""")]
    [InlineData("""{"ssid":"JCF-STAFF","confirmSsid":"JCF-STAFF","impactConfirmed":false}""")]
    [InlineData("""{"ssid":"JCF-STAFF","confirmSsid":"JCF-STAFF","impactConfirmed":true,"cli":"erase"}""")]
    public void Deleting_a_WLAN_requires_exact_confirmation_and_no_extra_fields(string json)
       => Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(ConfigAction.DeleteWlanProfile,json));

    [Fact]
    public void Valid_disable_and_delete_proposals_are_non_secret()
    {
        Assert.Contains("false",NetworkChangePolicy.Normalize(ConfigAction.SetWlanEnabled,
            """{"ssid":"JCF-GUEST","enabled":false}"""));
        Assert.Contains("JCF-GUEST",NetworkChangePolicy.Normalize(ConfigAction.DeleteWlanProfile,
            """{"ssid":"JCF-GUEST","confirmSsid":"JCF-GUEST","impactConfirmed":true}"""));
    }

    [Theory]
    [InlineData("""{"mode":"auto"}""")]
    [InlineData("""{"mode":"fair-access","value":1}""")]
    public void Unsupported_airtime_modes_are_rejected(string json)
        => Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(ConfigAction.SetAirtimeFairness,json));

    [Fact]
    public void Documented_airtime_modes_are_accepted_as_proposals()
    {
        foreach(var mode in new[]{"default-access","fair-access","preferred-access"})
            Assert.Contains(mode,NetworkChangePolicy.Normalize(ConfigAction.SetAirtimeFairness,
                JsonSerializer.Serialize(new{mode})));
    }

    [Theory]
    [InlineData("""{"band":"5ghz","minDbm":20,"maxDbm":18}""")]
    [InlineData("""{"band":"6ghz","minDbm":10,"maxDbm":18}""")]
    [InlineData("""{"band":"2.4ghz","minDbm":10,"maxDbm":80}""")]
    [InlineData("""{"band":"5ghz","minDbm":9,"maxDbm":18,"channelWidthMhz":80}""")]
    public void RF_power_bounds_reject_bad_values_and_undocumented_fields(string json)
        => Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(ConfigAction.SetRfPowerBounds,json));

    [Fact]
    public void Bounded_power_values_are_accepted_but_cannot_change_channel_width()
    {
        var value=NetworkChangePolicy.Normalize(ConfigAction.SetRfPowerBounds,
            """{"band":"5ghz","minDbm":9,"maxDbm":18}""");
        Assert.Contains("18",value);
        Assert.DoesNotContain("channelWidthMhz",value);
    }

    [Fact]
    public void New_actions_are_AP_only_and_require_dual_approval()
    {
        var ap=new Device{Kind=DeviceKind.Ap535};
        var cx=new Device{Kind=DeviceKind.Cx6200};
        foreach(var action in new[]{ConfigAction.UpsertWlanProfile,ConfigAction.SetWlanEnabled,
            ConfigAction.DeleteWlanProfile,ConfigAction.SetAirtimeFairness,ConfigAction.SetRfPowerBounds})
        {
            Assert.True(ApProfilePolicy.ProposalOnly(action));
            Assert.True(NetworkChangePolicy.RequiresSecondApprover(action));
            NetworkChangePolicy.CheckModel(ap,action);
            Assert.Throws<InvalidOperationException>(()=>NetworkChangePolicy.CheckModel(cx,action));
        }
    }

    sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext()=>new(options);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    sealed class RecordingGateway : IApprovedChangeGateway
    {
        public int Calls;
        public Task<ChangeExecutionOutcome> ExecuteAsync(Device d,ConfigProposal p,CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ChangeExecutionOutcome(true,true,false,"mock"));
        }
    }
    [Fact]
    public async Task Live_hardware_never_receives_new_uncertified_WLAN_write_even_after_two_approvals()
    {
        var options=new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var factory=new Factory(options);
        var ap=new Device{Kind=DeviceKind.Ap505,IsSimulated=false,Enabled=true,
            Firmware="8.10.0.21",Name="Real AP",ManagementIp="10.255.0.10"};
        await using(var db=factory.CreateDbContext()) {db.Devices.Add(ap);await db.SaveChangesAsync();}
        var config=new ConfigurationBuilder().Build();
        var gateway=new RecordingGateway();
        var workflow=new ChangeWorkflowService(factory,gateway,config);
        var request=await workflow.SubmitAsync(ap.Id,ConfigAction.UpsertWlanProfile,ValidWlan,"operator-1","lab review");
        var first=await workflow.DecideAsync(request.Id,"approve","reviewer-2",true);
        Assert.Equal(ConfigProposalState.PendingSecondApproval,first.State);
        var second=await workflow.DecideAsync(request.Id,"approve","reviewer-3",true);
        Assert.Equal(ConfigProposalState.Approved,second.State);
        await Assert.ThrowsAsync<InvalidOperationException>(
            ()=>workflow.ExecuteAsync(request.Id,"operator-1",true));
        Assert.Equal(0,gateway.Calls);
    }
}
