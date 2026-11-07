using System.Text.Json;
using System.Text.Json.Nodes;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Workflow;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Non-secret, allowlisted global -> site policy inheritance. This is DESIRED configuration,
/// not a device write. Site overrides merge per leaf key; arrays (WLANs, VLAN pools) replace
/// their corresponding global lists and are never silently concatenated.
/// </summary>
public static class SiteConfigurationPolicy
{
    public const int MaxDocumentLength=6500;
    private static readonly HashSet<string> SwitchKeys = new(StringComparer.Ordinal) {
        "dataVlan","voiceVlan","managementVlan","nativeVlan","allowedVlans",
        "lldpMode","poeDefault","poePriority"
    };
    private static readonly HashSet<string> WirelessKeys = new(StringComparer.Ordinal) {
        "width24GhzMhz","width5GhzMhz","allowed5GhzChannels","dfsEnabled",
        "clientMatch","bandSteering","airtimeFairness","minTxPowerDbm","maxTxPowerDbm",
        "wlanProfiles"
    };
    public static string Normalize(string text)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Length>MaxDocumentLength)
            throw new ArgumentException("Policy JSON must be non-empty and at most 6500 characters.");
        JsonDocument parsed;
        try {parsed=JsonDocument.Parse(text);}
        catch(JsonException) {throw new ArgumentException("Invalid configuration policy JSON.");}
        using(parsed)
        {
            var root=parsed.RootElement;
            if(root.ValueKind!=JsonValueKind.Object) throw new ArgumentException("Policy root must be an object.");
            var normalized=new JsonObject();
            DuplicateCheck(root,"policy root");
            foreach(var section in root.EnumerateObject())
            {
                if(section.Name is not ("switch" or "wireless"))
                    throw new ArgumentException("Only switch and wireless policy sections are permitted.");
                if(section.Value.ValueKind!=JsonValueKind.Object)
                    throw new ArgumentException("Each policy section must be an object.");
                var result=new JsonObject();
                DuplicateCheck(section.Value,section.Name);
                var permitted=section.Name=="switch"?SwitchKeys:WirelessKeys;
                foreach(var p in section.Value.EnumerateObject())
                {
                    if(!permitted.Contains(p.Name))throw new ArgumentException("Unknown policy key: "+section.Name+"."+p.Name);
                    result[p.Name]=section.Name=="switch"?SwitchValue(p.Name,p.Value):WirelessValue(p.Name,p.Value);
                }
                normalized[section.Name]=result;
            }
            if(normalized.Count==0)throw new ArgumentException("Policy cannot be empty.");
            VerifyEffective(normalized);
            var final=normalized.ToJsonString();
            if(final.Length>MaxDocumentLength)throw new ArgumentException("Normalized policy is too large.");
            return final;
        }
    }
    static void DuplicateCheck(JsonElement obj,string path)
    {
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var field in obj.EnumerateObject())
            if(!keys.Add(field.Name))throw new ArgumentException("Duplicate policy field: "+path+"."+field.Name);
    }
    static int Int(JsonElement val,string name,int min,int max)
    {
        if(val.ValueKind!=JsonValueKind.Number || !val.TryGetInt32(out var n)||n<min||n>max)
            throw new ArgumentException(name+" must be between "+min+" and "+max+".");
        return n;
    }
    static string Str(JsonElement val,string name,params string[] permitted)
    {
        if(val.ValueKind!=JsonValueKind.String || !permitted.Contains(val.GetString(),StringComparer.Ordinal))
            throw new ArgumentException("Unapproved value: "+name);
        return val.GetString()!;
    }
    static bool Bool(JsonElement val,string name)
    {
        if(val.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException(name+" must be a boolean.");
        return val.GetBoolean();
    }
    static JsonArray Vlans(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Array)throw new ArgumentException("VLAN pool must be an array.");
        var ids=new List<int>();
        foreach(var v in value.EnumerateArray())
        {
            ids.Add(Int(v,"VLAN",2,4094));
            if(ids.Count>64)throw new ArgumentException("VLAN pool limited to 64 IDs.");
        }
        if(ids.Count==0 || ids.Distinct().Count()!=ids.Count)
            throw new ArgumentException("VLAN pool requires nonempty unique IDs.");
        var result=new JsonArray();
        foreach(var id in ids.Order())result.Add(id);
        return result;
    }
    static JsonNode SwitchValue(string key,JsonElement value)=>key switch
    {
        "dataVlan" or "voiceVlan" or "managementVlan" or "nativeVlan" => JsonValue.Create(Int(value,key,2,4094))!,
        "allowedVlans"=>Vlans(value),
        "lldpMode"=>JsonValue.Create(Str(value,key,"tx-rx","tx-only","rx-only","disabled"))!,
        "poeDefault"=>JsonValue.Create(Str(value,key,"enabled","disabled","inherit-device"))!,
        "poePriority"=>JsonValue.Create(Str(value,key,"low","high","critical"))!,
        _=>throw new ArgumentException("Unknown switch policy key.")
    };
    static JsonNode WirelessValue(string key,JsonElement value)
    {
        if(key=="width24GhzMhz")
        {
            if(Int(value,key,20,20)!=20)throw new ArgumentException("2.4 GHz must use 20 MHz.");
            return JsonValue.Create(20)!;
        }
        if(key=="width5GhzMhz")
        {
            var n=Int(value,key,20,40);
            if(n is not(20 or 40))throw new ArgumentException("JCF permits 20 or 40 MHz only.");
            return JsonValue.Create(n)!;
        }
        if(key=="allowed5GhzChannels")
        {
            if(value.ValueKind!=JsonValueKind.Array)throw new ArgumentException("Channels must be an array.");
            var values=new List<int>();
            foreach(var channel in value.EnumerateArray())
            {
                var n=Int(channel,"5 GHz channel",1,165);
                if(!NetworkChangePolicy.PreferredNonDfs5GhzChannels.Contains(n))
                    throw new ArgumentException("DFS or unapproved 5 GHz channel "+n);
                values.Add(n);
            }
            if(values.Count==0 || values.Count>16 || values.Distinct().Count()!=values.Count)
                throw new ArgumentException("Channel set must contain 1-16 unique channels.");
            var ret=new JsonArray();foreach(var n in values.Order())ret.Add(n);
            return ret;
        }
        if(key=="dfsEnabled")
        {
            if(Bool(value,key))throw new ArgumentException("JCF disallows DFS in this compatibility policy.");
            return JsonValue.Create(false)!;
        }
        if(key=="wlanProfiles")
        {
            if(value.ValueKind!=JsonValueKind.Array)throw new ArgumentException("WLAN profiles must be an array.");
            var profiles=new JsonArray();var ssids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in value.EnumerateArray())
            {
                var checkedJson=ApProfilePolicy.Normalize(ConfigAction.UpsertWlanProfile,p.GetRawText());
                var node=JsonNode.Parse(checkedJson)!;
                var name=node["ssid"]!.GetValue<string>();
                if(!ssids.Add(name))throw new ArgumentException("Duplicate SSID in policy: "+name);
                profiles.Add(node);
                if(profiles.Count>16)throw new ArgumentException("Maximum 16 WLAN profiles per policy.");
            }
            return profiles;
        }
        if(key is "minTxPowerDbm" or "maxTxPowerDbm")
            return JsonValue.Create(Int(value,key,3,30))!;
        if(key=="clientMatch")
            return JsonValue.Create(Str(value,key,"enabled","disabled"))!;
        if(key=="bandSteering")
            return JsonValue.Create(Str(value,key,"prefer-higher-band","balance-bands","disable"))!;
        if(key=="airtimeFairness")
            return JsonValue.Create(Str(value,key,"default-access","fair-access","preferred-access"))!;
        throw new ArgumentException("Unknown wireless policy key.");
    }
    static void VerifyEffective(JsonObject policy)
    {
        if(policy["switch"] is JsonObject sw && sw["allowedVlans"] is JsonArray vlans)
        {
            var set=vlans.Select(v=>v!.GetValue<int>()).ToHashSet();
            foreach(var key in new[]{"dataVlan","voiceVlan","managementVlan","nativeVlan"})
                if(sw[key] is JsonNode n && !set.Contains(n.GetValue<int>()))
                    throw new ArgumentException(key+" must be included in allowedVlans.");
        }
        if(policy["wireless"] is JsonObject wlan)
        {
            if(wlan["minTxPowerDbm"] is JsonNode min && wlan["maxTxPowerDbm"] is JsonNode max &&
               min.GetValue<int>()>max.GetValue<int>())
                throw new ArgumentException("Minimum RF power exceeds maximum.");
            if(wlan["width5GhzMhz"]?.GetValue<int>()==40 &&
               wlan["allowed5GhzChannels"] is JsonArray chs)
            {
                var channels=chs.Select(x=>x!.GetValue<int>()).ToHashSet();
                foreach(var ch in channels)
                    if(!new[]{(36,40),(44,48),(149,153),(157,161)}
                        .Any(pair=>(pair.Item1==ch&&channels.Contains(pair.Item2))||
                                   (pair.Item2==ch&&channels.Contains(pair.Item1))))
                        throw new ArgumentException("40 MHz requires complete approved non-DFS channel pairs.");
            }
        }
    }

    public static string Resolve(string globalJson,string overridesJson)
    {
        var global=JsonNode.Parse(Normalize(globalJson))!.AsObject();
        var overrides=JsonNode.Parse(Normalize(overridesJson))!.AsObject();
        foreach(var section in overrides)
        {
            if(section.Value is not JsonObject values)continue;
            if(global[section.Key] is not JsonObject target)
            {
                global[section.Key]=new JsonObject();
                target=global[section.Key]!.AsObject();
            }
            foreach(var item in values)
                target[item.Key]=item.Value?.DeepClone();
        }
        return Normalize(global.ToJsonString());
    }

    public static string EmptyOverride => """{"switch":{}}""";
}
