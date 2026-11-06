using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Jcf.NetworkManager.Workflow;
using Xunit;

namespace Jcf.NetworkManager.Tests;

/// <summary>
/// Exercises the real InstantConnector, CxConnector and ArubaLabApprovedChangeGateway against in-process
/// simulations of the Aruba Instant 8.x and AOS-CX REST APIs (documented request/response shapes).
/// This proves the adapters' session, read, write, readback, rollback and logout logic. It is NOT a
/// substitute for the supervised hardware acceptance in docs/RELEASE_0_8_SQL_HARDWARE_ACCEPTANCE.md.
/// </summary>
public sealed partial class SimulatedDeviceTests
{
    // ---------- Simulated Aruba Instant virtual controller ----------
    internal sealed class InstantSim : HttpMessageHandler
    {
        public string User = "labadmin", Pass = "lab-pass-1";
        public int ApCount = 1;
        public readonly Dictionary<string, string> Arm = new() { ["band-steering-mode"] = "prefer-higher-band", ["min-tx-power"] = "9" };
        public readonly List<string> Ssids = new();
        public bool IgnoreArmWrites, ReportFailedStatus;
        public Action? OnFirstArmWrite;
        public int ArmWrites, Logouts, Logins;
        public readonly List<string> Requests = new();
        const string Sid = "SIM-SESSION-1";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();   // like SocketsHttpHandler: a cancelled request never reaches the device
            var path = req.RequestUri!.AbsolutePath.TrimStart('/');
            var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            Requests.Add(req.Method + " " + path);
            if (req.RequestUri.Port != 4343) return new HttpResponseMessage(HttpStatusCode.NotFound);
            switch (path)
            {
                case "rest/login":
                    Logins++;
                    var login = JsonNode.Parse(body)!;
                    return (string?)login["user"] == User && (string?)login["passwd"] == Pass
                        ? Json(new { Status = "Success", sid = Sid })          // Aruba replies with capital-S "Success"
                        : Json(new { Status = "Failed", message = "Invalid credentials" }, HttpStatusCode.Unauthorized);
                case "rest/logout":
                    Logouts++; return Json(new { Status = "Success" });
            }
            if (query["sid"] != Sid) return Json(new { Status = "Failed", message = "Invalid session" }, HttpStatusCode.Unauthorized);
            switch (path)
            {
                case "rest/show-cmd":
                    return Json(new Dictionary<string, object> { ["Status-code"] = 0, ["Command output"] = Show(query["cmd"]!) });
                case "rest/arm":
                    ArmWrites++;
                    var arm = JsonNode.Parse(body)!["arm"]!.AsObject();
                    if (ArmWrites == 1 && OnFirstArmWrite is { } hook)
                    {
                        Apply(arm);                       // the device applied it...
                        hook();                           // ...then the caller's request was cancelled
                        ct.ThrowIfCancellationRequested();
                    }
                    if (ReportFailedStatus) return Json(new { Status = "Failed", message = "Rejected" });
                    if (!IgnoreArmWrites) Apply(arm);
                    return Json(new Dictionary<string, object> { ["Status-code"] = 0, ["message"] = "Success" });
                case "rest/ssid":
                    var profile = JsonNode.Parse(body)!["ssid-profile"]!;
                    var name = (string)profile["ssid-profile"]!;
                    if ((string?)profile["action"] == "delete") Ssids.Remove(name); else Ssids.Add(name);
                    return Json(new Dictionary<string, object> { ["Status-code"] = 0 });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        public bool IgnoreFlagWrites;
        void Apply(JsonObject arm)
        {
            foreach (var kv in arm.Where(k => k.Key != "action"))
            {
                if (kv.Value!["value"] is { } v) Arm[kv.Key] = (string)v!;
                else if (IgnoreFlagWrites && ArubaLabApprovedChangeGateway.ArmFlags.Contains(kv.Key)) continue;
                else if ((string?)kv.Value["action"] == "delete") Arm.Remove(kv.Key);
                else Arm[kv.Key] = "";          // bare CLI flag, e.g. "client-match"
            }
        }
        string Show(string cmd) => cmd switch
        {
            "show version" => "Aruba Operating System Software.\nArubaOS (MODEL: 505), Version 8.10.0.21_89341\n",
            "show aps" => $"{ApCount} Access Points\nName           IP Address    Mode    Type\n" +
                          string.Join("\n", Enumerable.Range(1, ApCount).Select(i => $"LAB-AP-{i}   10.250.2.{70 + i}  access  AP-505")),
            "show clients" => "Client List\n-----------\nName  IP  MAC\nlaptop 10.250.20.5 aa:bb:cc:dd:ee:01\n1 Clients\n",
            "show running-config" => "version 8.10.0.0-8.10.0\nvirtual-controller-country JM\narm\n" +
                                     string.Join("", Arm.Select(kv => kv.Value.Length == 0 ? $" {kv.Key}\n" : $" {kv.Key} {kv.Value}\n")) +
                                     string.Join("", Ssids.Select(s => $"wlan ssid-profile {s}\n vlan 120\n")) + "!\n",
            "show arm config" => "ARM config\n",
            _ when cmd.StartsWith("show stats ap") => "Channel: 36\nChannel Width (MHz): 40\nChannel Utilization (percent): 23\nNoise Floor (dBm): -95\n",
            _ => ""
        };
    }

