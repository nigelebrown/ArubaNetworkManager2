using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Jcf.NetworkManager.Workflow;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jcf.NetworkManager.Tests;

/// <summary>0.9 real-device integration: credential vault, write policy, device-keyed gates, lifecycle, import, sample data.</summary>
public sealed class DeviceIntegrationTests
{
    sealed class Factory(DbContextOptions<NetworkDbContext> opts) : IDbContextFactory<NetworkDbContext> {
        public NetworkDbContext CreateDbContext()=>new(opts);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    static Factory NewDb() => new(new DbContextOptionsBuilder<NetworkDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    static IConfiguration Config(params (string Key,string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(v=>new KeyValuePair<string,string?>(v.Key,v.Value)).Append(new("Jcf:AllowedManagementCidrs:0","10.0.0.0/8"))).Build();
    sealed class CountingGateway : IApprovedChangeGateway {
        public int Calls;
        public Task<ChangeExecutionOutcome> ExecuteAsync(Device d,ConfigProposal p,CancellationToken ct){Calls++;return Task.FromResult(new ChangeExecutionOutcome(true,true,false,"ok"));}
    }

    [Fact]
    public async Task Vault_encrypts_credentials_and_never_lists_them()
    {
        var db=NewDb();
        var keys=new EphemeralDataProtectionProvider();
        var vault=new CredentialVault(db,keys);
        await vault.SetAsync("hq_instant","netadmin","S3cret-Device-Pass!","HQ cluster","admin");
        using(var check=db.CreateDbContext()) {
            var row=await check.DeviceCredentials.SingleAsync();
            Assert.Equal("HQ_INSTANT",row.Reference);
            Assert.DoesNotContain("S3cret",row.ProtectedPassword);
            Assert.DoesNotContain("netadmin",row.ProtectedUsername);
            Assert.DoesNotContain(await check.AuditEvents.ToListAsync(),a=>a.Outcome.Contains("S3cret")||a.Target.Contains("S3cret"));
        }
        var listing=JsonSerializer.Serialize(await vault.ListAsync());
        Assert.Contains("HQ_INSTANT",listing);
        Assert.DoesNotContain("S3cret",listing);
        Assert.DoesNotContain("netadmin",listing);
        Assert.Equal(("netadmin","S3cret-Device-Pass!"),await new SecretResolver(vault).ResolveAsync("HQ_INSTANT"));
        // A different key ring (lost/rotated keys) cannot decrypt: a clear, actionable error rather than garbage.
        var other=new SecretResolver(new CredentialVault(db,new EphemeralDataProtectionProvider()));
        var ex=await Assert.ThrowsAsync<InvalidOperationException>(()=>other.ResolveAsync("HQ_INSTANT"));
        Assert.Contains("cannot be decrypted",ex.Message);
    }

    [Fact]
    public async Task Missing_credentials_name_the_profile_and_in_use_profiles_cannot_be_deleted()
    {
        var db=NewDb();
        var vault=new CredentialVault(db,new EphemeralDataProtectionProvider());
        var ex=await Assert.ThrowsAsync<InvalidOperationException>(()=>new SecretResolver(vault).ResolveAsync("NOPE_123"));
        Assert.Contains("NOPE_123",ex.Message);
        await vault.SetAsync("SHARED","u","p",null,"admin");
        using(var d=db.CreateDbContext()){d.Devices.Add(new Device{Name="AP",Kind=DeviceKind.Ap505,ManagementIp="10.1.1.1",SecretRef="SHARED"});d.SaveChanges();}
        await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.DeleteAsync("SHARED","admin"));
        Assert.Contains(await vault.ListAsync(),v=>v.Reference=="SHARED" && v.DevicesUsing==1);
    }

    [Theory]
    [InlineData("AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89",true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",true)]
    [InlineData("abcdef",false)]
    [InlineData("zz cdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",false)]
    public void Certificate_fingerprints_accept_common_formats(string value,bool valid) =>
        Assert.Equal(valid,SecretResolver.NormalizeFingerprint(value) is { Length: 64 });

    [Fact]
    public void Device_pin_takes_precedence_over_environment_pin()
    {
        Environment.SetEnvironmentVariable("JCF_DEVICE_PINTEST_CERT_SHA256",new string('A',64));
        var resolver=new SecretResolver();
        var device=new Device{SecretRef="PINTEST"};
        Assert.Equal(new string('A',64),resolver.PinnedSha256(device));
        device.TlsCertSha256=new string('B',64);
        Assert.Equal(new string('B',64),resolver.PinnedSha256(device));
    }

    [Fact]
    public void Write_policy_requires_explicit_targets_and_accepts_legacy_keys()
    {
        Assert.False(DeviceWritePolicy.From(Config()).Permits("10.250.2.71"));
        var cidr=DeviceWritePolicy.From(Config(("Jcf:DeviceWrites:Enabled","true"),("Jcf:DeviceWrites:AllowedCidrs:0","10.250.2.0/24")));
        Assert.True(cidr.Permits("10.250.2.71"));
        Assert.False(cidr.Permits("10.250.3.1"));
        Assert.False(DeviceWritePolicy.From(Config(("Jcf:DeviceWrites:Enabled","true"))).Permits("10.250.2.71"));   // enabled but no targets
        var legacy=DeviceWritePolicy.From(Config(("Jcf:LiveLabWrites:Enabled","true"),("Jcf:LiveLabWrites:AllowedIps:0","10.250.2.71")));
        Assert.True(legacy.Permits("10.250.2.71"));
    }

    static async Task<(Factory Db,Guid Proposal,Guid Device)> RealApproved(int baseline,int live,bool certified=true)
    {
        var db=NewDb();
        var ap=new Device{Kind=DeviceKind.Ap505,Name="REAL AP",ManagementIp="10.250.2.71",SecretRef="LAB",Firmware="8.10.0.21",LastStatus="Healthy",LastSeenUtc=DateTime.UtcNow};
        var payload=NetworkChangePolicy.Normalize(ConfigAction.ChangeBandSteering,"{\"value\":\"balance-bands\"}");
        var hash=NetworkChangePolicy.Hash(payload);
        var p=new ConfigProposal{DeviceId=ap.Id,Action=ConfigAction.ChangeBandSteering,PayloadJson=payload,PayloadSha256=hash,ApprovedPayloadSha256=hash,
            RequestedBy="op",ApprovedBy="rev1",SecondApprovedBy="rev2",State=ConfigProposalState.Approved,ApprovedUtc=DateTime.UtcNow,
            BaselineFirmware="8.10.0.21",BaselineClusterMemberCount=baseline};
        using var c=db.CreateDbContext();
        c.Devices.Add(ap);c.ConfigProposals.Add(p);
        c.TelemetrySnapshots.Add(new TelemetrySnapshot{DeviceId=ap.Id,ApiConnected=true,ApCount=live});
        if(certified) c.DeviceCapabilities.Add(new DeviceCapability{DeviceId=ap.Id,Operation="arm.band-steering.write",Firmware="8.10.0.21",State=ApiCapabilityState.WriteVerified,EvidenceReference="LAB-TEST-0042"});
        await c.SaveChangesAsync();
        return (db,p.Id,ap.Id);
    }

    [Fact]
    public async Task Real_device_in_demo_mode_still_requires_every_live_gate()
    {
        var (db,id,_)=await RealApproved(1,1);
        var gateway=new CountingGateway();
        var demo=new ChangeWorkflowService(db,gateway,Config(("Jcf:DemoMode","true")));
        var ex=await Assert.ThrowsAsync<InvalidOperationException>(()=>demo.ExecuteAsync(id,"operator",true));
        Assert.Contains("Live device writes are disabled",ex.Message);
        var notListed=new ChangeWorkflowService(db,gateway,Config(("Jcf:DemoMode","true"),("Jcf:DeviceWrites:Enabled","true"),("Jcf:DeviceWrites:AllowedCidrs:0","10.249.0.0/16")));
        Assert.Contains("allowlist",(await Assert.ThrowsAsync<InvalidOperationException>(()=>notListed.ExecuteAsync(id,"operator",true))).Message);
        Assert.Equal(0,gateway.Calls);
        var allowed=new ChangeWorkflowService(db,gateway,Config(("Jcf:DeviceWrites:Enabled","true"),("Jcf:DeviceWrites:AllowedCidrs:0","10.250.2.0/24")));
        Assert.Equal(ConfigProposalState.Applied,(await allowed.ExecuteAsync(id,"operator",true)).State);
        Assert.Equal(1,gateway.Calls);
    }

    [Fact]
    public async Task Uncertified_firmware_and_cluster_scope_block_real_writes()
    {
        var writes=Config(("Jcf:DeviceWrites:Enabled","true"),("Jcf:DeviceWrites:AllowedCidrs:0","10.250.2.0/24"));
        var gateway=new CountingGateway();
        var (u,uid,_)=await RealApproved(1,1,certified:false);
        Assert.Contains("not been certified",(await Assert.ThrowsAsync<InvalidOperationException>(()=>new ChangeWorkflowService(u,gateway,writes).ExecuteAsync(uid,"op2",true))).Message);
        var (drift,did,_)=await RealApproved(3,4);
        Assert.Contains("membership changed",(await Assert.ThrowsAsync<InvalidOperationException>(()=>new ChangeWorkflowService(drift,gateway,writes).ExecuteAsync(did,"op2",true))).Message);
        var (wide,wid,_)=await RealApproved(3,3);
        Assert.Contains("cluster-wide writes are disabled",(await Assert.ThrowsAsync<InvalidOperationException>(()=>new ChangeWorkflowService(wide,gateway,writes).ExecuteAsync(wid,"op2",true))).Message);
        Assert.Equal(0,gateway.Calls);
        var clusterOk=Config(("Jcf:DeviceWrites:Enabled","true"),("Jcf:DeviceWrites:AllowedCidrs:0","10.250.2.0/24"),("Jcf:DeviceWrites:AllowClusterWide","true"));
        Assert.Equal(ConfigProposalState.Applied,(await new ChangeWorkflowService(wide,gateway,clusterOk).ExecuteAsync(wid,"op2",true)).State);
    }

    [Fact]
    public async Task Simulation_gateway_refuses_real_equipment()
    {
        var outcome=await new DemoChangeGateway().ExecuteAsync(new Device{IsSimulated=false},new ConfigProposal(),CancellationToken.None);
        Assert.False(outcome.Success);
        var routed=new RoutingChangeGateway(new DemoChangeGateway(),new ArubaLabApprovedChangeGateway(new DeviceHttpFactory(new SecretResolver()),new SecretResolver()));
        Assert.True((await routed.ExecuteAsync(new Device{IsSimulated=true},new ConfigProposal(),CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Csv_import_registers_valid_rows_and_reports_each_rejection()
    {
        var db=NewDb();
        using(var c=db.CreateDbContext()){c.Locations.Add(new Location{Name="Headquarters",Type="HQ"});c.SaveChanges();}
        var service=new DeviceService(db,[],Config());
        var rows=await service.ImportCsvAsync("name,model,ip,profile,location,cluster\nHQ-AP-01,AP-505,10.250.2.71,hq_instant,headquarters,HQ-VC\n"+
            "HQ-CORE,cx6200,10.249.1.21,HQ_SWITCH\nBAD-1,AP-999,10.250.2.72,X\nBAD-2,AP-535,8.8.8.8,X\nDUP,AP-535,10.250.2.71,X\nBAD-3,AP-535,10.250.2.80,X,Nowhere\n\"Quoted, Name\",535,10.250.2.81,X","importer");
        Assert.Equal(new[]{true,true,false,false,false,false,true},rows.Select(r=>r.Success).ToArray());
        Assert.Contains("Unknown model",rows[2].Outcome);
        Assert.Contains("approved management subnet",rows[3].Outcome);
        Assert.Contains("already registered",rows[4].Outcome);
        Assert.Contains("not found",rows[5].Outcome);
        using var check=db.CreateDbContext();
        var ap=await check.Devices.SingleAsync(d=>d.Name=="HQ-AP-01");
        Assert.Equal(("HQ_INSTANT","HQ-VC",false),(ap.SecretRef,ap.ClusterName,ap.IsSimulated));
        Assert.NotNull(ap.LocationId);
        Assert.Equal(3,await check.Devices.CountAsync());
        Assert.Equal(DeviceKind.Ap535,DeviceService.ParseModel("Aruba AP-535"));
    }

    [Fact]
    public async Task Removing_a_device_or_the_sample_set_keeps_history_and_real_equipment()
    {
        var db=NewDb();
        Guid real,sample;
        using(var c=db.CreateDbContext()) {
            await SampleDataService.SeedAsync(c);
            var r=new Device{Name="REAL",Kind=DeviceKind.Cx6100,ManagementIp="10.249.1.21",SecretRef="X"};
            c.Devices.Add(r);c.TelemetrySnapshots.Add(new TelemetrySnapshot{DeviceId=r.Id});
            await c.SaveChangesAsync();
            real=r.Id;sample=c.Devices.First(d=>d.IsSimulated).Id;
            c.TelemetrySnapshots.Add(new TelemetrySnapshot{DeviceId=sample});
            c.ConfigProposals.Add(new ConfigProposal{DeviceId=sample,State=ConfigProposalState.PendingApproval});
            await c.SaveChangesAsync();
        }
        Assert.Equal(3,await new SampleDataService(db).RemoveAsync("admin"));
        using(var c=db.CreateDbContext()) {
            Assert.Equal(real,(await c.Devices.SingleAsync()).Id);
            Assert.Single(c.TelemetrySnapshots);
            Assert.Equal(ConfigProposalState.Rejected,(await c.ConfigProposals.SingleAsync()).State);   // history retained, closed
            Assert.Contains(c.AuditEvents,a=>a.Action=="Remove sample data");
            Assert.DoesNotContain(c.Locations,l=>l.Name.Contains("(demo)"));
        }
        await new DeviceService(db,[],Config()).RemoveAsync(real,"admin");
        using(var c=db.CreateDbContext()) {
            Assert.Empty(c.Devices);
            Assert.Empty(c.TelemetrySnapshots);
            Assert.Contains(c.AuditEvents,a=>a.Action=="Remove device");
        }
        Assert.Equal(3,await new SampleDataService(db).RestoreAsync("admin"));
    }

    [Fact]
    public async Task Sample_device_uses_simulator_and_real_device_uses_the_aruba_connector()
    {
        var db=NewDb();
        var connectors=new IDeviceConnector[]{new InstantConnector(new DeviceHttpFactory(new SecretResolver()),new SecretResolver()),
            new CxConnector(new DeviceHttpFactory(new SecretResolver()),new SecretResolver()),new DemoConnector()};
        var service=new DeviceService(db,connectors,Config());
        Guid sample,real;
        using(var c=db.CreateDbContext()){
            var s=new Device{Name="S",Kind=DeviceKind.Ap505,ManagementIp="192.0.2.5",SecretRef="SAMPLE",IsSimulated=true};
            var r=new Device{Name="R",Kind=DeviceKind.Ap505,ManagementIp="10.250.2.250",SecretRef="NOT_PROVISIONED"};
            c.Devices.AddRange(s,r);c.SaveChanges();sample=s.Id;real=r.Id;
        }
        var simulated=await service.ProbeAsync(sample,"op");
        Assert.True(simulated.Connected);
        Assert.Contains(simulated.ApiChecks,x=>x.Contains("SIMULATED"));
        var live=await service.ProbeAsync(real,"op");
        Assert.False(live.Connected);
        Assert.DoesNotContain(live.ApiChecks,x=>x.Contains("SIMULATED"));
        Assert.Contains("NOT_PROVISIONED",live.Issue);
        using var check=db.CreateDbContext();
        Assert.Equal("Offline",(await check.Devices.SingleAsync(d=>d.Id==real)).LastStatus);
        Assert.Contains(check.NetworkAlerts,a=>a.DeviceId==real && a.Severity=="Critical" && a.Detail.Contains("NOT_PROVISIONED"));
        Assert.DoesNotContain(check.AuditEvents,a=>a.Actor==DeviceService.PollingActor);
        await service.ProbeAsync(sample,DeviceService.PollingActor);
        Assert.DoesNotContain(db.CreateDbContext().AuditEvents,a=>a.Actor==DeviceService.PollingActor);   // scheduled polls are not audited
    }

    [Fact]
    public void Poll_groups_serialize_named_clusters_only()
    {
        Guid a=Guid.NewGuid(),b=Guid.NewGuid();
        Assert.Equal(PollingWorker.PollGroup(a,DeviceKind.Ap505,"HQ-VC"),PollingWorker.PollGroup(b,DeviceKind.Ap535,"hq-vc "));
        Assert.NotEqual(PollingWorker.PollGroup(a,DeviceKind.Ap505,null),PollingWorker.PollGroup(b,DeviceKind.Ap505,null));
        Assert.NotEqual(PollingWorker.PollGroup(a,DeviceKind.Cx6100,"HQ-VC"),PollingWorker.PollGroup(b,DeviceKind.Cx6100,"HQ-VC"));
    }
}

/// <summary>Demo host over HTTP: sample devices are simulated, devices an operator registers are real.</summary>
public sealed class DemoRealDeviceApiTests : IClassFixture<DemoRealDeviceApiTests.Host>
{
    public sealed class Host : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jcf:DemoMode","true");
            builder.UseSetting("Jcf:Polling:Enabled","false");
            builder.UseSetting("Jcf:DemoDatabaseName","jcf-real-in-demo-"+Guid.NewGuid().ToString("N"));
            builder.UseEnvironment("Development");
        }
    }
    private readonly Host host;
    public DemoRealDeviceApiTests(Host host)=>this.host=host;
    HttpClient As(string persona){var c=host.CreateClient(new WebApplicationFactoryClientOptions{HandleCookies=false,AllowAutoRedirect=false});c.DefaultRequestHeaders.Add("Cookie","jcf_demo_persona="+persona);return c;}
    static string NewIp()=>"10.88."+Random.Shared.Next(1,250)+"."+Random.Shared.Next(1,250);

    [Fact]
    public async Task Registered_device_in_demo_uses_the_real_connector_and_secrets_never_leave_the_server()
    {
        using var op=As("operator");
        var created=await op.PostAsJsonAsync("/api/v1/devices",new{name="Real AP",kind="Ap505",managementIp=NewIp(),secretRef="lab_ap",
            clusterName="LAB-VC",username="labadmin",password="Very-Secret-Pass-1"});
        Assert.Equal(HttpStatusCode.OK,created.StatusCode);
        var body=await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Very-Secret",body);
        Assert.Contains("\"isSimulated\":false",body);
        Assert.Contains("\"credentialProfile\":\"LAB_AP\"",body);
        var id=JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        var creds=await op.GetStringAsync("/api/v1/credentials");
        Assert.Contains("LAB_AP",creds);
        Assert.Contains("Encrypted profile",creds);
        Assert.DoesNotContain("Very-Secret",creds);
        Assert.DoesNotContain("labadmin",creds);
        Assert.DoesNotContain("Very-Secret",await op.GetStringAsync("/api/v1/devices"));
        Assert.DoesNotContain("Very-Secret",await op.GetStringAsync("/api/v1/audit"));

        // The probe really goes to the network (nothing listens at a random 10.88.x.x): no simulated success.
        var probe=await (await op.PostAsync($"/api/v1/devices/{id}/probe",null)).Content.ReadAsStringAsync();
        Assert.Contains("\"connected\":false",probe);
        Assert.DoesNotContain("SIMULATED",probe);

        using var viewer=As("viewer");
        Assert.Equal(HttpStatusCode.Forbidden,(await viewer.GetAsync("/api/v1/credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await viewer.PutAsJsonAsync("/api/v1/credentials/X",new{username="a",password="b"})).StatusCode);
    }

    [Fact]
    public async Task Device_lifecycle_import_alerts_and_sample_data_over_the_api()
    {
        using var op=As("operator");
        var import=await op.PostAsJsonAsync("/api/v1/devices/import",new{csv="Imp-1,AP-535,"+NewIp()+",IMP\nImp-bad,AP-1,"+NewIp()+",IMP"});
        var result=await import.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1,result.GetProperty("registered").GetInt32());
        Assert.Equal(1,result.GetProperty("failed").GetInt32());
        var id=result.GetProperty("rows")[0].GetProperty("deviceId").GetGuid();
        Assert.Equal(HttpStatusCode.OK,(await op.PutAsJsonAsync($"/api/v1/devices/{id}",new{name="Imp-1 renamed",credentialProfile="IMP2",clusterName="VC-9"})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await op.PostAsync($"/api/v1/devices/{id}/disable",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await op.PostAsync($"/api/v1/devices/{id}/probe",null)).StatusCode);   // disabled devices are not contacted
        Assert.Contains("\"lastStatus\":\"Disabled\"",await op.GetStringAsync("/api/v1/devices"));
        Assert.Equal(HttpStatusCode.NoContent,(await op.PostAsync($"/api/v1/devices/{id}/enable",null)).StatusCode);
        Assert.DoesNotContain("\"lastStatus\":\"Disabled\"",await op.GetStringAsync("/api/v1/devices"));
        Assert.Equal(HttpStatusCode.BadRequest,(await op.PostAsJsonAsync($"/api/v1/devices/{id}/tls/pin",new{sha256="nothex"})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await op.PostAsJsonAsync($"/api/v1/devices/{id}/tls/pin",new{sha256=new string('c',64)})).StatusCode);
        Assert.Contains("\"tlsPinned\":true",await op.GetStringAsync("/api/v1/devices"));
        Assert.Equal(HttpStatusCode.NoContent,(await op.DeleteAsync($"/api/v1/devices/{id}")).StatusCode);
        Assert.DoesNotContain("Imp-1 renamed",await op.GetStringAsync("/api/v1/devices"));
        Assert.Contains("Remove device",await op.GetStringAsync("/api/v1/audit"));

        // An offline real device raises an alert that operators can triage.
        var real=await (await op.PostAsJsonAsync("/api/v1/devices",new{name="Alerting CX",kind="Cx6100",managementIp=NewIp(),secretRef="NONE"})).Content.ReadFromJsonAsync<JsonElement>();
        await op.PostAsync($"/api/v1/devices/{real.GetProperty("id").GetGuid()}/probe",null);
        var alert=(await op.GetFromJsonAsync<JsonElement>("/api/v1/alerts")).EnumerateArray().First(a=>a.GetProperty("deviceId").GetString()==real.GetProperty("id").GetString());
        var alertId=alert.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,(await op.PostAsJsonAsync($"/api/v1/alerts/{alertId}/state",new{state="Acknowledged"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await op.PostAsJsonAsync($"/api/v1/alerts/{alertId}/state",new{state="Open"})).StatusCode);
        using var viewer=As("viewer");
        Assert.Equal(HttpStatusCode.Forbidden,(await viewer.PostAsJsonAsync($"/api/v1/alerts/{alertId}/state",new{state="Resolved"})).StatusCode);

        // Sample data removal is an administrator action and leaves registered (real) devices alone.
        Assert.Equal(HttpStatusCode.Forbidden,(await op.PostAsync("/api/v1/admin/sample-data/remove",null)).StatusCode);
        using var admin=As("admin");
        var removed=await (await admin.PostAsync("/api/v1/admin/sample-data/remove",null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3,removed.GetProperty("removed").GetInt32());
        var left=await op.GetStringAsync("/api/v1/devices");
        Assert.Contains("Alerting CX",left);
        Assert.DoesNotContain("192.0.2.",left);
        await admin.PostAsync("/api/v1/admin/sample-data/load",null);
        Assert.Contains("192.0.2.",await op.GetStringAsync("/api/v1/devices"));
    }

    [Fact]
    public async Task Approved_sample_change_executes_through_the_api_with_separation_of_duties()
    {
        using var op=As("operator");
        var ap=(await op.GetFromJsonAsync<JsonElement>("/api/v1/devices")).EnumerateArray()
            .First(d=>d.GetProperty("isSimulated").GetBoolean() && d.GetProperty("model").GetString()=="Ap505").GetProperty("id").GetGuid();
        await op.PostAsync($"/api/v1/devices/{ap}/probe",null);
        var proposal=await (await op.PostAsJsonAsync("/api/v1/change-proposals",new{deviceId=ap,action="ChangeClientMatch",payloadJson="{\"value\":\"disabled\"}",justification="demo"}))
            .Content.ReadFromJsonAsync<JsonElement>();
        var pid=proposal.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict,(await op.PostAsync($"/api/v1/change-proposals/{pid}/execute",null)).StatusCode);   // not approved yet
        using var r1=As("reviewer-one"); using var r2=As("reviewer-two");
        Assert.Equal(HttpStatusCode.OK,(await r1.PostAsJsonAsync($"/api/v1/change-proposals/{pid}/decision",new{decision="approve"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await r1.PostAsJsonAsync($"/api/v1/change-proposals/{pid}/decision",new{decision="approve"})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await r2.PostAsJsonAsync($"/api/v1/change-proposals/{pid}/decision",new{decision="approve"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await r1.PostAsync($"/api/v1/change-proposals/{pid}/execute",null)).StatusCode);   // approvers cannot execute
        var executed=await op.PostAsync($"/api/v1/change-proposals/{pid}/execute",null);
        var text=await executed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK,executed.StatusCode);
        Assert.Contains("Applied",text);
        Assert.Contains("SIMULATION ONLY",text);
    }
}
