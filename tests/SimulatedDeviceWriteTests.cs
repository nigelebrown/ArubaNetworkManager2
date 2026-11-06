using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Jcf.NetworkManager.Workflow;
using Xunit;

namespace Jcf.NetworkManager.Tests;

/// <summary>0.9 gateway actions (ClientMatch, channel policy, port VLAN, PoE, cluster scope) against the in-process
/// Instant / AOS-CX simulations. Proves adapter logic - baseline, write, readback, restore - not firmware acceptance.</summary>
public sealed partial class SimulatedDeviceTests
{
    static ArubaLabApprovedChangeGateway Gateway(HttpMessageHandler sim) => new(Http(sim), new SecretResolver());
    static ConfigProposal CxProposal(ConfigAction action, string json) => new()
    { DeviceId = Cx.Id, Action = action, PayloadJson = NetworkChangePolicy.Normalize(action, json), RequestedBy = "op" };

    [Fact]
    public void Arm_parser_reads_bare_flags_negations_and_channel_lists()
    {
        var arm = InstantConnector.ExtractArm("arm\n wide-bands 5ghz\n 80mhz-support\n client-match\n no scanning\n a-channels 36,40,44,48\n min-tx-power 9\nwlan ssid-profile X\n");
        Assert.Equal("enabled", arm["80mhz-support"]);     // 0.8 ignored bare flags, so the 80 MHz policy check never fired on real APs
        Assert.Equal("enabled", arm["client-match"]);
        Assert.Equal("disabled", arm["scanning"]);
        Assert.Equal("36,40,44,48", arm["a-channels"]);
        Assert.Equal("5ghz", arm["wide-bands"]);
        Assert.False(arm.ContainsKey("ssid-profile"));
    }

    [Fact]
    public async Task ClientMatch_enable_writes_flag_and_verifies_readback()
    {
        var sim = new InstantSim();
        var outcome = await Gateway(sim).ExecuteAsync(Ap, Proposal(ConfigAction.ChangeClientMatch, "{\"value\":\"enabled\"}"), CancellationToken.None);
        Assert.True(outcome.Success, outcome.Detail);
        Assert.Equal("", sim.Arm["client-match"]);
        Assert.Equal(1, sim.ArmWrites);
        Assert.Equal(1, sim.Logouts);
    }

    [Fact]
    public async Task Channel_policy_sets_channels_and_widths_then_restores_every_key_on_readback_failure()
    {
        var ok = new InstantSim();
        ok.Arm["80mhz-support"] = "";
        var applied = await Gateway(ok).ExecuteAsync(Ap, Proposal(ConfigAction.SetChannelPolicy, "{\"channelWidthMhz\":40,\"channels5Ghz\":[36,40,149,153]}"), CancellationToken.None);
        Assert.True(applied.Success, applied.Detail);
        Assert.Equal("36,40,149,153", ok.Arm["a-channels"]);
        Assert.Equal("5ghz", ok.Arm["wide-bands"]);
        Assert.False(ok.Arm.ContainsKey("80mhz-support"));

        var stuck = new InstantSim { IgnoreFlagWrites = true };  // device accepts value writes but silently ignores the flag removal
        stuck.Arm["80mhz-support"] = "";
        stuck.Arm["a-channels"] = "36,40,44,48";
        var failed = await Gateway(stuck).ExecuteAsync(Ap, Proposal(ConfigAction.SetChannelPolicy, "{\"channelWidthMhz\":20,\"channels5Ghz\":[36,44]}"), CancellationToken.None);
        Assert.False(failed.Success);
        Assert.True(failed.Restored, failed.Detail);
        Assert.Equal("36,40,44,48", stuck.Arm["a-channels"]);   // every changed key returned to its baseline
        Assert.False(stuck.Arm.ContainsKey("wide-bands"));
    }

    [Fact]
    public async Task Cluster_scope_must_match_the_approved_member_count()
    {
        var sim = new InstantSim { ApCount = 3 };
        var proposal = Proposal(ConfigAction.ChangeBandSteering, "{\"value\":\"balance-bands\"}");
        proposal.BaselineClusterMemberCount = 3;      // cluster-wide change approved for exactly 3 APs
        Assert.True((await Gateway(sim).ExecuteAsync(Ap, proposal, CancellationToken.None)).Success);
        var drifted = new InstantSim { ApCount = 4 };
        var blocked = await Gateway(drifted).ExecuteAsync(Ap, proposal, CancellationToken.None);
        Assert.False(blocked.Success);
        Assert.Equal(0, drifted.ArmWrites);
        Assert.Contains("approved scope of 3", blocked.Detail);
    }

