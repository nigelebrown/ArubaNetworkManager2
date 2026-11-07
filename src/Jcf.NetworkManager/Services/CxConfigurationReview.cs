using System.Text.Json;
using Jcf.NetworkManager.Connectors;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Conservative, read-only advisory comparison between an observed AOS-CX port/VLAN projection
/// and the site's desired VLAN pool. No inferred pass/fail for unknown data or missing upstream
/// VLAN membership. This is not a device compliance certification.
/// </summary>
public static class CxConfigurationReview
{
    public sealed record Finding(string Port,string Rule,string Severity,string Explanation);
    public static IReadOnlyList<Finding> Compare(string siteEffectiveJson,IReadOnlyList<CxSwitchReadback.Interface> observed)
    {
        using var doc=JsonDocument.Parse(SiteConfigurationPolicy.Normalize(siteEffectiveJson));
        if(!doc.RootElement.TryGetProperty("switch",out var section) ||
           !section.TryGetProperty("allowedVlans",out var array))
            return new[]{new Finding("All ports","SITE_VLAN_POLICY_UNKNOWN","Unverified",
                "Site/global configuration does not define the allowed VLAN pool; no port compliance assumed.")};
        var pool=array.EnumerateArray().Select(x=>x.GetInt32()).ToHashSet();
        var findings=new List<Finding>();
        foreach(var port in observed)
        {
            if(port.NativeOrAccessVlan is int p && p is >=2 and <=4094 && !pool.Contains(p))
                findings.Add(new(port.Port,"UNEXPECTED_NATIVE_OR_ACCESS_VLAN","Review",
                    "Observed native/access VLAN "+p+" is outside the site configured VLAN pool."));
            if(port.TaggedVlans is {Count:>0})
            {
                var unexpected=port.TaggedVlans.Where(x=>!pool.Contains(x)).ToArray();
                if(unexpected.Length>0)
                    findings.Add(new(port.Port,"UNEXPECTED_TAGGED_VLAN","Review",
                        "Observed tagged VLAN(s) "+string.Join(",",unexpected)+" are outside the site policy."));
            }
            if(port.Mode is null || (port.NativeOrAccessVlan is null && port.TaggedVlans is null))
                findings.Add(new(port.Port,"VLAN_MAPPING_UNKNOWN","Unverified",
                    "REST interface fields do not establish a complete VLAN mode/membership."));
        }
        return findings;
    }
}
