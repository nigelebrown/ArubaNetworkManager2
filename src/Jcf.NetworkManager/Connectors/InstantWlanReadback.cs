using System.Text.RegularExpressions;

namespace Jcf.NetworkManager.Connectors;

/// <summary>
/// Firmware-UNCERTIFIED, read-only, strictly allowlisted projection of Aruba Instant AOS-8
/// "show running-config" SSID blocks. Never store the raw config (may contain WPA/RADIUS secrets).
/// This parser is not evidence of a successful device write or an authoritative WLAN inventory.
/// </summary>
public static class InstantWlanReadback
{
    public sealed record Wlan(string Profile,string? Essid,int? Vlan,string? OpMode,
        string? Band,string? Purpose,bool? Enabled,bool? IntraVlanTrafficDenied,bool DynamicVlan);

    public sealed record Result(string Status,IReadOnlyList<Wlan> Profiles)
    {
        public bool IsUsable => Status=="Parsed" && Profiles.Count>0;
    }

    private const int MaxInputChars=512_000;
    private const int MaxProfiles=40;
    private static readonly Regex SafeLabel=new(@"^[\p{L}\p{N}_ .-]{1,32}$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly Regex SafeMode=new(@"^[A-Za-z0-9_.-]{1,40}$",
        RegexOptions.CultureInvariant|RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));

    public static Result Parse(string? config)
    {
        if(string.IsNullOrWhiteSpace(config))return new("Unavailable",Array.Empty<Wlan>());
        if(config.Length>MaxInputChars)return new("Oversize",Array.Empty<Wlan>());

        var output=new List<Wlan>();
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool invalid=false,tooMany=false;
        string? name=null,ssid=null,opmode=null,band=null,purpose=null;
        int? vlan=null;
        bool? enabled=null,blocked=null;
        bool dynamicVlan=false;

        void Flush()
        {
            if(name is null)return;
            if(!seen.Add(name))invalid=true;
            if(output.Count>=MaxProfiles)tooMany=true;
            else output.Add(new Wlan(name,ssid,vlan,opmode,band,purpose,enabled,blocked,dynamicVlan));
            name=null;ssid=null;opmode=null;band=null;purpose=null;
            vlan=null;enabled=null;blocked=null;dynamicVlan=false;
        }
        foreach(var raw in config.Split('\n'))
        {
            var line=raw.Trim();
            if(line.Length==0 || line.StartsWith('!') || line.StartsWith('#'))continue;
            if(line.StartsWith("wlan ssid-profile ",StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                var value=line["wlan ssid-profile ".Length..].Trim();
                if(value.Length>=2 && value[0]=='"' && value[^1]=='"')value=value[1..^1];
                if(SafeLabel.IsMatch(value))name=value;
                else invalid=true;
                continue;
            }
            if(name is null)continue;
            // End on known top-level CLI sections; unknown lines inside an SSID block are ignored,
            // not persisted. Some firmware prints sections without indentation.
            if(line.Equals("end",StringComparison.OrdinalIgnoreCase)||
               line.Equals("exit",StringComparison.OrdinalIgnoreCase)||
               line.StartsWith("wlan ",StringComparison.OrdinalIgnoreCase)||
               line.StartsWith("ip ",StringComparison.OrdinalIgnoreCase)||
               line.StartsWith("interface ",StringComparison.OrdinalIgnoreCase)||
               line.Equals("arm",StringComparison.OrdinalIgnoreCase)||
               line.StartsWith("ap-name ",StringComparison.OrdinalIgnoreCase))
            {
                Flush();continue;
            }

            static string? Label(string x) => x.Length>=2 && x[0]=='"' && x[^1]=='"' ? x[1..^1] : x;
            if(line.StartsWith("essid ",StringComparison.OrdinalIgnoreCase))
            {
                var candidate=Label(line[6..].Trim());
                if(candidate is not null && SafeLabel.IsMatch(candidate))ssid=candidate;
                continue;
            }
            if(line.StartsWith("vlan ",StringComparison.OrdinalIgnoreCase))
            {
                if(int.TryParse(line[5..].Trim(),out var id) && id is >=1 and <=4094)vlan=id;
                continue;
            }
            if(line.StartsWith("set-vlan ",StringComparison.OrdinalIgnoreCase))
            {
                dynamicVlan=true;
                continue;
            }
            if(line.StartsWith("opmode ",StringComparison.OrdinalIgnoreCase))
            {
                var token=line[7..].Trim();
                if(SafeMode.IsMatch(token))opmode=token.ToLowerInvariant();
                continue;
            }
            if(line.StartsWith("rf-band ",StringComparison.OrdinalIgnoreCase))
            {
                var token=line[8..].Trim();
                if(token is "all" or "2.4" or "5.0" or "none")band=token;
                continue;
            }
            if(line.StartsWith("type ",StringComparison.OrdinalIgnoreCase))
            {
                var token=line[5..].Trim();
                if(token is "guest" or "employee" or "voice" or "personal")purpose=token;
                continue;
            }
            if(line.Equals("enable",StringComparison.OrdinalIgnoreCase))enabled=true;
            else if(line.Equals("disable",StringComparison.OrdinalIgnoreCase)||
                    line.Equals("no enable",StringComparison.OrdinalIgnoreCase))enabled=false;
            else if(line.Equals("deny-intra-vlan-traffic",StringComparison.OrdinalIgnoreCase))blocked=true;
            else if(line.Equals("no deny-intra-vlan-traffic",StringComparison.OrdinalIgnoreCase))blocked=false;
        }
        Flush();
        if(invalid)return new("Ambiguous",Array.Empty<Wlan>());
        if(tooMany)return new("Truncated",Array.Empty<Wlan>());
        return new(output.Count>0?"Parsed":"NoRecognizedProfiles",output);
    }
}