    [Fact]
    public async Task Missing_credentials_fail_before_any_device_request()
    {
        var sim = new InstantSim();
        var noCreds = new Device { Kind = DeviceKind.Ap505, Name = "NOCRED", ManagementIp = "10.250.2.99", SecretRef = "NO_SUCH_PROFILE", Firmware = "8.10.0.21" };
        var outcome = await Gateway(sim).ExecuteAsync(noCreds, Proposal(ConfigAction.ChangeBandSteering, "{\"value\":\"balance-bands\"}"), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.True(outcome.Restored);
        Assert.False(outcome.ManualIntervention);
        Assert.Contains("No write attempted", outcome.Detail);
        Assert.Empty(sim.Requests);
    }

    [Fact]
    public async Task Cx_access_port_vlan_change_is_verified_and_refuses_to_flatten_a_trunk()
    {
        var sim = new CxSim();
        sim.Vlans[20] = "STAFF";
        var applied = await Gateway(sim).ExecuteAsync(Cx, CxProposal(ConfigAction.SetSwitchPortVlan, "{\"port\":\"1/1/1\",\"mode\":\"access\",\"vlan\":20}"), CancellationToken.None);
        Assert.True(applied.Success, applied.Detail);
        Assert.Equal(("access", 20), (sim.Ports["1/1/1"].Mode, sim.Ports["1/1/1"].Tag));

        var uplink = await Gateway(sim).ExecuteAsync(Cx, CxProposal(ConfigAction.SetSwitchPortVlan, "{\"port\":\"1/1/49\",\"mode\":\"access\",\"vlan\":20}"), CancellationToken.None);
        Assert.False(uplink.Success);
        Assert.Contains("trunk", uplink.Detail);
        Assert.Equal("native-untagged", sim.Ports["1/1/49"].Mode);

        var missingVlan = await Gateway(sim).ExecuteAsync(Cx, CxProposal(ConfigAction.SetSwitchPortVlan, "{\"port\":\"1/1/1\",\"mode\":\"access\",\"vlan\":999}"), CancellationToken.None);
        Assert.Contains("does not exist", missingVlan.Detail);
        Assert.Equal(1, sim.Patches);
        Assert.Equal(3, sim.Logouts);
    }

    [Fact]
    public async Task Cx_trunk_vlan_is_added_but_an_all_vlans_trunk_is_never_restricted()
    {
        var sim = new CxSim();
        sim.Vlans[30] = "CCTV";
        var added = await Gateway(sim).ExecuteAsync(Cx, CxProposal(ConfigAction.SetSwitchPortVlan, "{\"port\":\"1/1/49\",\"mode\":\"trunk\",\"vlan\":30}"), CancellationToken.None);
        Assert.True(added.Success, added.Detail);
        Assert.Equal(new[] { 1, 10, 30 }, sim.Ports["1/1/49"].Trunks);
        var all = await Gateway(sim).ExecuteAsync(Cx, CxProposal(ConfigAction.SetSwitchPortVlan, "{\"port\":\"1/1/50\",\"mode\":\"trunk\",\"vlan\":30}"), CancellationToken.None);
        Assert.True(all.Success);
        Assert.Contains("all VLANs", all.Detail);
        Assert.Empty(sim.Ports["1/1/50"].Trunks);
    }

    [Fact]
    public async Task Cx_poe_change_keeps_other_settings_and_reports_unverified_writes()
    {
        var sim = new CxSim();
        var off = await Gateway(sim).ExecuteAsync(Cx, CxProposal(ConfigAction.SetSwitchPoe, "{\"port\":\"1/1/1\",\"state\":\"disabled\"}"), CancellationToken.None);
        Assert.True(off.Success, off.Detail);
        Assert.True(sim.Poe["1/1/1"]["admin_disable"]!.GetValue<bool>());
        Assert.Equal("high", (string?)sim.Poe["1/1/1"]["priority"]);    // whole config map preserved

        var deaf = new CxSim { IgnorePatches = true };
        var ignored = await Gateway(deaf).ExecuteAsync(Cx, CxProposal(ConfigAction.SetSwitchPoe, "{\"port\":\"1/1/1\",\"state\":\"disabled\"}"), CancellationToken.None);
        Assert.False(ignored.Success);
        Assert.True(ignored.Restored, ignored.Detail);
        Assert.Contains("original state verified", ignored.Detail);
        var none = await Gateway(sim).ExecuteAsync(Cx, CxProposal(ConfigAction.SetSwitchPoe, "{\"port\":\"1/1/7\",\"state\":\"disabled\"}"), CancellationToken.None);
        Assert.Contains("not available", none.Detail);
    }
}
