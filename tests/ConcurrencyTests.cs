using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jcf.NetworkManager.Tests;

/// <summary>An approved change must reach the device gateway at most once, even under concurrent Execute clicks.</summary>
public sealed class ConcurrencyTests
{
    sealed class Factory(DbContextOptions<NetworkDbContext> opts) : IDbContextFactory<NetworkDbContext> {
        public NetworkDbContext CreateDbContext()=>new(opts);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    sealed class CountingGateway : IApprovedChangeGateway {
        public int Calls;
        public Task<ChangeExecutionOutcome> ExecuteAsync(Device d,ConfigProposal p,CancellationToken ct) {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ChangeExecutionOutcome(true,true,false,"mock apply"));
        }
    }
    /// <summary>Holds each "mark Executing" save until both callers have loaded the Approved proposal (forces the race).</summary>
    sealed class RendezvousInterceptor : SaveChangesInterceptor {
        int arrived; readonly TaskCompletionSource both=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e,InterceptionResult<int> r,CancellationToken ct=default) {
            var executing=e.Context!.ChangeTracker.Entries<ConfigProposal>().Any(x=>x.State==EntityState.Modified && x.Entity.State==ConfigProposalState.Executing);
            if(executing) {
                if(Interlocked.Increment(ref arrived)>=2) both.TrySetResult();
                await Task.WhenAny(both.Task,Task.Delay(3000,ct));
            }
            return r;
        }
    }

    [Fact]
    public async Task Concurrent_execute_requests_call_the_device_gateway_once()
    {
        var options=new DbContextOptionsBuilder<NetworkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new RendezvousInterceptor()).Options;
        var dbf=new Factory(options);
        var proposalId=Guid.NewGuid();
        using(var db=dbf.CreateDbContext()) {
            var ap=new Device{Kind=DeviceKind.Ap505,Name="LAB AP",ManagementIp="10.250.2.71",SecretRef="LAB",Firmware="8.10.0.21",LastStatus="Healthy",LastSeenUtc=DateTime.UtcNow,IsSimulated=true};
            db.Devices.Add(ap);
            var payload=NetworkChangePolicy.Normalize(ConfigAction.ChangeBandSteering,"{\"value\":\"balance-bands\"}");
            var hash=NetworkChangePolicy.Hash(payload);
            db.ConfigProposals.Add(new ConfigProposal{Id=proposalId,DeviceId=ap.Id,Action=ConfigAction.ChangeBandSteering,PayloadJson=payload,
                PayloadSha256=hash,ApprovedPayloadSha256=hash,RequestedBy="op",ApprovedBy="rev1",SecondApprovedBy="rev2",
                State=ConfigProposalState.Approved,ApprovedUtc=DateTime.UtcNow,BaselineFirmware="8.10.0.21",BaselineClusterMemberCount=1});
            db.SaveChanges();
        }
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Jcf:DemoMode","true"}}).Build();
        var gateway=new CountingGateway();
        var service=new ChangeWorkflowService(dbf,gateway,config);

        var outcomes=await Task.WhenAll(
            Run(()=>service.ExecuteAsync(proposalId,"operator-a",true)),
            Run(()=>service.ExecuteAsync(proposalId,"operator-b",true)));

        Assert.Equal(1,gateway.Calls);
        Assert.Single(outcomes,o=>o is null);                       // exactly one execution proceeded
        Assert.Single(outcomes,o=>o is InvalidOperationException);  // the other was refused as a state conflict
        using var check=dbf.CreateDbContext();
        Assert.Equal(ConfigProposalState.Applied,(await check.ConfigProposals.SingleAsync()).State);
    }

    static async Task<Exception?> Run(Func<Task> action) { try { await Task.Run(action); return null; } catch(Exception ex) { return ex; } }
}
