using System.Text.Json;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Domain;

namespace Jcf.NetworkManager.Workflow;

/// <summary>
/// Strict non-secret configuration proposal normalization. These are APP PLANS, not firmware-verified
/// device REST writes. Never accept passwords, shared secrets, CLI commands or unknown fields.
/// </summary>
public static class ApProfilePolicy
{
    static readonly Regex Identifier = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    static readonly Regex SsidPattern = new("^[A-Za-z0-9][A-Za-z0-9_ .-]{0,31}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));

    public static string Normalize(ConfigAction action, string json)
    {
        if(string.IsNullOrWhiteSpace(json) || json.Length>4000) throw new ArgumentException("AP profile payload invalid.");
        using var document=JsonDocument.Parse(json);
        var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object) throw new ArgumentException("AP profile payload must be an object.");
        string Text(string name,int max)
        {
            if(!root.TryGetProperty(name,out var v) || v.ValueKind!=JsonValueKind.String)
                throw new ArgumentException("Missing string field: "+name);
            var s=v.GetString()?.Trim()??"";
            if(s.Length==0 || s.Length>max) throw new ArgumentException("Invalid field: "+name);
            return s;
        }
        string Optional(string name,int max)
        {
            if(!root.TryGetProperty(name,out var v))return "";
            if(v.ValueKind!=JsonValueKind.String) throw new ArgumentException("Invalid string field: "+name);
            var s=v.GetString()?.Trim()??"";
            if(s.Length>max)throw new ArgumentException("Field too long: "+name);
            return s;
        }
        bool Flag(string name)
        {
            if(!root.TryGetProperty(name,out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("Required boolean field: "+name);
            return v.GetBoolean();
        }
        int Number(string name,int min,int max)
        {
            if(!root.TryGetProperty(name,out var v) || v.ValueKind!=JsonValueKind.Number ||
               !v.TryGetInt32(out var n) || n<min || n>max) throw new ArgumentException("Invalid numeric field: "+name);
            return n;
        }
        void Strict(params string[] allowed)
        {
            var accepted=new HashSet<string>(allowed,StringComparer.Ordinal);
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in root.EnumerateObject())
                if(!accepted.Contains(field.Name)||!seen.Add(field.Name))
                    throw new ArgumentException("Unexpected or duplicate field: "+field.Name);
        }
        string Ssid()
        {
            var s=Text("ssid",32);
            if(!SsidPattern.IsMatch(s))throw new ArgumentException("SSID contains unsupported characters.");
            return s;
        }
        string SecretRef(string field,string value)
        {
            if(!Identifier.IsMatch(value))throw new ArgumentException("Invalid secret/profile reference: "+field);
            return value;
        }

        if(action==ConfigAction.UpsertWlanProfile)
        {
            Strict("ssid","vlan","securityMode","band","purpose","pskSecretRef","radiusProfileRef","clientIsolation","pmf","enabled");
            var ssid=Ssid();
            var vlan=Number("vlan",2,4094);
            var mode=Text("securityMode",30);
            if(mode is not ("wpa2-personal" or "wpa3-personal" or "wpa2-wpa3-transition" or
                            "wpa2-enterprise" or "wpa3-enterprise"))
                throw new ArgumentException("Unapproved WLAN security mode.");
            var band=Text("band",15);
            if(band is not ("dual" or "2.4ghz" or "5ghz"))throw new ArgumentException("Invalid WLAN band.");
            var purpose=Text("purpose",15);
            if(purpose is not ("staff" or "admin" or "guest" or "voice"))
                throw new ArgumentException("Invalid WLAN purpose.");
            var isolation=Flag("clientIsolation");
            var enabled=Flag("enabled");
            var pmf=Text("pmf",15);
            if(pmf is not ("capable" or "required"))
                throw new ArgumentException("Invalid PMF setting.");
            if(mode.StartsWith("wpa3-",StringComparison.Ordinal) && pmf!="required")
                throw new ArgumentException("WPA3-only profiles require PMF.");
            if(purpose=="guest" && !isolation)
                throw new ArgumentException("Guest WLANs must enforce client isolation.");
            var psk=Optional("pskSecretRef",40);
            var radius=Optional("radiusProfileRef",40);
            if(mode.EndsWith("enterprise",StringComparison.Ordinal))
            {
                if(psk.Length>0 || radius.Length==0)
                    throw new ArgumentException("Enterprise security requires a RADIUS reference and prohibits PSK references.");
                return JsonSerializer.Serialize(new {ssid,vlan,securityMode=mode,band,purpose,radiusProfileRef=SecretRef("radiusProfileRef",radius),clientIsolation=isolation,pmf,enabled});
            }
            if(radius.Length>0 || psk.Length==0)
                throw new ArgumentException("Personal security requires a PSK secret reference and prohibits RADIUS references.");
            return JsonSerializer.Serialize(new {ssid,vlan,securityMode=mode,band,purpose,pskSecretRef=SecretRef("pskSecretRef",psk),clientIsolation=isolation,pmf,enabled});
        }
        if(action==ConfigAction.SetWlanEnabled)
        {
            Strict("ssid","enabled");
            return JsonSerializer.Serialize(new {ssid=Ssid(),enabled=Flag("enabled")});
        }
        if(action==ConfigAction.DeleteWlanProfile)
        {
            Strict("ssid","confirmSsid","impactConfirmed");
            var ssid=Ssid();
            if(Text("confirmSsid",32)!=ssid || !Flag("impactConfirmed"))
                throw new ArgumentException("WLAN deletion requires matching SSID and explicit service-impact confirmation.");
            return JsonSerializer.Serialize(new {ssid,confirmSsid=ssid,impactConfirmed=true});
        }
        if(action==ConfigAction.SetAirtimeFairness)
        {
            Strict("mode");
            var mode=Text("mode",30);
            if(mode is not ("default-access" or "fair-access" or "preferred-access"))
                throw new ArgumentException("Invalid airtime fairness mode.");
            return JsonSerializer.Serialize(new {mode});
        }
        if(action==ConfigAction.SetRfPowerBounds)
        {
            Strict("band","minDbm","maxDbm");
            var band=Text("band",15);
            if(band is not ("2.4ghz" or "5ghz"))throw new ArgumentException("Invalid RF band.");
            var min=Number("minDbm",3,20);
            var max=Number("maxDbm",6,30);
            if(min>max)throw new ArgumentException("Minimum transmit power must not exceed maximum.");
            return JsonSerializer.Serialize(new {band,minDbm=min,maxDbm=max});
        }
        throw new NotSupportedException("Unsupported AP profile proposal action.");
    }

    /// <summary>New proposals have NO certified real-hardware REST adapter. Never permit a real-device write.</summary>
    public static bool ProposalOnly(ConfigAction action) => action is
        ConfigAction.UpsertWlanProfile or ConfigAction.SetWlanEnabled or
        ConfigAction.DeleteWlanProfile or ConfigAction.SetAirtimeFairness or ConfigAction.SetRfPowerBounds;
}
