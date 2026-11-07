using System.Text.Json;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;

namespace Jcf.NetworkManager.Workflow;

/// <summary>
/// Fail-closed switch planning against operator-declared 24/48 copper interface count
/// and REST-observed port names. SFP/SFP+ uplink numbering varies by exact SKU and can
/// be targeted only if independently observed on the device.
/// Does not certify hardware PoE budgets, interface capabilities or rollback safety.
/// </summary>
public static class SwitchPortInventoryPolicy
{
    public static IReadOnlyList<string> Targets(ConfigAction action,string canonicalJson)
    {
        if(!SwitchConfigPolicy.IsPlanned(action))return Array.Empty<string>();
        using var json=JsonDocument.Parse(canonicalJson);
        var obj=json.RootElement;
        if(action==ConfigAction.PlanCxLag)
            return obj.GetProperty("members").EnumerateArray().Select(x=>x.GetString()!).ToArray();
        return new[]{obj.GetProperty("port").GetString()!};
    }
    public static int? DeclaredCount(DeviceCapability? evidence)
    {
        if(evidence?.Operation!=SiteConfigurationService.PortCountOperation ||
           !evidence.EvidenceReference.StartsWith("operator-declared:",StringComparison.Ordinal))
            return null;
        return int.TryParse(evidence.EvidenceReference["operator-declared:".Length..],out var count)
            && count is 24 or 48?count:null;
    }
    public static void Check(Device device,ConfigAction action,string canonicalJson,
        int? physicalAccessPorts,IReadOnlyCollection<string> observedPorts)
    {
        if(!SwitchConfigPolicy.IsPlanned(action))return;
        if(device.Kind is not(DeviceKind.Cx6100 or DeviceKind.Cx6200))
            throw new InvalidOperationException("CX switch only.");
        if(physicalAccessPorts is not(24 or 48))
            throw new InvalidOperationException("Declare the switch as 24-port or 48-port under Global → Site Configuration first.");
        foreach(var port in Targets(action,canonicalJson))
        {
            var segments=port.Split('/');
            if(segments.Length!=3 || !int.TryParse(segments[0],out var member) ||
               !int.TryParse(segments[1],out var slot) ||
               !int.TryParse(segments[2],out var number))
                throw new ArgumentException("Invalid physical interface.");
            if(member!=1 || slot!=1)
                throw new InvalidOperationException("Only explicitly supported member 1/slot 1 ports are planned in this sprint.");
            if(number<=physicalAccessPorts)continue;
            if(!observedPorts.Contains(port,StringComparer.Ordinal))
                throw new InvalidOperationException("Port "+port+" exceeds the declared "+physicalAccessPorts+
                    "-port access bank and is not in the latest REST inventory. Probe the physical uplink/SFP port first.");
        }
    }
}
