using System.Text.Json;
using Jcf.NetworkManager.Services;
using Xunit;
namespace Jcf.NetworkManager.Tests;
public sealed class DeviceTemplateResolverTests
{
    private const string Global = """{"wireless":{"width24GhzMhz":20,"width5GhzMhz":20,"dfsEnabled":false,"minTxPowerDbm":6,"maxTxPowerDbm":12,"rfPowerMode":"arm"}}""";
    private static DeviceTemplateResolver.Layer Layer(string json,string source,string actor="operator",string? approval=null)
        => new(json,source,1,actor,"Reviewed network policy",approval,DateTime.UtcNow);

    [Fact]
    public void Rf_power_inherits_global_then_site_then_approved_optimization()
    {
        var result=DeviceTemplateResolver.Resolve(
            Layer(Global,"G","admin"),
            Layer("""{"wireless":{"minTxPowerDbm":8,"maxTxPowerDbm":10}}""","S"),
            Layer(SiteConfigurationPolicy.EmptyOverride,"D"),
            Layer("""{"wireless":{"rfPowerMode":"fixed","fixedTxPowerDbm":9}}""","O","rf-operator","recommendation-id"),
            mandatoryPaths:new[]{"wireless.width24GhzMhz","wireless.dfsEnabled"});
        using var doc=JsonDocument.Parse(result.DesiredJson);
        var power=doc.RootElement.GetProperty("wireless");
        Assert.Equal(8,power.GetProperty("minTxPowerDbm").GetInt32());
        Assert.Equal(10,power.GetProperty("maxTxPowerDbm").GetInt32());
        Assert.Equal(9,power.GetProperty("fixedTxPowerDbm").GetInt32());
        Assert.Equal("S",result.Settings.Single(x=>x.Path=="wireless.minTxPowerDbm").Source);
        Assert.Equal("G",result.Settings.Single(x=>x.Path=="wireless.width24GhzMhz").Source);
        Assert.Equal("O",result.Settings.Single(x=>x.Path=="wireless.fixedTxPowerDbm").Source);
    }
    [Fact]
    public void Mandatory_global_setting_cannot_be_changed_at_the_site()
    {
        Assert.Throws<InvalidOperationException>(()=>DeviceTemplateResolver.Resolve(
            Layer(Global,"G","admin"),
            Layer("""{"wireless":{"minTxPowerDbm":8}}""","S"),
            mandatoryPaths:new[]{"wireless.minTxPowerDbm"}));
    }
    [Fact]
    public void Unapproved_optimization_override_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(()=>DeviceTemplateResolver.Resolve(
            Layer(Global,"G","admin"),
            optimization:Layer("""{"wireless":{"minTxPowerDbm":8}}""","O")));
    }
    [Fact]
    public void Conflicting_functional_profiles_must_not_last_write_win()
    {
        Assert.Throws<InvalidOperationException>(()=>DeviceTemplateResolver.Resolve(
            Layer(Global,"G","admin"),
            functionalProfiles:new[]{
                Layer("""{"wireless":{"clientMatch":"enabled"}}""","F"),
                Layer("""{"wireless":{"clientMatch":"disabled"}}""","F")
            }));
    }
    [Theory]
    [InlineData("""{"wireless":{"rfPowerMode":"fixed"}}""")]
    [InlineData("""{"wireless":{"rfPowerMode":"arm","fixedTxPowerDbm":9}}""")]
    [InlineData("""{"wireless":{"width5GhzMhz":80}}""")]
    public void Bad_RF_policies_are_blocked(string data)
        => Assert.Throws<ArgumentException>(()=>SiteConfigurationPolicy.Normalize(data));
}
