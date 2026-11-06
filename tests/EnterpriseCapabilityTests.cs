using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Workflow;
using Jcf.NetworkManager.Services;
using Jcf.NetworkManager.Connectors;
using Microsoft.EntityFrameworkCore;
using Jcf.NetworkManager.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using System.Text.Json;

namespace Jcf.NetworkManager.Tests;

public sealed class EnterpriseCapabilityTests
{
    [Theory]
    [InlineData(80)]
    [InlineData(160)]
    [InlineData(10)]
    public void Refuses_unapproved_channel_widths(int width)
    {
        var payload=JsonSerializer.Serialize(new {channelWidthMhz=width,channels5Ghz=new[]{36,40}});
        Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(ConfigAction.SetChannelPolicy,payload));
    }

    [Theory]
    [InlineData(52)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(144)]
    public void Refuses_excluded_DFS_channels(int channel)
    {
        var payload=JsonSerializer.Serialize(new {channelWidthMhz=20,channels5Ghz=new[]{36,channel}});
        Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(ConfigAction.SetChannelPolicy,payload));
    }

    [Fact]
    public void Does_not_allow_orphaned_40mhz_channels()
    {
        Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(ConfigAction.SetChannelPolicy,
            "{\"channelWidthMhz\":40,\"channels5Ghz\":[149]}"));
        Assert.Contains("153",NetworkChangePolicy.Normalize(ConfigAction.SetChannelPolicy,
            "{\"channelWidthMhz\":40,\"channels5Ghz\":[149,153]}"));
    }

    [Fact]
    public void Does_not_pair_channel_165_with_40MHz()
    {
        Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(ConfigAction.SetChannelPolicy,
            "{\"channelWidthMhz\":40,\"channels5Ghz\":[149,153,165]}"));
        Assert.Contains("165",NetworkChangePolicy.Normalize(ConfigAction.SetChannelPolicy,
            "{\"channelWidthMhz\":20,\"channels5Ghz\":[149,165]}"));
    }

    [Theory]
    [InlineData(ConfigAction.ChangeClientMatch)]
    [InlineData(ConfigAction.SetChannelPolicy)]
    [InlineData(ConfigAction.SetSwitchPortVlan)]
    [InlineData(ConfigAction.SetSwitchPoe)]
    public void New_actions_require_independent_double_review(ConfigAction action)
        => Assert.True(NetworkChangePolicy.RequiresSecondApprover(action));

    [Fact]
    public void Cluster_parser_keeps_unmanaged_models_in_inventory()
    {
        var data="3 Access Points\nAP-A 10.250.2.71 AP-505\nAP-B 10.250.2.72 AP-535\nAP-C 10.250.2.73 AP-565";
        var (members,total)=InstantConnector.ParseMembers(data);
        Assert.Equal(3,total);
        Assert.Equal(3,members.Count);
        Assert.Contains(members,m=>m.Model.Contains("565"));
    }

    [Fact]
    public async Task Move_device_preserves_effective_dated_history()
    {
        var name="placement-"+Guid.NewGuid().ToString("N");
        var services=new ServiceCollection();
        services.AddDbContextFactory<NetworkDbContext>(o=>o.UseInMemoryDatabase(name));
        using var provider=services.BuildServiceProvider();
        var factory=provider.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        Guid device,first,second;
        await using(var db=await factory.CreateDbContextAsync())
        {
            var a=new Location{Name="HQ"};var b=new Location{Name="Division"};
            var d=new Device{Name="AP",Kind=DeviceKind.Ap505,ManagementIp="10.14.0.15",SecretRef="LAB"};
            db.Locations.AddRange(a,b);db.Devices.Add(d);await db.SaveChangesAsync();
            device=d.Id;first=a.Id;second=b.Id;
        }
        var service=new EnterpriseOperationsService(factory);
        await service.PlaceAsync(device,first,"operator-one");
        await service.PlaceAsync(device,second,"operator-one");
        await using(var db=await factory.CreateDbContextAsync())
        {
            var moves=await db.DevicePlacements.OrderBy(x=>x.FromUtc).ToListAsync();
            Assert.Equal(2,moves.Count);
            Assert.Single(moves,x=>x.ThroughUtc is null && x.LocationId==second);
            Assert.Single(moves,x=>x.ThroughUtc is not null && x.LocationId==first);
        }
    }

    [Fact]
    public async Task Compliance_reports_drift_without_modifying_a_device()
    {
        var services=new ServiceCollection();
        services.AddDbContextFactory<NetworkDbContext>(o=>o.UseInMemoryDatabase("drift-"+Guid.NewGuid()));
        using var provider=services.BuildServiceProvider();
        var factory=provider.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        var id=Guid.NewGuid();
        await using(var db=await factory.CreateDbContextAsync())
        {
            db.Devices.Add(new Device{Id=id,Name="RF-LAB",ManagementIp="10.20.0.2",Kind=DeviceKind.Ap505,SecretRef="LAB"});
            db.TelemetrySnapshots.Add(new TelemetrySnapshot{DeviceId=id,ApiConnected=true,ChannelWidthMhz=80,
                RadioChannel=100,ArmJson="{\"80mhz-support\":\"enabled\"}"});
            await db.SaveChangesAsync();
        }
        var service=new EnterpriseOperationsService(factory);
        var findings=await service.EvaluateComplianceAsync("test-operator");
        Assert.Equal(3,findings);
        await using(var db=await factory.CreateDbContextAsync())
        {
            Assert.Equal(3,await db.ComplianceFindings.CountAsync());
            Assert.Equal(0,await db.ConfigExecutionEvents.CountAsync());
        }
    }

    [Fact]
    public async Task Snapshot_contains_no_credentials()
    {
        var services=new ServiceCollection();
        services.AddDbContextFactory<NetworkDbContext>(o=>o.UseInMemoryDatabase("snapshot-"+Guid.NewGuid()));
        using var provider=services.BuildServiceProvider();
        var factory=provider.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        var id=Guid.NewGuid();
        await using(var db=await factory.CreateDbContextAsync())
        {
            db.Devices.Add(new Device{Id=id,Name="AP",ManagementIp="10.20.0.2",Kind=DeviceKind.Ap505,SecretRef="ENV_SECRET",Firmware="8.10.0.21"});
            db.TelemetrySnapshots.Add(new TelemetrySnapshot{DeviceId=id,ApiConnected=true,ArmJson="{\"min-tx-power\":\"9\"}"});
            await db.SaveChangesAsync();
        }
        var result=await new EnterpriseOperationsService(factory).SnapshotAsync(id,"operator");
        Assert.Contains("min-tx-power",result.SanitizedJson);
        Assert.DoesNotContain("ENV_SECRET",result.SanitizedJson);
        Assert.Equal(64,result.DataSha256.Length);
    }
}
