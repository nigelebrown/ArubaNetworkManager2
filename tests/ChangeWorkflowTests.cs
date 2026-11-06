using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class ChangeWorkflowTests
{
    sealed class Factory(DbContextOptions<NetworkDbContext> opts) : IDbContextFactory<NetworkDbContext> {
        public NetworkDbContext CreateDbContext()=>new(opts);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    sealed class RecordingGateway : IApprovedChangeGateway {
        public int Calls;
        public bool Fail;
        public Task<ChangeExecutionOutcome> ExecuteAsync(Device d,ConfigProposal p,CancellationToken ct) {
            Calls++;
            return Task.FromResult(Fail?new ChangeExecutionOutcome(false,false,true,"Simulated restore failure"):
                new ChangeExecutionOutcome(true,true,false,"Mock successful preflight, apply, readback"));
        }
    }
    static (ChangeWorkflowService Service,Factory Db,RecordingGateway Gateway,Guid ApId,Guid CxId) Start()
    {
        var options=new DbContextOptionsBuilder<NetworkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var dbf=new Factory(options);
        Guid apId,cxId;
        using(var db=dbf.CreateDbContext()) {
            // Workflow semantics are exercised on SAMPLE devices (simulation gateway); real-device gates are covered in DeviceIntegrationTests.
            var ap=new Device{Kind=DeviceKind.Ap505,Name="LAB AP",ManagementIp="10.250.2.71",SecretRef="LAB",Firmware="8.10.0.21",LastStatus="Healthy",LastSeenUtc=DateTime.UtcNow,IsSimulated=true};
            var cx=new Device{Kind=DeviceKind.Cx6100,Name="LAB CX",ManagementIp="10.249.1.25",SecretRef="CX",Firmware="10.13.0001",IsSimulated=true};
            apId=ap.Id;cxId=cx.Id;
            db.Devices.AddRange(ap,cx);
            db.TelemetrySnapshots.Add(new TelemetrySnapshot{DeviceId=apId,ApCount=1});
            db.SaveChanges();
        }
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Jcf:DemoMode","true"},{"Jcf:LiveLabWrites:Enabled","false"}}).Build();
        var gateway=new RecordingGateway();
        return(new ChangeWorkflowService(dbf,gateway,config),dbf,gateway,apId,cxId);
    }
    [Theory]
    [InlineData("{\"value\":80,\"channelWidthMhz\":80}")]
    [InlineData("{\"value\":9,\"channelWidthMhz\":160}")]
    [InlineData("{\"value\":9,\"channel5Ghz\":100}")]
    [InlineData("{\"value\":9,\"channel5Ghz\":52}")]
    public void Radio_policy_rejects_prohibited_width_or_dfs_channel(string payload) =>
        Assert.Throws<ArgumentException>(()=>NetworkChangePolicy.Normalize(ConfigAction.ChangeMinTxPower,payload));

    [Fact] public void Supported_power_and_channel_policy_does_not_widen_channel()
    {
        var normalized=NetworkChangePolicy.Normalize(ConfigAction.ChangeMinTxPower,"{\"value\":9,\"channelWidthMhz\":40,\"channel5Ghz\":149}");
        Assert.Equal("{\"value\":9}",normalized); // width/channel are validated but not transmitted by this operation.
        Assert.Equal(new[]{20,40},NetworkChangePolicy.ApprovedWidthsMhz);
    }
    [Fact] public void Wireless_secret_is_a_reference_and_invalid_models_are_rejected()
    {
        var normalized=NetworkChangePolicy.Normalize(ConfigAction.CreateSsid,"{\"ssid\":\"JCF-LAB\",\"vlan\":120,\"pskSecretRef\":\"WIFI_LAB\"}");
        Assert.Contains("WIFI_LAB",normalized);
        Assert.DoesNotContain("password",normalized,StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(()=>NetworkChangePolicy.CheckModel(new Device{Kind=DeviceKind.Cx6100},ConfigAction.CreateSsid));
    }
    [Fact] public async Task Standard_change_requires_independent_approval_then_records_demo_execution()
    {
        var (s,dbf,g,id,_) = Start();
        var req=await s.SubmitAsync(id,ConfigAction.CreateSsid,"{\"ssid\":\"JCF-TEST\",\"vlan\":120,\"pskSecretRef\":\"LAB_WIFI\"}","operator-a","test");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ExecuteAsync(req.Id,"operator-a",true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>s.DecideAsync(req.Id,"approve","operator-a",true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>s.DecideAsync(req.Id,"approve","viewer",false));
        var p=await s.DecideAsync(req.Id,"approve","approver-b",true);
        Assert.Equal(ConfigProposalState.Approved,p.State);
        var done=await s.ExecuteAsync(req.Id,"operator-a",true);
        Assert.Equal(ConfigProposalState.Applied,done.State);
        Assert.Equal(1,g.Calls);
        await using var db=await dbf.CreateDbContextAsync();
        Assert.Equal(2,await db.ConfigExecutionEvents.CountAsync());
        Assert.Contains(await db.AuditEvents.ToListAsync(),x=>x.Action=="Execute approved change");
    }
    [Fact] public async Task RF_change_requires_two_independent_approvers()
    {
        var (s,_,g,id,_) = Start();
        var req=await s.SubmitAsync(id,ConfigAction.ChangeBandSteering,"{\"value\":\"balance-bands\"}","operator-a","lab");
        var p=await s.DecideAsync(req.Id,"approve","reviewer-b",true);
        Assert.Equal(ConfigProposalState.PendingSecondApproval,p.State);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ExecuteAsync(req.Id,"operator-a",true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>s.DecideAsync(req.Id,"approve","reviewer-b",true));
        p=await s.DecideAsync(req.Id,"approve","reviewer-c",true);
        Assert.Equal(ConfigProposalState.Approved,p.State);
        Assert.Equal(ConfigProposalState.Applied,(await s.ExecuteAsync(req.Id,"operator-a",true)).State);
        Assert.Equal(1,g.Calls);
    }
    [Fact] public async Task Payload_tampering_blocks_execution_before_device_gateway()
    {
        var (s,dbf,g,id,_) = Start();
        var req=await s.SubmitAsync(id,ConfigAction.ChangeMinTxPower,"{\"value\":9}","operator-a","lab");
        await s.DecideAsync(req.Id,"approve","reviewer-b",true);
        await s.DecideAsync(req.Id,"approve","reviewer-c",true);
        await using(var db=await dbf.CreateDbContextAsync()) {var row=await db.ConfigProposals.FindAsync(req.Id);row!.PayloadJson="{\"value\":17}";await db.SaveChangesAsync();}
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.ExecuteAsync(req.Id,"operator-a",true));
        Assert.Equal(0,g.Calls);
    }
    [Fact] public async Task Failed_rollback_is_critical_and_keeps_recovery_state()
    {
        var (s,dbf,g,id,_) = Start();g.Fail=true;
        var req=await s.SubmitAsync(id,ConfigAction.CreateSsid,"{\"ssid\":\"JCF-TEST\",\"vlan\":120,\"pskSecretRef\":\"LAB_WIFI\"}","operator-a","lab");
        await s.DecideAsync(req.Id,"approve","reviewer-b",true);
        var outcome=await s.ExecuteAsync(req.Id,"operator-a",true);
        Assert.Equal(ConfigProposalState.RecoveryRequired,outcome.State);
        await using var db=await dbf.CreateDbContextAsync();
        Assert.Contains(await db.NetworkAlerts.ToListAsync(),x=>x.Severity=="Critical");
    }
    [Fact] public async Task Switch_target_cannot_receive_wireless_config()
    {
        var (s,_,g,_,cxId)=Start();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.SubmitAsync(cxId,ConfigAction.CreateSsid,
            "{\"ssid\":\"JCF-TEST\",\"vlan\":120,\"pskSecretRef\":\"LAB_WIFI\"}","operator-a","test"));
        Assert.Equal(0,g.Calls);
    }
}
