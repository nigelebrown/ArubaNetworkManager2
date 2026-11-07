using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Domain;

namespace Jcf.NetworkManager.Workflow;

/// <summary>Cross-cutting policy enforced before any proposal is created or executed.</summary>
public static class NetworkChangePolicy
{
    public static readonly int[] ApprovedWidthsMhz = [20, 40];
    public static readonly int[] Approved24GhzChannels = [1, 6, 11];
    // Policy options are constrained further by regulatory domain, model and configured client compatibility.
    public static readonly int[] PreferredNonDfs5GhzChannels = [36, 40, 44, 48, 149, 153, 157, 161, 165];
    public static readonly int[] ExcludedDfs5GhzChannels = [52,56,60,64,100,104,108,112,116,120,124,128,132,136,140,144];
    public static bool RequiresSecondApprover(ConfigAction a) => ApProfilePolicy.ProposalOnly(a) || a is ConfigAction.ChangeBandSteering
        or ConfigAction.ChangeMinTxPower or ConfigAction.ChangeClientMatch or ConfigAction.SetChannelPolicy
        or ConfigAction.SetSwitchPortVlan or ConfigAction.SetSwitchPoe;

    public static string Normalize(ConfigAction action, string payload)
    {
        if(string.IsNullOrWhiteSpace(payload) || payload.Length>4000) throw new ArgumentException("Change payload is required (max 4000 characters).");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(payload); }
        catch(JsonException) { throw new ArgumentException("Change payload must be valid JSON."); }
        using var source = parsed;
        var root = source.RootElement;
        if(root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Change payload must be an object.");
        string Text(string key, int max) {
            if(!root.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) throw new ArgumentException("Missing field: "+key);
            var value=(field.GetString() ?? "").Trim();
            if(value.Length is 0 || value.Length>max) throw new ArgumentException("Invalid field: "+key);
            return value;
        }
        int Number(string key,int min,int max) {
            if(!root.TryGetProperty(key,out var field) || field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out int value) || value<min || value>max)
                throw new ArgumentException("Invalid numeric field: "+key);
            return value;
        }
        if(root.TryGetProperty("channelWidthMhz",out var width)) {
            if(width.ValueKind != JsonValueKind.Number || !width.TryGetInt32(out var mhz) || !ApprovedWidthsMhz.Contains(mhz))
                throw new ArgumentException("JCF permits only 20 MHz and 40 MHz; wider widths are forbidden.");
        }
        if(root.TryGetProperty("channel5Ghz",out var ch) && (ch.ValueKind != JsonValueKind.Number || !ch.TryGetInt32(out var chan) || !PreferredNonDfs5GhzChannels.Contains(chan)))
            throw new ArgumentException("Selected 5 GHz channel is not in the JCF non-DFS compatibility pool.");
        if(ApProfilePolicy.ProposalOnly(action)) return ApProfilePolicy.Normalize(action,payload);
        return action switch {
            ConfigAction.CreateSsid => SerializeSsid(Text("ssid",32),Number("vlan",2,4094),Text("pskSecretRef",40)),
            ConfigAction.ChangeBandSteering => SerializeBand(Text("value",30)),
            ConfigAction.ChangeMinTxPower => SerializePower(Number("value",6,20)),
            ConfigAction.CreateSwitchVlan => SerializeVlan(Number("vlan",2,4094),Text("name",32)),
            ConfigAction.ChangeClientMatch => SerializeClientMatch(Text("value",10)),
            ConfigAction.SetChannelPolicy => SerializeChannelPolicy(root),
            ConfigAction.SetSwitchPortVlan => SerializePortVlan(Text("port",40),Text("mode",10),Number("vlan",2,4094)),
            ConfigAction.SetSwitchPoe => SerializePoe(Text("port",40),Text("state",10)),
            _ => throw new NotSupportedException("Unsupported configuration action.")
        };
    }
    static string SerializeSsid(string ssid,int vlan,string secretRef) {
        if(!Regex.IsMatch(ssid,@"^[\w. -]{1,32}$",RegexOptions.CultureInvariant) || !Regex.IsMatch(secretRef,@"^[A-Za-z0-9_]{1,40}$"))
            throw new ArgumentException("SSID and secret reference contain invalid characters.");
        return JsonSerializer.Serialize(new {ssid,vlan,pskSecretRef=secretRef});
    }
    static string SerializeBand(string value) {
        if(!new[]{"prefer-higher-band","balance-bands","disable"}.Contains(value,StringComparer.Ordinal)) throw new ArgumentException("Unapproved band-steering mode.");
        return JsonSerializer.Serialize(new {value});
    }
    static string SerializePower(int value) => JsonSerializer.Serialize(new {value});
    static string SerializeVlan(int vlan,string name) {
        if(!Regex.IsMatch(name,@"^[A-Za-z0-9_. -]{1,32}$")) throw new ArgumentException("VLAN name contains invalid characters.");
        return JsonSerializer.Serialize(new {vlan,name});
    }
    static string SerializeClientMatch(string value) {
        if(value is not ("enabled" or "disabled")) throw new ArgumentException("ClientMatch state must be enabled or disabled.");
        return JsonSerializer.Serialize(new {value});
    }
    static string SerializeChannelPolicy(JsonElement root) {
        if(!root.TryGetProperty("channelWidthMhz",out var widthEl) ||
           !widthEl.TryGetInt32(out var width) || !ApprovedWidthsMhz.Contains(width))
            throw new ArgumentException("Only 20 and 40 MHz are permitted.");
        if(!root.TryGetProperty("channels5Ghz",out var channelsEl) || channelsEl.ValueKind!=JsonValueKind.Array)
            throw new ArgumentException("A documented 5 GHz allowed-channel array is required.");
        var channels=new List<int>();
        foreach(var ch in channelsEl.EnumerateArray()) {
            if(ch.ValueKind!=JsonValueKind.Number || !ch.TryGetInt32(out var n) ||
               !PreferredNonDfs5GhzChannels.Contains(n))
                throw new ArgumentException("5 GHz channel is not in the approved non-DFS compatibility pool.");
            if(width==40 && n==165) throw new ArgumentException("Channel 165 is 20 MHz only; it cannot be paired for 40 MHz.");
            channels.Add(n);
        }
        if(channels.Count==0||channels.Count>16) throw new ArgumentException("Approved channel list is empty or too long.");
        if(width==40) {
            // Only non-DFS 40 MHz pairs; all channels must belong to a complete 40 MHz pair.
            var approvedPairs=new[]{(36,40),(44,48),(149,153),(157,161)};
            foreach(var ch in channels) {
                if(!approvedPairs.Any(pair=>
                    (pair.Item1==ch && channels.Contains(pair.Item2)) ||
                    (pair.Item2==ch && channels.Contains(pair.Item1))))
                    throw new ArgumentException("Every 40 MHz channel must have an approved non-DFS partner in the same list.");
            }
        }
        return JsonSerializer.Serialize(new{channelWidthMhz=width,channels5Ghz=channels.Distinct().Order().ToArray()});
    }
    static string SerializePortVlan(string port,string mode,int vlan) {
        if(!Regex.IsMatch(port,@"^[A-Za-z0-9/_-]{1,40}$") || mode is not("access" or "trunk"))
            throw new ArgumentException("Invalid switch port or VLAN mode.");
        return JsonSerializer.Serialize(new {port,mode,vlan});
    }
    static string SerializePoe(string port,string state) {
        if(!Regex.IsMatch(port,@"^[A-Za-z0-9/_-]{1,40}$") || state is not("enabled" or "disabled"))
            throw new ArgumentException("Invalid port or PoE state.");
        return JsonSerializer.Serialize(new {port,state});
    }
    public static string Hash(string normalized) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    public static string Operation(ConfigAction action) => action switch {
        ConfigAction.UpsertWlanProfile=>"wlan.profile.upsert.UNVERIFIED",
        ConfigAction.SetWlanEnabled=>"wlan.profile.state.UNVERIFIED",
        ConfigAction.DeleteWlanProfile=>"wlan.profile.delete.UNVERIFIED",
        ConfigAction.SetAirtimeFairness=>"arm.airtime-fairness.UNVERIFIED",
        ConfigAction.SetRfPowerBounds=>"arm.power-bounds.UNVERIFIED",
        ConfigAction.CreateSsid=>"ssid.create", ConfigAction.ChangeBandSteering=>"arm.band-steering.write",
        ConfigAction.ChangeMinTxPower=>"arm.min-power.write", ConfigAction.CreateSwitchVlan=>"switch.vlan.create",
        ConfigAction.ChangeClientMatch=>"arm.client-match.write",ConfigAction.SetChannelPolicy=>"arm.channel-policy.write",
        ConfigAction.SetSwitchPortVlan=>"switch.port-vlan.write",ConfigAction.SetSwitchPoe=>"switch.poe.write",
        _=>"unsupported"
    };
    public static void CheckModel(Device device, ConfigAction action) {
        bool ap=device.IsAccessPoint;
        var switchAction=action is ConfigAction.CreateSwitchVlan or ConfigAction.SetSwitchPortVlan or ConfigAction.SetSwitchPoe;
        // WLAN and RF proposals are AP-only; all switch actions are segregated by target class.
        if(ap && switchAction || !ap && !switchAction)
            throw new InvalidOperationException("This action is not supported for the selected equipment class.");
    }
}
