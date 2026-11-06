using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class FoundationsTests
{
    private static readonly string[] Networks = {"10.0.0.0/8","172.16.0.0/12","192.168.0.0/16"};
    [Theory]
    [InlineData("10.250.2.71",true)]
    [InlineData("172.16.1.2",true)]
    [InlineData("192.168.10.15",true)]
    [InlineData("8.8.8.8",false)]
    [InlineData("127.0.0.1",false)]
    [InlineData("169.254.1.1",false)]
    [InlineData("localhost",false)]
    public void Management_network_policy_rejects_unauthorized_addresses(string ip,bool expected)
        => Assert.Equal(expected,AddressPolicy.IsAllowed(ip,Networks));

    [Fact]
    public void Arm_extraction_only_collects_approved_non_secret_fields()
    {
        const string config = "ssid test\narm\n wide-bands 5ghz\n 80mhz-support enabled\n min-tx-power 9\n secret-password not-for-export\nwlan ssid-profile X\n wpa-passphrase SECRET";
        var arm=InstantConnector.ExtractArm(config);
        Assert.Equal("enabled",arm["80mhz-support"]);
        Assert.Equal("9",arm["min-tx-power"]);
        Assert.DoesNotContain("secret-password",arm.Keys);
        Assert.DoesNotContain("wpa-passphrase",arm.Keys);
    }

    [Fact]
    public void Cluster_parser_does_not_invent_members_from_count_only()
    {
        var (members,count)=InstantConnector.ParseMembers("2 Access Points\nNo member rows available");
        Assert.Equal(2,count);
        Assert.Empty(members);
    }

    [Fact]
    public void AP_model_set_matches_SRS()
    {
        Assert.Equal(new[]{"Ap505","Ap535","Cx6100","Cx6200"},Enum.GetNames<DeviceKind>());
        Assert.DoesNotContain("Ap353",Enum.GetNames<DeviceKind>());
    }
}
