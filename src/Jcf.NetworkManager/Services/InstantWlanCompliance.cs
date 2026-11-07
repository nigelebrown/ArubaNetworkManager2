using Jcf.NetworkManager.Connectors;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Advisory-only findings from explicit readback fields. Missing commands cannot prove a setting
/// is disabled or absent. Never calls or writes to physical Aruba devices.
/// </summary>
public static class InstantWlanCompliance
{
    public sealed record Finding(string Ssid,string Rule,string Level,string Detail);

    public static IReadOnlyList<Finding> Evaluate(IReadOnlyList<InstantWlanReadback.Wlan> profiles)
    {
        var results=new List<Finding>();
        foreach(var p in profiles)
        {
            var label=p.Essid??p.Profile;
            if(p.OpMode=="opensystem")
                results.Add(new(label,"OPEN_AUTH","Review",
                    "Open system authentication reported; check approved guest/captive-portal requirements."));
            if(p.Vlan==1)
                results.Add(new(label,"DEFAULT_VLAN","Review",
                    "VLAN 1 observed; validate the approved SSID-to-VLAN mapping."));
            if(p.Purpose=="guest" && p.IntraVlanTrafficDenied==false)
                results.Add(new(label,"GUEST_ISOLATION_DISABLED","Warning",
                    "WLAN explicitly reports no intra-VLAN traffic denial; review guest isolation."));
            if(p.DynamicVlan)
                results.Add(new(label,"DYNAMIC_VLAN","Information",
                    "Attribute-based VLAN assignment is present; static VLAN alone is not the effective client VLAN."));
            if(p.Vlan is null)
                results.Add(new(label,"VLAN_UNKNOWN","Unverified",
                    "No static numeric VLAN could be verified from this CLI projection."));
            if(p.OpMode is null)
                results.Add(new(label,"SECURITY_UNKNOWN","Unverified",
                    "Authentication mode could not be established by this parser."));
        }
        return results;
    }
}
