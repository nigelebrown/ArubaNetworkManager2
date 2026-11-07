using Jcf.NetworkManager.Services;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class OnboardingDiscoveryTests
{
    private static readonly string[] Approved={"10.101.0.0/16","172.17.71.0/24"};
    [Fact]
    public void OneIpRange_IsOneTarget()
    {
        var ip=OnboardingDiscoveryService.ExpandRange("10.101.1.9","10.101.1.9",Approved);
        Assert.Equal(new[]{"10.101.1.9"},ip);
    }
    [Fact]
    public void SmallContiguousRangeEnumeratesInOrder()
    {
        var ips=OnboardingDiscoveryService.ExpandRange("172.17.71.20","172.17.71.22",Approved);
        Assert.Equal(new[]{"172.17.71.20","172.17.71.21","172.17.71.22"},ips);
    }
    [Fact]
    public void RejectsCrossingUnapprovedSubnetBeforeAnyProbe()
    {
        Assert.Throws<InvalidOperationException>(()=>
            OnboardingDiscoveryService.ExpandRange("172.17.71.254","172.17.72.1",Approved));
    }
    [Fact]
    public void RejectsMoreThan64Targets()
    {
        Assert.Throws<ArgumentException>(()=>
            OnboardingDiscoveryService.ExpandRange("10.101.1.1","10.101.1.65",Approved));
    }
    [Fact]
    public void RejectsReverseRangeAndNonIpv4()
    {
        Assert.Throws<ArgumentException>(()=>
            OnboardingDiscoveryService.ExpandRange("10.101.1.8","10.101.1.7",Approved));
        Assert.Throws<ArgumentException>(()=>
            OnboardingDiscoveryService.ExpandRange("::1","10.101.1.1",Approved));
    }
    [Fact]
    public void RejectsPublicAndLoopbackAddresses()
    {
        Assert.Throws<InvalidOperationException>(()=>
            OnboardingDiscoveryService.ExpandRange("8.8.8.8","8.8.8.8",Approved));
        Assert.Throws<InvalidOperationException>(()=>
            OnboardingDiscoveryService.ExpandRange("127.0.0.1","127.0.0.1",new[]{"127.0.0.0/8"}));
    }
    [Fact]
    public void HandlesMaxIpv4WithoutOverflow()
    {
        Assert.Throws<InvalidOperationException>(()=>
            OnboardingDiscoveryService.ExpandRange("255.255.255.255","255.255.255.255",new[]{"0.0.0.0/0"}));
    }
}
