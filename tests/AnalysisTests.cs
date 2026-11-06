using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class AnalysisTests
{
    private sealed class Factory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext() => new(options);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }

    [Fact]
    public async Task AnalyzeNow_generates_explainable_policy_drift_but_never_executes()
    {
        var opts=new DbContextOptionsBuilder<NetworkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var factory=new Factory(opts);
        Guid id;
        await using(var db=await factory.CreateDbContextAsync())
        {
            var d=new Device{Name="LAB AP",Kind=DeviceKind.Ap505,ManagementIp="10.250.2.71",SecretRef="SIM"};
            id=d.Id;db.Devices.Add(d);
            db.TelemetrySnapshots.Add(new TelemetrySnapshot {DeviceId=id,ClientCount=null,
                ArmJson=JsonSerializer.Serialize(new Dictionary<string,string>{{"80mhz-support","enabled"}})});
            await db.SaveChangesAsync();
        }
        var analyzer=new AnalysisService(factory);
        var run=await analyzer.RunAsync("Manual","test-operator");
        Assert.Equal(2,run.Findings);
        await using(var db=await factory.CreateDbContextAsync())
        {
            var recommendations=await db.Recommendations.ToListAsync();
            Assert.Equal(2,recommendations.Count);
            Assert.All(recommendations,r=>Assert.False(r.Executed));
            Assert.Contains(recommendations,r=>r.Fingerprint.EndsWith("RF_WIDTH_80_ENABLED"));
        }
        // Re-running analysis does not create duplicate pending recommendations.
        await analyzer.RunAsync("Manual","test-operator");
        await using(var db=await factory.CreateDbContextAsync())
            Assert.Equal(2,await db.Recommendations.CountAsync());
    }

    [Fact]
    public async Task Creator_cannot_approve_own_recommendation()
    {
        var opts=new DbContextOptionsBuilder<NetworkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var factory=new Factory(opts);
        Guid id;
        await using(var db=await factory.CreateDbContextAsync())
        {
            var r=new Recommendation{Fingerprint="test",Title="Test",CreatedBy="operator-a"};
            id=r.Id;db.Recommendations.Add(r);await db.SaveChangesAsync();
        }
        var analysis=new AnalysisService(factory);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>analysis.DecideAsync(id,"approve","operator-a"));
        var approved=await analysis.DecideAsync(id,"approve","approver-b");
        Assert.Equal(RecommendationState.Approved,approved.State);
        Assert.False(approved.Executed);
    }
}
