using System.Text.Json;
using Jcf.NetworkManager.Services;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class SiteConfigurationPolicyTests
{
    const string Global = """
    {"switch":{"dataVlan":100,"voiceVlan":200,"managementVlan":300,
        "nativeVlan":100,"allowedVlans":[100,200,300],
        "lldpMode":"tx-rx","poeDefault":"inherit-device","poePriority":"high"},
     "wireless":{"width24GhzMhz":20,"width5GhzMhz":40,
        "allowed5GhzChannels":[36,40,44,48],"dfsEnabled":false,
        "clientMatch":"enabled","minTxPowerDbm":9,"maxTxPowerDbm":18}}
    """;

    [Fact]
    public void Site_inherits_all_global_settings_and_replaces_only_explicit_override_keys()
    {
        const string overrides="""{"switch":{"dataVlan":110,"allowedVlans":[100,110,200,300]},
                                  "wireless":{"clientMatch":"disabled"}}""";
        var resolved=SiteConfigurationPolicy.Resolve(Global,overrides);
        using var doc=JsonDocument.Parse(resolved);
        var sw=doc.RootElement.GetProperty("switch");
        Assert.Equal(110,sw.GetProperty("dataVlan").GetInt32());
        Assert.Equal(200,sw.GetProperty("voiceVlan").GetInt32());
        Assert.Equal(300,sw.GetProperty("managementVlan").GetInt32());
        Assert.Equal("tx-rx",sw.GetProperty("lldpMode").GetString());
        Assert.Equal("inherit-device",sw.GetProperty("poeDefault").GetString());
        var wl=doc.RootElement.GetProperty("wireless");
        Assert.Equal(20,wl.GetProperty("width24GhzMhz").GetInt32());
        Assert.Equal(40,wl.GetProperty("width5GhzMhz").GetInt32());
        Assert.Equal("disabled",wl.GetProperty("clientMatch").GetString());
        Assert.Equal(4,wl.GetProperty("allowed5GhzChannels").GetArrayLength());
    }

    [Fact]
    public void Empty_site_override_inherits_full_global_baseline()
    {
        var full=SiteConfigurationPolicy.Resolve(Global,SiteConfigurationPolicy.EmptyOverride);
        Assert.Equal(SiteConfigurationPolicy.Normalize(Global),full);
    }

    [Theory]
    [InlineData("""{"wireless":{"width5GhzMhz":80}}""")]
    [InlineData("""{"wireless":{"width5GhzMhz":160}}""")]
    [InlineData("""{"wireless":{"width24GhzMhz":40}}""")]
    [InlineData("""{"wireless":{"dfsEnabled":true}}""")]
    [InlineData("""{"wireless":{"allowed5GhzChannels":[52,56]}}""")]
    [InlineData("""{"switch":{"managementVlan":1}}""")]
    [InlineData("""{"switch":{"poeDefault":"automatic"}}""")]
    [InlineData("""{"switch":{"password":"secret"}}""")]
    [InlineData("""{"switch":{"dataVlan":100,"dataVlan":200}}""")]
    [InlineData("""{"switch":{"allowedVlans":[100,100]}}""")]
    [InlineData("""{"wireless":{"wlanProfiles":[{"ssid":"JCF-STAFF","vlan":100,"securityMode":"wpa2-personal","band":"dual","purpose":"staff","pskSecretRef":"PSK","clientIsolation":false,"pmf":"capable","enabled":true,"passphrase":"RAW"}]}}""")]
    public void Rejects_unsafe_or_unknown_global_site_settings(string candidate)
    {
        Assert.Throws<ArgumentException>(()=>SiteConfigurationPolicy.Normalize(candidate));
    }

    [Fact]
    public void Cross_field_inheritance_validation_blocks_invalid_effective_vlans()
    {
        const string overrideText="""{"switch":{"allowedVlans":[100,200]}}""";
        Assert.Throws<ArgumentException>(()=>SiteConfigurationPolicy.Resolve(Global,overrideText));
    }

    [Fact]
    public void Strict_40MHz_non_DFS_pairing_is_inherited_and_enforced()
    {
        const string overrideText="""{"wireless":{"allowed5GhzChannels":[36,44]}}""";
        Assert.Throws<ArgumentException>(()=>SiteConfigurationPolicy.Resolve(Global,overrideText));
    }

    [Fact]
    public void WPA3_Radius_profile_uses_only_a_reference_in_template()
    {
        const string doc = """
        {"wireless":{"wlanProfiles":[{"ssid":"JCF-STAFF","vlan":100,
            "securityMode":"wpa3-enterprise","band":"dual","purpose":"staff",
            "radiusProfileRef":"JCF_RADIUS","clientIsolation":false,
            "pmf":"required","enabled":true}]}}
        """;
        var result=SiteConfigurationPolicy.Normalize(doc);
        Assert.Contains("JCF_RADIUS",result);
        Assert.DoesNotContain("password",result,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret",result,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WLAN_list_is_replaced_as_a_unit_for_site_exception()
    {
        const string global = """
        {"wireless":{"wlanProfiles":[{"ssid":"HQ-STAFF","vlan":100,
        "securityMode":"wpa2-personal","band":"dual","purpose":"staff",
        "pskSecretRef":"HQ_PSK","clientIsolation":false,"pmf":"capable","enabled":true}]}}
        """;
        const string site = """{"wireless":{"wlanProfiles":[]}}""";
        var effective=SiteConfigurationPolicy.Resolve(global,site);
        using var parsed=JsonDocument.Parse(effective);
        Assert.Empty(parsed.RootElement.GetProperty("wireless").GetProperty("wlanProfiles").EnumerateArray());
    }
}
