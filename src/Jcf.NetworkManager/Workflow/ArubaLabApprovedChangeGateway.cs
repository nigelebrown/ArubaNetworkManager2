using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;

namespace Jcf.NetworkManager.Workflow;

/// <summary>
/// Native REST execution adapter for REAL Aruba equipment. Reached only through <see cref="ChangeWorkflowService"/>
/// after its exact-target, independent-approval, firmware, freshness, allowlist and per-firmware capability gates.
/// No generic command execution. Every write follows: baseline read -> write -> exact readback -> on any doubt,
/// restore the baseline on a separate 90 s token and verify the restore; an unverifiable restore is reported as
/// "manual intervention" (RecoveryRequired) and raises a critical alert.
/// The request shapes for ClientMatch, channel policy, port VLAN and PoE follow the vendor REST schemas but are only
/// trusted after the per-device/firmware capability is lab-certified (WriteVerified) by an administrator.
/// </summary>
public sealed class ArubaLabApprovedChangeGateway(DeviceHttpFactory factory,SecretResolver secrets) : IApprovedChangeGateway
{
    /// <summary>ARM settings that are bare CLI flags (present = enabled).</summary>
    public static readonly HashSet<string> ArmFlags = new(StringComparer.OrdinalIgnoreCase)
        { "80mhz-support", "client-match", "scanning", "client-aware", "spectrum-load-balancing" };

    public Task<ChangeExecutionOutcome> ExecuteAsync(Device device,ConfigProposal p,CancellationToken ct) =>
        device.IsAccessPoint ? InstantAsync(device,p,ct) : CxAsync(device,p,ct);

    /// <summary>The ARM target state for an approved payload (null value = remove the setting).</summary>
    public static Dictionary<string,string?> ArmTarget(ConfigAction action,JsonElement root) => action switch
    {
        ConfigAction.ChangeBandSteering => new() { ["band-steering-mode"]=root.GetProperty("value").GetString() },
        ConfigAction.ChangeMinTxPower => new() { ["min-tx-power"]=root.GetProperty("value").GetInt32().ToString() },
        ConfigAction.ChangeClientMatch => new() { ["client-match"]=root.GetProperty("value").GetString()=="enabled"?"enabled":"disabled" },
        // 2.4 GHz stays 20 MHz; 40 MHz is permitted on 5 GHz only; 80 MHz is always off under JCF policy.
        ConfigAction.SetChannelPolicy => new() {
            ["a-channels"]=string.Join(",",root.GetProperty("channels5Ghz").EnumerateArray().Select(x=>x.GetInt32())),
            ["wide-bands"]=root.GetProperty("channelWidthMhz").GetInt32()==40?"5ghz":"none",
            ["80mhz-support"]="disabled" },
        _ => throw new NotSupportedException("Not an ARM action.")
    };

    static string? Current(IReadOnlyDictionary<string,string> arm,string key) =>
        arm.TryGetValue(key,out var v) ? v : ArmFlags.Contains(key) ? "disabled" : null;

