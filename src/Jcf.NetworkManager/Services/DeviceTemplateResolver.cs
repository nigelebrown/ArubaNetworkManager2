using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Pure deterministic resolver: catalog/global G -> site S -> device D -> independently
/// approved optimization O. Desired config ONLY, never an API write.
/// Array values are atomic settings; conflicting functional profiles are rejected.
/// Mandatory global values and JCF RF limits cannot be weakened at any lower level.
/// </summary>
public static class DeviceTemplateResolver
{
    public sealed record Layer(string NonSecretJson,string Source,int Version,string Author,
        string Reason,string? ApprovalId,DateTime ModifiedUtc);
    public sealed record Setting(string Path,string ValueJson,string Source,int Version,
        string Author,string Reason,string? ApprovalId,DateTime ModifiedUtc);
    public sealed record Result(string DesiredJson,IReadOnlyList<Setting> Settings,
        IReadOnlyList<string> OverriddenPaths);
    public static Result Resolve(Layer global,Layer? site=null,Layer? device=null,
        Layer? optimization=null,IEnumerable<Layer>? functionalProfiles=null,
        IReadOnlyCollection<string>? mandatoryPaths=null)
    {
        if(global.Source!="G")throw new ArgumentException("Global layer must be G.");
        var baseline=Parse(global.NonSecretJson);
        var result=Parse(global.NonSecretJson);
        var origin=new Dictionary<string,Setting>(StringComparer.Ordinal);
        Apply(global,result,origin);
        // A functional profile cannot override another profile's conflicting value;
        // there is no silent last-one-wins when functions are combined.
        var occupied=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var profile in functionalProfiles??Enumerable.Empty<Layer>())
        {
            if(profile.Source!="F")throw new ArgumentException("Functional profile layer must use F.");
            foreach(var (key,value) in Fields(Parse(profile.NonSecretJson)))
            {
                var serialized=value.ToJsonString();
                if(occupied.TryGetValue(key,out var previous)&&previous!=serialized)
                    throw new InvalidOperationException("Conflicting functional profiles for "+key);
                occupied[key]=serialized;
            }
            Apply(profile,result,origin);
        }
        foreach(var layer in new[]{site,device,optimization})
        {
            if(layer is null)continue;
            if(layer.Source is not("S" or "D" or "O"))throw new ArgumentException("Unexpected override source");
            if(layer.Source=="O"&&string.IsNullOrWhiteSpace(layer.ApprovalId))
                throw new InvalidOperationException("Optimization overrides require an approved recommendation reference.");
            Apply(layer,result,origin);
        }
        var must=(mandatoryPaths??Array.Empty<string>()).Distinct(StringComparer.Ordinal);
        var actual=Fields(result).ToDictionary(x=>x.Key,x=>x.Value.ToJsonString(),StringComparer.Ordinal);
        var required=Fields(baseline).ToDictionary(x=>x.Key,x=>x.Value.ToJsonString(),StringComparer.Ordinal);
        foreach(var key in must)
        {
            if(!required.TryGetValue(key,out var original))
                throw new InvalidOperationException("Mandatory template path is missing: "+key);
            if(!actual.TryGetValue(key,out var effective)||effective!=original)
                throw new InvalidOperationException("Mandatory global configuration cannot be overridden: "+key);
        }
        // Cross-field validation catches overrides that produce invalid VLAN pools,
        // 80/160 MHz, DFS channels or unsafe SSID security.
        var normalized=SiteConfigurationPolicy.Normalize(result.ToJsonString());
        var settings=origin.Values.OrderBy(x=>x.Path,StringComparer.Ordinal).ToArray();
        return new Result(normalized,settings,
            settings.Where(x=>x.Source!="G").Select(x=>x.Path).ToArray());
    }
    private static JsonObject Parse(string json)=>JsonNode.Parse(
        SiteConfigurationPolicy.Normalize(json))!.AsObject();
    private static IEnumerable<KeyValuePair<string,JsonNode>> Fields(JsonObject root)
    {
        foreach(var section in root)
        {
            if(section.Value is not JsonObject group)continue;
            foreach(var field in group)
                if(field.Value is JsonNode value)
                    yield return new(section.Key+"."+field.Key,value);
        }
    }
    private static void Apply(Layer layer,JsonObject target,Dictionary<string,Setting> origin)
    {
        var patch=Parse(layer.NonSecretJson);
        foreach(var (key,value) in Fields(patch))
        {
            var split=key.Split('.',2);
            if(target[split[0]] is not JsonObject group)
            {
                group=new JsonObject();
                target[split[0]]=group;
            }
            group[split[1]]=value.DeepClone();
            origin[key]=new Setting(key,value.ToJsonString(),layer.Source,layer.Version,
                layer.Author,layer.Reason,layer.ApprovalId,layer.ModifiedUtc);
        }
    }
}
