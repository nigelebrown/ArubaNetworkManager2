using System.Text.Json;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Domain;

namespace Jcf.NetworkManager.Workflow;

/// <summary>
/// Strict, bounded and non-secret AOS-CX switch configuration planning policy.
/// New actions are NOT mapped to live switch write endpoints until version-specific
/// baseline/readback/rollback/management-path impact checks are verified.
/// </summary>
public static class SwitchConfigPolicy
{
    private static readonly Regex PortPattern=new(
        @"^[1-9][0-9]?/[1-9][0-9]?/[1-9][0-9]{0,2}$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly Regex DescriptionPattern=new(
        @"^[\p{L}\p{N}_ .:/()-]{0,64}$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));

    public static bool IsPlanned(ConfigAction action) => action is
        ConfigAction.PlanCxTrunk or ConfigAction.PlanCxPortAdmin or
        ConfigAction.PlanCxLag or ConfigAction.PlanCxLldp or ConfigAction.PlanCxPoePolicy;

    public static string Normalize(ConfigAction action,string json)
    {
        if(!IsPlanned(action)) throw new NotSupportedException("Unrecognized switch planning action.");
        if(string.IsNullOrWhiteSpace(json) || json.Length>3500)
            throw new ArgumentException("Switch change payload is required (max 3500 characters).");
        JsonDocument parsed;
        try {parsed=JsonDocument.Parse(json);}
        catch(JsonException){throw new ArgumentException("Switch payload must be valid JSON.");}
        using(parsed)
        {
            var root=parsed.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Switch payload must be a JSON object.");
            void Exact(params string[] keys)
            {
                var allowed=new HashSet<string>(keys,StringComparer.Ordinal);
                var seen=new HashSet<string>(StringComparer.Ordinal);
                foreach(var prop in root.EnumerateObject())
                    if(!allowed.Contains(prop.Name) || !seen.Add(prop.Name))
                        throw new ArgumentException("Unknown or duplicate switch field: "+prop.Name);
            }
            string Text(string key)
            {
                if(!root.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.String)
                    throw new ArgumentException("Missing switch field: "+key);
                return v.GetString()?.Trim()??"";
            }
            int Number(string key,int min,int max)
            {
                if(!root.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.Number ||
                   !v.TryGetInt32(out int n)||n<min||n>max)
                    throw new ArgumentException("Invalid switch numeric field: "+key);
                return n;
            }
            bool Boolean(string key)
            {
                if(!root.TryGetProperty(key,out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Required switch boolean field: "+key);
                return v.GetBoolean();
            }
            static string Port(string raw)
            {
                if(!PortPattern.IsMatch(raw))throw new ArgumentException("Physical port must use member/slot/port such as 1/1/1.");
                return raw;
            }
            static string SafeDesc(string raw)
            {
                if(!DescriptionPattern.IsMatch(raw))throw new ArgumentException("Port description has unsupported characters or exceeds 64 characters.");
                return raw;
            }
            int[] Vlans(string key)
            {
                if(!root.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.Array)
                    throw new ArgumentException("Explicit allowed VLAN list required; 'all' is not permitted in a plan.");
                var list=new List<int>();
                foreach(var value in v.EnumerateArray())
                {
                    if(value.ValueKind!=JsonValueKind.Number || !value.TryGetInt32(out var n) || n<2 || n>4094)
                        throw new ArgumentException("Allowed VLAN IDs must be between 2 and 4094.");
                    list.Add(n);
                    if(list.Count>64)throw new ArgumentException("VLAN list exceeds 64 entries.");
                }
                if(list.Count==0 || list.Distinct().Count()!=list.Count)
                    throw new ArgumentException("VLAN list must contain unique VLAN IDs.");
                return list.Order().ToArray();
            }
            string[] Members(string key)
            {
                if(!root.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.Array)
                    throw new ArgumentException("LACP member ports must be an array.");
                var ports=new List<string>();
                foreach(var value in v.EnumerateArray())
                {
                    if(value.ValueKind!=JsonValueKind.String)
                        throw new ArgumentException("LAG members must be port strings.");
                    ports.Add(Port(value.GetString()??""));
                    if(ports.Count>8)throw new ArgumentException("At most eight member ports per LAG proposal.");
                }
                if(ports.Count<2 || ports.Distinct(StringComparer.Ordinal).Count()!=ports.Count)
                    throw new ArgumentException("LAG requires at least two different physical ports.");
                // All member ports must belong to the same stack member/slot in this conservative planning release.
                if(ports.Select(x=>x[..x.LastIndexOf('/')]).Distinct(StringComparer.Ordinal).Count()!=1)
                    throw new ArgumentException("Cross-slot/member LAG requires a separately verified topology plan.");
                return ports.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            }
            switch(action)
            {
                case ConfigAction.PlanCxTrunk:
                {
                    Exact("port","nativeVlan","allowedVlans","impactAcknowledged");
                    var port=Port(Text("port"));
                    var native=Number("nativeVlan",2,4094);
                    var allowed=Vlans("allowedVlans");
                    if(!allowed.Contains(native))throw new ArgumentException("Native VLAN must be included in explicit allowed VLAN list.");
                    if(!Boolean("impactAcknowledged"))throw new ArgumentException("Trunk/uplink impact must be acknowledged.");
                    return JsonSerializer.Serialize(new{port,nativeVlan=native,allowedVlans=allowed,impactAcknowledged=true});
                }
                case ConfigAction.PlanCxPortAdmin:
                {
                    Exact("port","state","description","impactAcknowledged");
                    var port=Port(Text("port"));
                    var state=Text("state");
                    if(state is not("up" or "down"))throw new ArgumentException("Port administrative state must be up or down.");
                    var description=SafeDesc(Text("description"));
                    var impact=Boolean("impactAcknowledged");
                    if(state=="down"&&!impact)throw new ArgumentException("Shutting a port requires explicit impact acknowledgement.");
                    return JsonSerializer.Serialize(new{port,state,description,impactAcknowledged=impact});
                }
                case ConfigAction.PlanCxLag:
                {
                    Exact("lagId","lacpMode","members","nativeVlan","allowedVlans","impactAcknowledged");
                    var id=Number("lagId",1,32);
                    var mode=Text("lacpMode");
                    if(mode is not("active" or "passive"))throw new ArgumentException("LACP mode must be active or passive.");
                    var members=Members("members");
                    var native=Number("nativeVlan",2,4094);
                    var allowed=Vlans("allowedVlans");
                    if(!allowed.Contains(native))throw new ArgumentException("LAG native VLAN must be included in allowed VLAN list.");
                    if(!Boolean("impactAcknowledged"))throw new ArgumentException("LAG creation can disrupt uplinks. Impact acknowledgement required.");
                    return JsonSerializer.Serialize(new{lagId=id,lacpMode=mode,members,nativeVlan=native,allowedVlans=allowed,impactAcknowledged=true});
                }
                case ConfigAction.PlanCxLldp:
                {
                    Exact("port","adminMode");
                    var port=Port(Text("port"));
                    var mode=Text("adminMode");
                    if(mode is not("tx-rx" or "tx-only" or "rx-only" or "disabled"))
                        throw new ArgumentException("LLDP mode must be tx-rx, tx-only, rx-only or disabled.");
                    return JsonSerializer.Serialize(new{port,adminMode=mode});
                }
                case ConfigAction.PlanCxPoePolicy:
                {
                    Exact("port","priority","powerLimitWatts");
                    var port=Port(Text("port"));
                    var priority=Text("priority");
                    if(priority is not("low" or "high" or "critical"))
                        throw new ArgumentException("PoE priority must be low, high or critical.");
                    var power=Number("powerLimitWatts",1,90);
                    return JsonSerializer.Serialize(new{port,priority,powerLimitWatts=power});
                }
                default:throw new NotSupportedException("Switch action unsupported.");
            }
        }
    }

    /// <summary>Only class and LAG platform ceilings; not a substitute for querying switch capabilities.</summary>
    public static void CheckTarget(Device device,ConfigAction action,string normalized)
    {
        if(!IsPlanned(action))return;
        if(device.Kind is not(DeviceKind.Cx6100 or DeviceKind.Cx6200))
            throw new InvalidOperationException("Switch planning requires CX 6100 or 6200.");
        if(action==ConfigAction.PlanCxLag)
        {
            using var json=JsonDocument.Parse(normalized);
            var id=json.RootElement.GetProperty("lagId").GetInt32();
            int limit=device.Kind==DeviceKind.Cx6100?8:32;
            if(id>limit)throw new ArgumentException($"The {device.Kind} supports at most {limit} LAGs in this planning policy.");
        }
    }
}