    private async Task<ChangeExecutionOutcome> InstantAsync(Device d,ConfigProposal p,CancellationToken ct)
    {
        if(p.Action is not (ConfigAction.CreateSsid or ConfigAction.ChangeBandSteering or ConfigAction.ChangeMinTxPower
            or ConfigAction.ChangeClientMatch or ConfigAction.SetChannelPolicy))
            return new(false,true,false,"Instant action not allowlisted.");
        using var client=factory.Create(d,DevicePorts.Management(d.Kind));
        string user,password;
        try { (user,password)=await secrets.ResolveAsync(d.SecretRef,ct); }
        catch(InvalidOperationException ex) { return new(false,true,false,"No write attempted: "+ex.Message); }
        string? sid=null;
        bool attempted=false;
        string? testSsid=null;
        Dictionary<string,string?> baselineValues=new();   // ARM keys we changed -> value before the change
        // Compensation must still run if the caller's request is cancelled mid-write, so it switches to its own bounded token.
        var op=ct;
        using var compensation=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try {
            using var login=await client.PostAsync("rest/login",DeviceJson.Body(new {user,passwd=password}),ct);
            using var reply=JsonDocument.Parse(await login.Content.ReadAsStringAsync(ct));
            if(!login.IsSuccessStatusCode || !reply.RootElement.TryGetProperty("Status",out var status) || !string.Equals(status.GetString(),"success",StringComparison.OrdinalIgnoreCase) ||
                !reply.RootElement.TryGetProperty("sid",out var token)) return new(false,true,false,"Instant REST login rejected.");
            sid=token.GetString();if(string.IsNullOrWhiteSpace(sid)) return new(false,true,false,"Instant session missing.");
            // Blast radius: an Instant VC applies ARM/SSID changes to every member. The live member count must equal
            // the count the approvers saw (1 unless cluster-wide writes were enabled and approved).
            var expected=p.BaselineClusterMemberCount ?? 1;
            var members=await Command("show aps");
            var count=Regex.Match(members,@"\b(\d+)\s+Access Points?\b",RegexOptions.IgnoreCase);
            if(!count.Success || count.Groups[1].Value!=expected.ToString())
                return new(false,true,false,$"Multi-member/unknown Instant cluster: live membership does not match the approved scope of {expected} AP(s); write blocked.");
            var baseline=await Command("show running-config");
            using var data=JsonDocument.Parse(p.PayloadJson);
            var root=data.RootElement;
            if(p.Action==ConfigAction.CreateSsid) {
                testSsid=root.GetProperty("ssid").GetString()!;
                if(Profile(baseline,testSsid)) { testSsid=null; return new(false,true,false,"SSID already exists; refusing overwrite."); }
                var vlan=root.GetProperty("vlan").GetInt32();
                var secretRef=root.GetProperty("pskSecretRef").GetString()!;
                var psk=Environment.GetEnvironmentVariable("JCF_WLAN_"+secretRef+"_PASS");
                if(psk is null || psk.Length<8 || psk.Length>63)
                { testSsid=null; return new(false,true,false,"PSK secret reference unresolved or invalid. No device change."); }
                // Explicitly never put the passphrase in database or emitted logs.
                var body=new Dictionary<string,object> { ["ssid-profile"]=new Dictionary<string,object> {
                    ["action"]="create",["ssid-profile"]=testSsid,
                    ["essid"]=new {action="create",value=testSsid},["type"]="employee",["opmode"]="wpa2-psk-aes",
                    ["wpa-passphrase"]=psk,["vlan"]=new {action="create",value=vlan.ToString()},["rf-band"]="all",["enable"]="yes"
                }};
                attempted=true;
                using(var answer=await client.PostAsync("rest/ssid?sid="+Uri.EscapeDataString(sid),DeviceJson.Body(body),ct)) {
                    if(!await Accepted(answer,ct)) throw new InvalidOperationException("SSID API rejected the write; checking rollback.");
                }
                var after=await Command("show running-config");
                if(!Profile(after,testSsid) || !SsidVlan(after,testSsid,vlan)) throw new InvalidOperationException("SSID or VLAN readback did not match.");
                return new(true,true,false,"SSID created and VLAN binding verified through Instant readback.");
            }
            var baselineArm=InstantConnector.ExtractArm(baseline);
            var target=ArmTarget(p.Action,root);
            // Valued settings that the device does not report explicitly are not guessed at: no write.
            foreach(var key in target.Keys.Where(k=>!ArmFlags.Contains(k) && p.Action is ConfigAction.ChangeBandSteering or ConfigAction.ChangeMinTxPower))
                if(!baselineArm.ContainsKey(key)) return new(false,true,false,"No explicit ARM baseline for "+key+"; no write attempted.");
            var changes=target.Where(kv=>Current(baselineArm,kv.Key)!=kv.Value).ToList();
            if(changes.Count==0) return new(true,true,false,"No change required; target ARM settings already match.");
            foreach(var (key,_) in changes) baselineValues[key]=Current(baselineArm,key);
            attempted=true;
            foreach(var (key,value) in changes)
                if(!await ArmWrite(key,value)) throw new InvalidOperationException("ARM API rejected "+key+"; attempting restoration.");
            var now=InstantConnector.ExtractArm(await Command("show running-config"));
            var mismatch=changes.Where(kv=>Current(now,kv.Key)!=kv.Value).Select(kv=>kv.Key).ToList();
            if(mismatch.Count>0) throw new InvalidOperationException("ARM readback mismatch ("+string.Join(", ",mismatch)+"); attempting restoration.");
            return new(true,true,false,"ARM "+string.Join(", ",changes.Select(c=>c.Key))+" applied and exact readback verified.");
        }
        catch(Exception exc) when(exc is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) {
            if(!attempted) return new(false,true,false,"Preflight failed; no write attempted: "+DeviceErrors.Describe(exc,"Instant REST API"));
            op=compensation.Token;
            try {
                if(sid is null) return new(false,false,true,"Write outcome unknown; Instant session lost. Inspect manually.");
                if(testSsid is not null) {
                    var body=new Dictionary<string,object>{["ssid-profile"]=new Dictionary<string,string>{{"action","delete"},{"ssid-profile",testSsid}}};
                    using var deleted=await client.PostAsync("rest/ssid?sid="+Uri.EscapeDataString(sid),DeviceJson.Body(body),op);
                    if(Profile(await Command("show running-config"),testSsid)) return new(false,false,true,"SSID restoration not verified; manual cleanup required.");
                } else if(baselineValues.Count>0) {
                    foreach(var (key,value) in baselineValues)
                        if(!await ArmWrite(key,value)) return new(false,false,true,"ARM restoration of "+key+" rejected; manual recovery required.");
                    var check=InstantConnector.ExtractArm(await Command("show running-config"));
                    var notRestored=baselineValues.Where(kv=>Current(check,kv.Key)!=kv.Value).Select(kv=>kv.Key).ToList();
                    if(notRestored.Count>0) return new(false,false,true,"ARM restoration readback failed ("+string.Join(", ",notRestored)+"); manual recovery required.");
                }
                return new(false,true,false,"Write not confirmed; original setting restored and verified.");
            }catch {return new(false,false,true,"Write outcome and rollback unverified. Manual device inspection required.");}
        }
        finally {
            if(sid is not null) try {using var _=await client.PostAsync("rest/logout",DeviceJson.Body(new {sid}),CancellationToken.None);}catch{}
        }
        async Task<string> Command(string command) {
            var path="rest/show-cmd?iap_ip_addr="+Uri.EscapeDataString(d.ManagementIp)+"&cmd="+Uri.EscapeDataString(command)+"&sid="+Uri.EscapeDataString(sid!);
            using var r=await client.GetAsync(path,op);r.EnsureSuccessStatusCode();
            using var doc=JsonDocument.Parse(await r.Content.ReadAsStringAsync(op));
            if(!doc.RootElement.TryGetProperty("Status-code",out var code) || code.ToString()!="0" ||
               !doc.RootElement.TryGetProperty("Command output",out var output)) throw new InvalidOperationException("Readback unsupported.");
            return output.GetString()??"";
        }
        // Instant configuration API: {"arm":{"action":"create","<key>":{"action":"create","value":"<v>"}}}; a flag is
        // enabled with {"action":"create"} and a flag or value is removed with {"action":"delete"}.
        async Task<bool> ArmWrite(string key,string? value) {
            object setting = value is null || value=="disabled" && ArmFlags.Contains(key) ? new {action="delete"}
                : ArmFlags.Contains(key) ? new {action="create"} : new {action="create",value};
            var obj=new Dictionary<string,object>{["arm"]=new Dictionary<string,object>{["action"]="create",[key]=setting}};
            using var resp=await client.PostAsync("rest/arm?sid="+Uri.EscapeDataString(sid!),DeviceJson.Body(obj),op);
            return await Accepted(resp,op);
        }
    }
    static bool Profile(string config,string name) => Regex.IsMatch(config,
        "(?m)^\\s*wlan ssid-profile\\s+\"?"+Regex.Escape(name)+"\"?\\s*$");
    static bool SsidVlan(string config,string name,int vlan) {
        var match=Regex.Match(config,"(?m)^\\s*wlan ssid-profile\\s+\"?"+Regex.Escape(name)+"\"?\\s*$");
        if(!match.Success) return false;
        var lines=config[(match.Index+match.Length)..].Split('\n');
        foreach(var line in lines) {
            if(line.Length>0 && !char.IsWhiteSpace(line[0])) break;
            if(Regex.IsMatch(line,@"^\s*vlan\s+"+vlan+@"\s*$"))return true;
        }
        return false;
    }
    static async Task<bool> Accepted(HttpResponseMessage response,CancellationToken ct) {
        if(!response.IsSuccessStatusCode)return false;
        var text=await response.Content.ReadAsStringAsync(ct);
        if(string.IsNullOrWhiteSpace(text)) return true;
        using var doc=JsonDocument.Parse(text);
        if(doc.RootElement.TryGetProperty("Status-code",out var code) && code.ToString()!="0")return false;
        if(doc.RootElement.TryGetProperty("Status",out var status) && status.ValueKind==JsonValueKind.String &&
           (string.Equals(status.GetString(),"failed",StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.GetString(),"failure",StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    // ------------------------------------------------------------------ AOS-CX

    /// <summary>VLAN IDs referenced by an AOS-CX attribute: a URI string, a {"id":"uri"} map or an array of either.</summary>
    public static List<int> VlanIds(JsonElement? element) =>
        element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? new() :
        Regex.Matches(element.Value.GetRawText(),@"vlans/(\d+)").Select(m=>int.Parse(m.Groups[1].Value)).Distinct().Order().ToList();

    private async Task<ChangeExecutionOutcome> CxAsync(Device d,ConfigProposal proposal,CancellationToken ct) {
        if(proposal.Action is not (ConfigAction.CreateSwitchVlan or ConfigAction.SetSwitchPortVlan or ConfigAction.SetSwitchPoe))
            return new(false,true,false,"Switch action not allowlisted.");
        using var http=factory.Create(d,DevicePorts.Management(d.Kind));
        string username,password;
        try { (username,password)=await secrets.ResolveAsync(d.SecretRef,ct); }
        catch(InvalidOperationException ex) { return new(false,true,false,"No write attempted: "+ex.Message); }
        // AOS-CX 10.09+ REST session login uses query parameters, a cookie and x-csrf-token. Never log the login URI.
        var loginPath="rest/latest/login?username="+Uri.EscapeDataString(username)+"&password="+Uri.EscapeDataString(password);
        using var login=new HttpRequestMessage(HttpMethod.Post,loginPath) {Content=new ByteArrayContent(Array.Empty<byte>())};
        login.Headers.TryAddWithoutValidation("x-use-csrf-token","true");
        bool authenticated=false;
        try {
            using(var response=await http.SendAsync(login,ct)) {
                if(!response.IsSuccessStatusCode) return new(false,true,false,"AOS-CX REST session login rejected.");
                authenticated=true;
                if(response.Headers.TryGetValues("x-csrf-token",out var values))
                    http.DefaultRequestHeaders.TryAddWithoutValidation("x-csrf-token",values.FirstOrDefault());
            }
            return proposal.Action switch {
                ConfigAction.CreateSwitchVlan => await CreateVlanAsync(http,proposal,ct),
                ConfigAction.SetSwitchPortVlan => await PortVlanAsync(http,proposal,ct),
                _ => await PoeAsync(http,proposal,ct)
            };
        }
        catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) {
            return new(false,true,false,"Switch preflight failed; no write attempted: "+DeviceErrors.Describe(ex,"AOS-CX REST API"));
        } finally {
            if(authenticated) try {using var _=await http.PostAsync("rest/latest/logout",null,CancellationToken.None);}catch{}
        }
    }

    private static async Task<ChangeExecutionOutcome> CreateVlanAsync(HttpClient http,ConfigProposal proposal,CancellationToken ct)
    {
        using var obj=JsonDocument.Parse(proposal.PayloadJson);
        int vlan=obj.RootElement.GetProperty("vlan").GetInt32();
        var name=obj.RootElement.GetProperty("name").GetString()!;
        var path="rest/latest/system/vlans/"+vlan;
        using(var pre=await http.GetAsync(path,ct)) {
            if(pre.StatusCode!=HttpStatusCode.NotFound) return new(false,true,false,"Target VLAN exists or cannot be proven absent. No write attempted.");
        }
        try {
            using(var created=await http.PostAsync("rest/latest/system/vlans",DeviceJson.Body(new {id=vlan,name,type="static",admin="up"}),ct)) {
                if(!created.IsSuccessStatusCode) throw new InvalidOperationException("VLAN create rejected. Rollback safety check required.");
            }
            using var read=await http.GetAsync(path,ct);
            if(!read.IsSuccessStatusCode) throw new InvalidOperationException("VLAN write readback failed.");
            using var json=JsonDocument.Parse(await read.Content.ReadAsStringAsync(ct));
            if(!json.RootElement.TryGetProperty("name",out var observed) || observed.GetString()!=name)
                throw new InvalidOperationException("Created VLAN name mismatch. Rollback required.");
            return new(true,true,false,"VLAN created and exact name/ID verified by CX REST readback.");
        }
        catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) {
            using var compensation=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var op=compensation.Token;
            try {
                using var check=await http.GetAsync(path,op);
                if(check.StatusCode==HttpStatusCode.NotFound)return new(false,true,false,"VLAN create unconfirmed; target remains absent.");
                if(!check.IsSuccessStatusCode)return new(false,false,true,"Cannot inspect temporary VLAN; manual verification required.");
                using var actual=JsonDocument.Parse(await check.Content.ReadAsStringAsync(op));
                if(!actual.RootElement.TryGetProperty("name",out var n)||n.GetString()!=name)
                    return new(false,false,true,"VLAN ownership not verified; manual inspection required, deletion blocked.");
                using var deleted=await http.DeleteAsync(path,op);
                using var confirm=await http.GetAsync(path,op);
                return confirm.StatusCode==HttpStatusCode.NotFound
                    ?new(false,true,false,"Temporary VLAN removed and verified after failed create/readback.")
                    :new(false,false,true,"VLAN cleanup not verified. Manual recovery required.");
            } catch {return new(false,false,true,"VLAN write outcome unknown. Manual inspection required.");}
        }
    }

    /// <summary>The versioned prefix used in AOS-CX object references (e.g. "/rest/v10.13").</summary>
    private static async Task<string> RestPrefixAsync(HttpClient http,CancellationToken ct)
    {
        try {
            using var r=await http.GetAsync("rest",ct);
            if(r.IsSuccessStatusCode) {
                using var doc=JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                if(doc.RootElement.TryGetProperty("latest",out var latest) && latest.TryGetProperty("prefix",out var prefix) &&
                   prefix.GetString() is { } value && Regex.IsMatch(value,@"^/rest/v\d+\.\d+$")) return value;
            }
        } catch(Exception ex) when(ex is HttpRequestException or JsonException) { }
        return "/rest/v10.09";
    }

    static HttpRequestMessage Patch(string path,object body) => new(HttpMethod.Patch,path){Content=DeviceJson.Body(body)};

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient http,string path,CancellationToken ct)
    {
        using var r=await http.GetAsync(path,ct);
        if(r.StatusCode==HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
    }

    private static string InterfacePath(string port) => "rest/latest/system/interfaces/"+Uri.EscapeDataString(port);

    private static async Task<ChangeExecutionOutcome> PortVlanAsync(HttpClient http,ConfigProposal proposal,CancellationToken ct)
    {
        using var payload=JsonDocument.Parse(proposal.PayloadJson);
        var port=payload.RootElement.GetProperty("port").GetString()!;
        var mode=payload.RootElement.GetProperty("mode").GetString()!;
        var vlan=payload.RootElement.GetProperty("vlan").GetInt32();
        var path=InterfacePath(port);
        using(var target=await GetJsonAsync(http,"rest/latest/system/vlans/"+vlan,ct))
            if(target is null) return new(false,true,false,$"VLAN {vlan} does not exist on the switch; create it first. No write attempted.");
        var prefix=await RestPrefixAsync(http,ct);
        string Ref(int id)=>prefix+"/system/vlans/"+id;
        using var before=await GetJsonAsync(http,path+"?attributes=vlan_mode,vlan_tag,vlan_trunks",ct);
        if(before is null) return new(false,true,false,$"Interface {port} not found. No write attempted.");
        var baseMode=before.RootElement.TryGetProperty("vlan_mode",out var m) && m.ValueKind==JsonValueKind.String ? m.GetString() : null;
        var baseTag=VlanIds(before.RootElement.TryGetProperty("vlan_tag",out var t)?t:null).FirstOrDefault();
        var baseTrunks=VlanIds(before.RootElement.TryGetProperty("vlan_trunks",out var tr)?tr:null);
        var isTrunk=baseMode is "native-untagged" or "native-tagged";
        object change; object restore; Func<string?,int,List<int>,bool> applied;
        if(mode=="access") {
            // Converting a trunk to access is the classic way to cut off an AP or downstream switch uplink.
            if(isTrunk) return new(false,true,false,$"Interface {port} is a trunk ({baseMode}); converting it to access is blocked. No write attempted.");
            if(baseMode=="access" && baseTag==vlan) return new(true,true,false,"No change required; port already an access port in VLAN "+vlan+".");
            change=new Dictionary<string,object>{["vlan_mode"]="access",["vlan_tag"]=Ref(vlan)};
            restore=baseTag>0 ? new Dictionary<string,object>{["vlan_mode"]=baseMode??"access",["vlan_tag"]=Ref(baseTag)}
                              : new Dictionary<string,object>{["vlan_mode"]=baseMode??"access"};
            applied=(mo,tag,_)=>mo=="access" && tag==vlan;
        } else {
            if(!isTrunk) return new(false,true,false,$"Interface {port} is not a trunk; adding a trunk VLAN is blocked. No write attempted.");
            // An empty vlan_trunks list means "all VLANs" on AOS-CX: adding one VLAN would RESTRICT the trunk.
            if(baseTrunks.Count==0) return new(true,true,false,"No change required; trunk already permits all VLANs.");
            if(baseTrunks.Contains(vlan)) return new(true,true,false,"No change required; VLAN already permitted on the trunk.");
            change=new Dictionary<string,object>{["vlan_trunks"]=baseTrunks.Append(vlan).Order().Select(Ref).ToArray()};
            restore=new Dictionary<string,object>{["vlan_trunks"]=baseTrunks.Select(Ref).ToArray()};
            applied=(_,_,trunks)=>trunks.Contains(vlan) && baseTrunks.All(trunks.Contains);
        }
        bool attempted=false;
        try {
            attempted=true;
            using(var w=await http.SendAsync(Patch(path,change),ct))
                if(!w.IsSuccessStatusCode) throw new InvalidOperationException("Interface PATCH rejected ("+(int)w.StatusCode+").");
            if(!await Matches(ct,applied)) throw new InvalidOperationException("Port VLAN readback mismatch.");
            return new(true,true,false,$"Port {port} {mode} VLAN {vlan} applied and verified by CX REST readback.");
        }
        catch(Exception ex) when(attempted && ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) {
            using var compensation=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try {
                if(await Matches(compensation.Token,(mo,tag,tr)=>mo==baseMode && tag==baseTag && tr.SequenceEqual(baseTrunks)))
                    return new(false,true,false,"Port change not applied; original VLAN configuration verified unchanged.");
                using(var r=await http.SendAsync(Patch(path,restore),compensation.Token))
                    if(!r.IsSuccessStatusCode) return new(false,false,true,"Port VLAN restore rejected; manual recovery required.");
                return await Matches(compensation.Token,(mo,tag,tr)=>mo==baseMode && tag==baseTag && tr.SequenceEqual(baseTrunks))
                    ? new(false,true,false,"Write not confirmed; original port VLAN configuration restored and verified.")
                    : new(false,false,true,"Port VLAN restore not verified; manual recovery required.");
            } catch {return new(false,false,true,"Port VLAN write outcome unknown. Manual inspection required.");}
        }

        async Task<bool> Matches(CancellationToken token,Func<string?,int,List<int>,bool> predicate) {
            using var now=await GetJsonAsync(http,path+"?attributes=vlan_mode,vlan_tag,vlan_trunks",token) ?? throw new InvalidOperationException("Interface disappeared.");
            var mo=now.RootElement.TryGetProperty("vlan_mode",out var x) && x.ValueKind==JsonValueKind.String ? x.GetString() : null;
            var tag=VlanIds(now.RootElement.TryGetProperty("vlan_tag",out var y)?y:null).FirstOrDefault();
            return predicate(mo,tag,VlanIds(now.RootElement.TryGetProperty("vlan_trunks",out var z)?z:null));
        }
    }

    private static async Task<ChangeExecutionOutcome> PoeAsync(HttpClient http,ConfigProposal proposal,CancellationToken ct)
    {
        using var payload=JsonDocument.Parse(proposal.PayloadJson);
        var port=payload.RootElement.GetProperty("port").GetString()!;
        var disable=payload.RootElement.GetProperty("state").GetString()=="disabled";
        var path=InterfacePath(port)+"/poe_interface";
        using var before=await GetJsonAsync(http,path+"?attributes=config",ct);
        if(before is null || !before.RootElement.TryGetProperty("config",out var cfg) || cfg.ValueKind!=JsonValueKind.Object)
            return new(false,true,false,$"PoE is not available on {port}. No write attempted.");
        var baseline=JsonNode.Parse(cfg.GetRawText())!.AsObject();
        var baseDisabled=baseline["admin_disable"]?.GetValue<bool>() ?? false;
        if(baseDisabled==disable) return new(true,true,false,$"No change required; PoE already {(disable?"disabled":"enabled")} on {port}.");
        // PATCH replaces the whole "config" map, so send the baseline map with only admin_disable changed.
        var target=JsonNode.Parse(baseline.ToJsonString())!.AsObject();
        target["admin_disable"]=disable;
        try {
            using(var w=await http.SendAsync(Patch(path,new Dictionary<string,object>{["config"]=target}),ct))
                if(!w.IsSuccessStatusCode) throw new InvalidOperationException("PoE PATCH rejected ("+(int)w.StatusCode+").");
            if(await AdminDisabled(ct)!=disable) throw new InvalidOperationException("PoE readback mismatch.");
            return new(true,true,false,$"PoE {(disable?"disabled":"enabled")} on {port} and verified by CX REST readback.");
        }
        catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) {
            using var compensation=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try {
                if(await AdminDisabled(compensation.Token)==baseDisabled) return new(false,true,false,"PoE change not applied; original state verified.");
                using(var r=await http.SendAsync(Patch(path,new Dictionary<string,object>{["config"]=baseline}),compensation.Token))
                    if(!r.IsSuccessStatusCode) return new(false,false,true,"PoE restore rejected; manual recovery required.");
                return await AdminDisabled(compensation.Token)==baseDisabled
                    ? new(false,true,false,"Write not confirmed; original PoE state restored and verified.")
                    : new(false,false,true,"PoE restore not verified; manual recovery required.");
            } catch {return new(false,false,true,"PoE write outcome unknown. Manual inspection required.");}
        }

        async Task<bool> AdminDisabled(CancellationToken token) {
            using var now=await GetJsonAsync(http,path+"?attributes=config",token) ?? throw new InvalidOperationException("PoE interface disappeared.");
            return now.RootElement.GetProperty("config").TryGetProperty("admin_disable",out var a) && a.ValueKind==JsonValueKind.True;
        }
    }
}
