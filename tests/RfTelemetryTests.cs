using System.Text.Json;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class RfTelemetryTests
{
    sealed class Factory(DbContextOptions<NetworkDbContext> opts): IDbContextFactory<NetworkDbContext> {public NetworkDbContext CreateDbContext()=>new(opts); public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());}
    [Fact] public void Strict_labeled_values_are_parsed_with_provenance()
    {
        var result=InstantConnector.ParseRfObservation("Channel: 149\nChannel Width (MHz): 20\nChannel Utilization: 85%\nNoise Floor (dBm): -91\nClient Retry Rate: 13%");
        Assert.NotNull(result);
        Assert.Equal(149,result!.Channel);
        Assert.Equal(20,result.WidthMhz);
        Assert.Equal(85m,result.UtilizationPct);
        Assert.Equal(-91m,result.NoiseFloorDbm);
        Assert.Equal(13m,result.ClientRetryPct);
        Assert.Contains("labeled",result.Source);
    }
    [Theory]
    [InlineData("Channel: 9999")]
    [InlineData("Channel Utilization: 900%")]
    [InlineData("An unknown parameter was present")]
    [InlineData("")]
    public void Missing_or_invalid_samples_remain_unknown(string text)
    {
        var result=InstantConnector.ParseRfObservation(text);
        Assert.Null(result);
    }
    [Fact] public async Task Observed_DFS_and_unapproved_width_make_recommendations_only()
    {
        var factory=new Factory(new DbContextOptionsBuilder<NetworkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using(var db=await factory.CreateDbContextAsync()) {
            var ap=new Device{Name="Demo",Kind=DeviceKind.Ap505,ManagementIp="10.249.1.2",SecretRef="LAB"};
            db.Devices.Add(ap);db.TelemetrySnapshots.Add(new TelemetrySnapshot{DeviceId=ap.Id,AtUtc=DateTime.UtcNow,ApiConnected=true,
                ClientCount=0,RadioChannel=100,ChannelWidthMhz=80,ObservationSource="Instant show stats ap; labeled values (heuristic parsing)",ArmJson="{}"});
            await db.SaveChangesAsync();
        }
        var run=await new AnalysisService(factory).RunAsync("Manual","test");
        Assert.Equal(2,run.Findings);
        await using var verify=await factory.CreateDbContextAsync();
        Assert.Contains(await verify.Recommendations.ToListAsync(),r=>r.Title.Contains("width"));
        Assert.Contains(await verify.Recommendations.ToListAsync(),r=>r.Title.Contains("channel"));
        Assert.All(await verify.Recommendations.ToListAsync(),r=>Assert.False(r.Executed));
    }
    [Fact] public async Task Sustained_congestion_needs_three_samples_and_no_write()
    {
        var factory=new Factory(new DbContextOptionsBuilder<NetworkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using(var db=await factory.CreateDbContextAsync()){
            var ap=new Device{Name="Lab",Kind=DeviceKind.Ap535,ManagementIp="10.249.7.9",SecretRef="LAB"};
            db.Devices.Add(ap);
            for(var i=0;i<3;i++)db.TelemetrySnapshots.Add(new TelemetrySnapshot{DeviceId=ap.Id,AtUtc=DateTime.UtcNow.AddMinutes(-i*3),
                ApiConnected=true,ClientCount=0,ChannelUtilizationPct=85,ObservationSource="Instant show stats ap; labeled values (heuristic parsing)",ArmJson="{}"});
            await db.SaveChangesAsync();
        }
        var run=await new AnalysisService(factory).RunAsync("Hourly","scheduler");
        Assert.Equal(1,run.Findings);
        await using var verify=await factory.CreateDbContextAsync();
        var only=await verify.Recommendations.SingleAsync();
        Assert.Equal("Sustained RF congestion requiring review",only.Title);
        Assert.False(only.Executed);
    }
}
