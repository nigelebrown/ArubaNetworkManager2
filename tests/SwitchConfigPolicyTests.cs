using System.Text.Json;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Jcf.NetworkManager.Workflow;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class SwitchConfigPolicyTests
{
    [Theory]
    [InlineData(ConfigAction.PlanCxTrunk,"""{"port":"1/1/23","nativeVlan":100,"allowedVlans":[100,200,300],"impactAcknowledged":true}""")]
    [InlineData(ConfigAction.PlanCxPortAdmin,"""{"port":"1/1/1","state":"up","description":"Front office","impactAcknowledged":false}""")]
    [InlineData(ConfigAction.PlanCxLag,"""{"lagId":1,"lacpMode":"active","members":["1/1/23","1/1/24"],"nativeVlan":100,"allowedVlans":[100,200],"impactAcknowledged":true}""")]
    [InlineData(ConfigAction.PlanCxLldp,"""{"port":"1/1/5","adminMode":"tx-rx"}""")]
    [InlineData(ConfigAction.PlanCxPoePolicy,"""{"port":"1/1/7","priority":"high","powerLimitWatts":30}""")]
    public void Valid_switch_configuration_plans_are_canonical_and_require_two_approvers(ConfigAction action,string json)
    {
        var result=NetworkChangePolicy.Normalize(action,json);
        Assert.NotEmpty(result);
        Assert.True(SwitchConfigPolicy.IsPlanned(action));
        Assert.True(NetworkChangePolicy.RequiresSecondApprover(action));
        NetworkChangePolicy.CheckModel(new Device{Kind=DeviceKind.Cx6100},action);
        NetworkChangePolicy.CheckModel(new Device{Kind=DeviceKind.Cx6200},action);
        Assert.Throws<InvalidOperationException>(()=>
            NetworkChangePolicy.CheckModel(new Device{Kind=DeviceKind.Ap535},action));
    }

    [Theory]
    [InlineData(ConfigAction.PlanCxTrunk,"""{"port":"1/1/5","nativeVlan":100,"allowedVlans":[200],"impactAcknowledged":true}""")]
    [InlineData(ConfigAction.PlanCxTrunk,"""{"port":"1/1/5","nativeVlan":100,"allowedVlans":[100],"impactAcknowledged":false}""")]
    [InlineData(ConfigAction.PlanCxTrunk,"""{"port":"1/1/5","nativeVlan":100,"allowedVlans":[100],"impactAcknowledged":true,"allVlans":true}""")]
    [InlineData(ConfigAction.PlanCxTrunk,"""{"port":"1/1/5","nativeVlan":100,"allowedVlans":[100,100],"impactAcknowledged":true}""")]
    [InlineData(ConfigAction.PlanCxPortAdmin,"""{"port":"1/1/1","state":"down","description":"Uplink","impactAcknowledged":false}""")]
    [InlineData(ConfigAction.PlanCxPortAdmin,"""{"port":"1/1/9999","state":"up","description":"Invalid","impactAcknowledged":false}""")]
    [InlineData(ConfigAction.PlanCxPortAdmin,"""{"port":"1/1/1","state":"up","description":"Normal","impactAcknowledged":false,"command":"erase startup-config"}""")]
    [InlineData(ConfigAction.PlanCxLag,"""{"lagId":1,"lacpMode":"active","members":["1/1/1","1/2/1"],"nativeVlan":100,"allowedVlans":[100],"impactAcknowledged":true}""")]
    [InlineData(ConfigAction.PlanCxLag,"""{"lagId":1,"lacpMode":"active","members":["1/1/1","1/1/1"],"nativeVlan":100,"allowedVlans":[100],"impactAcknowledged":true}""")]
    [InlineData(ConfigAction.PlanCxLldp,"""{"port":"1/1/1","adminMode":"not-supported"}""")]
    [InlineData(ConfigAction.PlanCxPoePolicy,"""{"port":"1/1/1","priority":"high","powerLimitWatts":91}""")]
    [InlineData(ConfigAction.PlanCxPoePolicy,"""{"port":"1/1/1","priority":"automatic","powerLimitWatts":30}""")]
    public void Unsafe_switch_port_changes_are_rejected(ConfigAction action,string json)
        =>Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(action,json));

    [Fact]
    public void Trunk_vlan_sets_are_sorted_and_cannot_use_implicit_all_vlans()
    {
        var canonical=NetworkChangePolicy.Normalize(ConfigAction.PlanCxTrunk,
            """{"port":"1/1/24","nativeVlan":100,"allowedVlans":[300,100,200],"impactAcknowledged":true}""");
        using var doc=JsonDocument.Parse(canonical);
        Assert.Equal(new[]{100,200,300},
            doc.RootElement.GetProperty("allowedVlans").EnumerateArray().Select(x=>x.GetInt32()));
    }

    [Fact]
    public void CX6100_and_CX6200_apply_separate_LAG_ID_limits()
    {
        const string json="""{"lagId":10,"lacpMode":"active","members":["1/1/1","1/1/2"],"nativeVlan":100,"allowedVlans":[100],"impactAcknowledged":true}""";
        var canonical=NetworkChangePolicy.Normalize(ConfigAction.PlanCxLag,json);
        Assert.Throws<ArgumentException>(()=>
            SwitchConfigPolicy.CheckTarget(new Device{Kind=DeviceKind.Cx6100},ConfigAction.PlanCxLag,canonical));
        SwitchConfigPolicy.CheckTarget(new Device{Kind=DeviceKind.Cx6200},ConfigAction.PlanCxLag,canonical);
    }

    [Fact]
    public void Switch_24_and_48_ports_allow_declared_access_ports_and_only_observed_uplinks()
    {
        var cx24=new Device{Kind=DeviceKind.Cx6100,Name="24-port CX"};
        var cx48=new Device{Kind=DeviceKind.Cx6200,Name="48-port CX"};
        const string safe="""{"port":"1/1/24","nativeVlan":100,"allowedVlans":[100],"impactAcknowledged":true}""";
        const string uplink="""{"port":"1/1/49","nativeVlan":100,"allowedVlans":[100],"impactAcknowledged":true}""";
        const string edge48="""{"port":"1/1/48","nativeVlan":100,"allowedVlans":[100],"impactAcknowledged":true}""";
        var trunk=NetworkChangePolicy.Normalize(ConfigAction.PlanCxTrunk,safe);
        SwitchPortInventoryPolicy.Check(cx24,ConfigAction.PlanCxTrunk,trunk,24,Array.Empty<string>());
        var uplinkPlan=NetworkChangePolicy.Normalize(ConfigAction.PlanCxTrunk,uplink);
        Assert.Throws<InvalidOperationException>(()=>
            SwitchPortInventoryPolicy.Check(cx24,ConfigAction.PlanCxTrunk,uplinkPlan,24,Array.Empty<string>()));
        SwitchPortInventoryPolicy.Check(cx24,ConfigAction.PlanCxTrunk,uplinkPlan,24,new[]{"1/1/49"});
        SwitchPortInventoryPolicy.Check(cx48,ConfigAction.PlanCxTrunk,
            NetworkChangePolicy.Normalize(ConfigAction.PlanCxTrunk,edge48),48,Array.Empty<string>());
        Assert.Throws<InvalidOperationException>(()=>
            SwitchPortInventoryPolicy.Check(cx48,ConfigAction.PlanCxTrunk,trunk,null,Array.Empty<string>()));
    }

    [Fact]
    public void Declared_chassis_count_is_not_misrepresented_as_verified_switch_capability()
    {
        var c=new DeviceCapability{DeviceId=Guid.NewGuid(),
            Operation=SiteConfigurationService.PortCountOperation,
            EvidenceReference="operator-declared:48",
            State=ApiCapabilityState.Unknown};
        Assert.Equal(48,SwitchPortInventoryPolicy.DeclaredCount(c));
        Assert.Null(SwitchPortInventoryPolicy.DeclaredCount(new DeviceCapability{
            Operation=SiteConfigurationService.PortCountOperation,
            EvidenceReference="operator-declared:28"
        }));
    }
}
