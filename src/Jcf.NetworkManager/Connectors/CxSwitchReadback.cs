using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jcf.NetworkManager.Connectors;

/// <summary>
/// Safe, bounded, read-only AOS-CX REST interface configuration projection. The JSON collection
/// may contain additional secret or sensitive data; ONLY approved typed fields leave this parser.
/// API attribute names reflect 10.13 documentation, but real device firmware/SKU validation
/// remains required. Absent values are unknown, never presumed disabled or empty.
/// </summary>
public static class CxSwitchReadback
{
    public sealed record Interface(
        string Port,string? Mode,int? NativeOrAccessVlan,IReadOnlyList<int>? TaggedVlans,
        string? AdminState,string? LinkState,string? Description,string? InterfaceType);
    public sealed record Result(string Status,IReadOnlyList<Interface> Interfaces)
    {
        public bool IsUsable=>Status=="Parsed"&&Interfaces.Count>0;
    }

    private static readonly Regex PortName=new(
        @"^(?:1/[1-9][0-9]?/[1-9][0-9]{0,2}|lag[1-9][0-9]{0,2})$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly Regex UriVlan=new(
        @"/vlans/([0-9]{1,4})(?:/|$|\?|#)",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));

    public static Result Parse(JsonElement root)
    {
        if(root.ValueKind!=JsonValueKind.Object)return new("Unrecognized",Array.Empty<Interface>());
        var ports=new List<Interface>();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        bool duplicate=false,truncated=false;
        foreach(var prop in root.EnumerateObject())
        {
            if(!PortName.IsMatch(prop.Name))continue; // e.g., ignore unrelated interface types
            if(!seen.Add(prop.Name)){duplicate=true;break;}
            if(ports.Count>=256){truncated=true;break;}
            if(prop.Value.ValueKind!=JsonValueKind.Object)continue;
            var item=prop.Value;
            static string? Token(JsonElement obj,string key,string[] allowed)
            {
                if(!obj.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.String)return null;
                var text=v.GetString();
                return allowed.Contains(text,StringComparer.Ordinal)?text:null;
            }
            static string? Description(JsonElement obj)
            {
                if(!obj.TryGetProperty("description",out var v)||v.ValueKind!=JsonValueKind.String)return null;
                var str=v.GetString()?.Trim();
                if(string.IsNullOrWhiteSpace(str) || str.Length>64 ||
                    str.Any(c=>char.IsControl(c)||c=='<'||c=='>'||c=='&'))
                    return null;
                return str;
            }
            static int? Vlan(JsonElement el)
            {
                if(el.ValueKind==JsonValueKind.Number && el.TryGetInt32(out var n) && n is >=1 and <=4094)
                    return n;
                if(el.ValueKind!=JsonValueKind.String)return null;
                var found=UriVlan.Match(el.GetString()??"");
                if(!found.Success||!int.TryParse(found.Groups[1].Value,out var id)||id is <1 or >4094)return null;
                return id;
            }
            int? native=null;
            if(item.TryGetProperty("vlan_tag",out var tag))native=Vlan(tag);
            List<int>? tagged=null;
            if(item.TryGetProperty("vlan_trunks",out var trunks)&&trunks.ValueKind==JsonValueKind.Array)
            {
                tagged=new();
                foreach(var v in trunks.EnumerateArray())
                {
                    if(tagged.Count>=128){tagged=null;break;}
                    var id=Vlan(v);
                    if(id is null){tagged=null;break;}
                    tagged.Add(id.Value);
                }
                if(tagged is not null) tagged=tagged.Distinct().Order().ToList();
            }
            ports.Add(new Interface(prop.Name,
                Token(item,"vlan_mode",new[]{"access","trunk","native-untagged","native-tagged"}),
                native,tagged,
                Token(item,"admin_state",new[]{"up","down"}),
                Token(item,"link_state",new[]{"up","down"}),
                Description(item),
                Token(item,"type",new[]{"system","lag","vlan"})));
        }
        if(duplicate)return new("Ambiguous",Array.Empty<Interface>());
        if(truncated)return new("Truncated",Array.Empty<Interface>());
        return new(ports.Count>0?"Parsed":"NoPhysicalPorts",ports);
    }
}
