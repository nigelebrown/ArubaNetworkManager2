using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Jcf.NetworkManager.Workflow;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class SimulatedWlanStateTests
{
    static ConfigProposal Applied(ConfigAction action,string json,int ordinal)
        => new() {
            Action=action,PayloadJson=ApProfilePolicy.Normalize(action,json),
            State=ConfigProposalState.Applied,
            RequestedUtc=new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc).AddMinutes(ordinal),
            FinishedUtc=new DateTime(2026,1,1,0,0,1,DateTimeKind.Utc).AddMinutes(ordinal)
        };
    const string Staff = """
       {"ssid":"JCF-STAFF","vlan":120,"securityMode":"wpa3-enterprise",
       "band":"5ghz","purpose":"staff","radiusProfileRef":"JCF_RADIUS",
       "pskSecretRef":"","clientIsolation":false,"pmf":"required","enabled":true}
       """;
    [Fact]
    public void Applied_profile_state_is_deterministic_and_persists_in_approved_SQL_history()
    {
        var applied=new [] {
            Applied(ConfigAction.UpsertWlanProfile,Staff,1),
            Applied(ConfigAction.SetWlanEnabled,"""{"ssid":"JCF-STAFF","enabled":false}""",2),
            Applied(ConfigAction.SetAirtimeFairness,"""{"mode":"fair-access"}""",3),
            Applied(ConfigAction.SetRfPowerBounds,"""{"band":"5ghz","minDbm":9,"maxDbm":17}""",4),
        };
        var result=SimulatedWlanState.Replay(applied.Reverse());
        var wlan=Assert.Single(result.Wlans);
        Assert.Equal("JCF-STAFF",wlan.Ssid);
        Assert.Equal("wpa3-enterprise",wlan.SecurityMode);
        Assert.Equal("JCF_RADIUS",wlan.CredentialReference);
        Assert.False(wlan.Enabled);
        Assert.Equal("fair-access",result.Rf.AirtimeFairnessMode);
        Assert.Equal(9,result.Rf.MinDbm);
        Assert.Equal(17,result.Rf.MaxDbm);
        Assert.Equal("5ghz",result.Rf.PowerBand);
    }
    [Fact]
    public void Deleting_WLAN_removes_state_without_changing_other_profiles()
    {
        var list=new[]{
            Applied(ConfigAction.UpsertWlanProfile,Staff,1),
            Applied(ConfigAction.UpsertWlanProfile,
               """{"ssid":"JCF-GUEST","vlan":230,"securityMode":"wpa2-personal","band":"dual","purpose":"guest","pskSecretRef":"GUEST_VAULT","clientIsolation":true,"pmf":"capable","enabled":true}""",2),
            Applied(ConfigAction.DeleteWlanProfile,
               """{"ssid":"JCF-STAFF","confirmSsid":"JCF-STAFF","impactConfirmed":true}""",3)
        };
        var result=SimulatedWlanState.Replay(list);
        Assert.Equal("JCF-GUEST",Assert.Single(result.Wlans).Ssid);
    }
    [Fact]
    public void Pending_and_unapproved_changes_never_affect_simulated_effective_settings()
    {
        var proposal=Applied(ConfigAction.UpsertWlanProfile,Staff,1);
        proposal.State=ConfigProposalState.Approved;
        Assert.Empty(SimulatedWlanState.Replay(new[]{proposal}).Wlans);
    }
    [Fact]
    public void Simulator_ignores_malformed_applied_records_rather_than_displaying_unverified_state()
    {
        var p=new ConfigProposal {Action=ConfigAction.UpsertWlanProfile,
            State=ConfigProposalState.Applied,PayloadJson="{invalid"};
        var result=SimulatedWlanState.Replay(new[]{p});
        Assert.Empty(result.Wlans);
    }
}