    // ---------- Simulated AOS-CX switch ----------
    internal sealed class CxSim : HttpMessageHandler
    {
        public readonly Dictionary<int, string> Vlans = new() { [1] = "DEFAULT_VLAN_1" };
        public int Logouts;
        public bool RejectCreate, IgnorePatches;
        public int Patches;
        // Interface VLAN state: 1/1/1 access VLAN 1; 1/1/49 uplink trunk with an explicit list; 1/1/50 trunk allowing all VLANs.
        public readonly Dictionary<string, (string Mode, int Tag, List<int> Trunks)> Ports = new()
        {
            ["1/1/1"] = ("access", 1, new()), ["1/1/49"] = ("native-untagged", 1, new() { 1, 10 }), ["1/1/50"] = ("native-untagged", 1, new())
        };
        public readonly Dictionary<string, JsonObject> Poe = new() { ["1/1/1"] = new JsonObject { ["admin_disable"] = false, ["priority"] = "high" } };
        static string Ref(int id) => "/rest/v10.13/system/vlans/" + id;
        const string Csrf = "SIM-CSRF";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath.TrimStart('/');
            var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
            if (path == "rest/latest/login" && req.Method == HttpMethod.Post)
            {
                if (query["username"] != "cxadmin" || query["password"] != "cx-pass-1") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                var ok = new HttpResponseMessage(HttpStatusCode.OK);
                if (req.Headers.TryGetValues("x-use-csrf-token", out var v) && v.Contains("true")) ok.Headers.Add("x-csrf-token", Csrf);
                return ok;
            }
            // Every other call needs the CSRF token from the session login.
            if (!req.Headers.TryGetValues("x-csrf-token", out var t) || t.First() != Csrf) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (path == "rest/latest/logout") { Logouts++; return new HttpResponseMessage(HttpStatusCode.OK); }
            if (path == "rest/latest/system") return Json(new { software_version = "PL.10.13.1010" });
            if (path == "rest/latest/system/vlans" && req.Method == HttpMethod.Get)
                return Json(Vlans.ToDictionary(k => k.Key.ToString(), k => (object)new { id = k.Key, name = k.Value }));
            if (path == "rest/latest/system/interfaces")
                return Json(new Dictionary<string, object> {
                    ["1/1/1"] = new { link_state = "up", rx_bytes = 1000, tx_bytes = 2000, rx_errors = 0, tx_errors = 0, poe_power = 6.5 },
                    ["1/1/2"] = new { link_state = "down" } });
            if (path == "rest/latest/system/vlans" && req.Method == HttpMethod.Post)
            {
                if (RejectCreate) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                var b = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;
                Vlans[(int)b["id"]!] = (string)b["name"]!;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            if (path == "rest" && req.Method == HttpMethod.Get)
                return Json(new { latest = new { version = "v10.13", prefix = "/rest/v10.13" } });
            if (path.StartsWith("rest/latest/system/interfaces/"))
            {
                var rest = Uri.UnescapeDataString(path["rest/latest/system/interfaces/".Length..]);
                var poe = rest.EndsWith("/poe_interface");
                var port = poe ? rest[..^"/poe_interface".Length] : rest;
                if (poe)
                {
                    if (!Poe.TryGetValue(port, out var cfg)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    if (req.Method == HttpMethod.Patch)
                    {
                        Patches++;
                        if (!IgnorePatches) Poe[port] = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!["config"]!.AsObject().DeepClone().AsObject();
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    }
                    return Json(new { config = cfg });
                }
                if (!Ports.TryGetValue(port, out var st)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (req.Method == HttpMethod.Patch)
                {
                    Patches++;
                    var b = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!.AsObject();
                    if (IgnorePatches) return new HttpResponseMessage(HttpStatusCode.NoContent);
                    var mode = (string?)b["vlan_mode"] ?? st.Mode;
                    var tag = b["vlan_tag"] is { } tg ? int.Parse(((string)tg!).Split('/').Last()) : st.Tag;
                    var trunks = b["vlan_trunks"] is JsonArray arr ? arr.Select(x => int.Parse(((string)x!).Split('/').Last())).ToList() : st.Trunks;
                    Ports[port] = (mode, tag, trunks);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                return Json(new Dictionary<string, object> { ["vlan_mode"] = st.Mode, ["vlan_tag"] = Ref(st.Tag),
                    ["vlan_trunks"] = st.Trunks.ToDictionary(x => x.ToString(), Ref) });
            }
            if (path.StartsWith("rest/latest/system/vlans/"))
            {
                var id = int.Parse(path[(path.LastIndexOf('/') + 1)..]);
                if (req.Method == HttpMethod.Delete) { Vlans.Remove(id); return new HttpResponseMessage(HttpStatusCode.NoContent); }
                return Vlans.TryGetValue(id, out var n) ? Json(new { id, name = n }) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

    static readonly Device Ap = new() { Kind = DeviceKind.Ap505, Name = "SIM AP", ManagementIp = "10.250.2.71", SecretRef = "SIMAP", Firmware = "8.10.0.21" };
    static readonly Device Cx = new() { Kind = DeviceKind.Cx6100, Name = "SIM CX", ManagementIp = "10.249.1.21", SecretRef = "SIMCX", Firmware = "PL.10.13.1010" };

    static SimulatedDeviceTests()
    {
        Environment.SetEnvironmentVariable("JCF_DEVICE_SIMAP_USER", "labadmin");
        Environment.SetEnvironmentVariable("JCF_DEVICE_SIMAP_PASS", "lab-pass-1");
        Environment.SetEnvironmentVariable("JCF_DEVICE_SIMCX_USER", "cxadmin");
        Environment.SetEnvironmentVariable("JCF_DEVICE_SIMCX_PASS", "cx-pass-1");
    }
    static DeviceHttpFactory Http(HttpMessageHandler sim) => new(new SecretResolver(), (_, _) => sim);
    static ConfigProposal Proposal(ConfigAction action, string json) => new()
    { DeviceId = Ap.Id, Action = action, PayloadJson = NetworkChangePolicy.Normalize(action, json), RequestedBy = "op" };

    [Fact]
    public async Task Instant_probe_reads_inventory_and_always_logs_out()
    {
        var sim = new InstantSim();
        var result = await new InstantConnector(Http(sim), new SecretResolver()).ProbeAsync(Ap, CancellationToken.None);
        Assert.True(result.Connected);
        Assert.Null(result.Issue);
        Assert.Equal("8.10.0.21", result.Firmware);
        Assert.Equal(1, result.ApCount);
        Assert.Equal("prefer-higher-band", result.Arm["band-steering-mode"]);
        Assert.Equal(36, result.Observation?.Channel);
        Assert.Equal(40, result.Observation?.WidthMhz);
        Assert.Single(result.Clients!);
        Assert.Equal(1, sim.Logouts);
        Assert.DoesNotContain(sim.Requests, r => r.StartsWith("POST rest/arm") || r.StartsWith("POST rest/ssid")); // read-only
    }

    [Fact]
    public async Task Instant_probe_with_wrong_credentials_reports_rejection_without_reads()
    {
        var sim = new InstantSim { Pass = "something-else" };
        var result = await new InstantConnector(Http(sim), new SecretResolver()).ProbeAsync(Ap, CancellationToken.None);
        Assert.False(result.Connected);
        Assert.Contains("authentication rejected", result.Issue);
        Assert.DoesNotContain(sim.Requests, r => r.Contains("show-cmd"));
    }

    [Fact]
    public async Task Lab_gateway_accepts_Arubas_capitalised_Success_login_and_applies_with_readback()
    {
        var sim = new InstantSim();
        var outcome = await new ArubaLabApprovedChangeGateway(Http(sim), new SecretResolver())
            .ExecuteAsync(Ap, Proposal(ConfigAction.ChangeBandSteering, "{\"value\":\"balance-bands\"}"), CancellationToken.None);
        Assert.True(outcome.Success, outcome.Detail);
        Assert.Equal("balance-bands", sim.Arm["band-steering-mode"]);
        Assert.Equal(1, sim.ArmWrites);
        Assert.Equal(1, sim.Logouts);
    }

    [Fact]
    public async Task Lab_gateway_restores_original_value_when_readback_does_not_match()
    {
        var sim = new InstantSim { IgnoreArmWrites = true };
        var outcome = await new ArubaLabApprovedChangeGateway(Http(sim), new SecretResolver())
            .ExecuteAsync(Ap, Proposal(ConfigAction.ChangeMinTxPower, "{\"value\":12}"), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.True(outcome.Restored, outcome.Detail);
        Assert.False(outcome.ManualIntervention);
        Assert.Equal(2, sim.ArmWrites);                    // the write, then the verified restore
        Assert.Contains("restored and verified", outcome.Detail);
        Assert.Equal("9", sim.Arm["min-tx-power"]);
    }

    [Fact]
    public async Task Lab_gateway_treats_a_Failed_status_reply_as_rejected()
    {
        var sim = new InstantSim { ReportFailedStatus = true };
        var outcome = await new ArubaLabApprovedChangeGateway(Http(sim), new SecretResolver())
            .ExecuteAsync(Ap, Proposal(ConfigAction.ChangeBandSteering, "{\"value\":\"disable\"}"), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.True(sim.ArmWrites >= 1, "the write must actually have been attempted");
        Assert.Equal("prefer-higher-band", sim.Arm["band-steering-mode"]);
    }

    [Fact]
    public async Task Lab_gateway_still_rolls_back_when_the_request_is_cancelled_after_the_device_applied_the_write()
    {
        using var cts = new CancellationTokenSource();
        var sim = new InstantSim();
        sim.OnFirstArmWrite = cts.Cancel;
        var outcome = await new ArubaLabApprovedChangeGateway(Http(sim), new SecretResolver())
            .ExecuteAsync(Ap, Proposal(ConfigAction.ChangeBandSteering, "{\"value\":\"balance-bands\"}"), cts.Token);
        Assert.False(outcome.Success);
        Assert.True(outcome.Restored, outcome.Detail);
        Assert.False(outcome.ManualIntervention, outcome.Detail);
        Assert.Equal("prefer-higher-band", sim.Arm["band-steering-mode"]);  // compensated to the original value
        Assert.Equal(2, sim.ArmWrites);
        Assert.Equal(1, sim.Logouts);
    }

    [Fact]
    public async Task Lab_gateway_blocks_writes_to_multi_member_clusters()
    {
        var sim = new InstantSim { ApCount = 3 };
        var outcome = await new ArubaLabApprovedChangeGateway(Http(sim), new SecretResolver())
            .ExecuteAsync(Ap, Proposal(ConfigAction.ChangeBandSteering, "{\"value\":\"balance-bands\"}"), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("Multi-member", outcome.Detail);
        Assert.Equal(0, sim.ArmWrites);
    }

    [Fact]
    public async Task Cx_probe_uses_csrf_session_and_parses_ports_and_vlans()
    {
        var sim = new CxSim();
        var result = await new CxConnector(Http(sim), new SecretResolver()).ProbeAsync(Cx, CancellationToken.None);
        Assert.True(result.Connected);
        Assert.Null(result.Issue);
        Assert.Equal("PL.10.13.1010", result.Firmware);
        Assert.Equal(1, result.VlanCount);
        Assert.Equal(2, result.PortReadings!.Count);
        Assert.Equal(6.5m, result.PortReadings.Single(p => p.Name == "1/1/1").PoeWatts);
        Assert.Equal(1, sim.Logouts);
    }

    [Fact]
    public async Task Cx_gateway_creates_vlan_with_readback_and_refuses_existing_vlan()
    {
        var sim = new CxSim();
        var gateway = new ArubaLabApprovedChangeGateway(Http(sim), new SecretResolver());
        var create = new ConfigProposal { DeviceId = Cx.Id, Action = ConfigAction.CreateSwitchVlan, RequestedBy = "op",
            PayloadJson = NetworkChangePolicy.Normalize(ConfigAction.CreateSwitchVlan, "{\"vlan\":120,\"name\":\"JCF_LAB\"}") };
        var first = await gateway.ExecuteAsync(Cx, create, CancellationToken.None);
        Assert.True(first.Success, first.Detail);
        Assert.Equal("JCF_LAB", sim.Vlans[120]);
        var again = await gateway.ExecuteAsync(Cx, create, CancellationToken.None);
        Assert.False(again.Success);
        Assert.Contains("exists", again.Detail);
        Assert.Equal(2, sim.Logouts);
    }

    [Fact]
    public async Task Cx_gateway_reports_absent_vlan_when_the_switch_rejects_the_create()
    {
        var sim = new CxSim { RejectCreate = true };
        var outcome = await new ArubaLabApprovedChangeGateway(Http(sim), new SecretResolver()).ExecuteAsync(Cx,
            new ConfigProposal { DeviceId = Cx.Id, Action = ConfigAction.CreateSwitchVlan, RequestedBy = "op",
                PayloadJson = NetworkChangePolicy.Normalize(ConfigAction.CreateSwitchVlan, "{\"vlan\":121,\"name\":\"JCF_LAB2\"}") },
            CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.True(outcome.Restored, outcome.Detail);
        Assert.False(outcome.ManualIntervention);
        Assert.False(sim.Vlans.ContainsKey(121));
        Assert.Equal(1, sim.Logouts);
    }
}
