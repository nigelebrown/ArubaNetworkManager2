using Jcf.NetworkManager.Services;

namespace Jcf.NetworkManager.Workflow;

/// <summary>
/// Where approved changes may be executed on REAL equipment. Off by default. Enable with
/// <c>Jcf:DeviceWrites:Enabled=true</c> and an explicit target allowlist (<c>AllowedCidrs</c> and/or <c>AllowedIps</c>).
/// Every execution still needs two-person approval (where required), an unchanged payload hash, a fresh healthy probe,
/// unchanged firmware and a per-device/firmware WriteVerified capability. The 0.8 keys
/// <c>Jcf:LiveLabWrites:Enabled/AllowedIps</c> remain accepted.
/// Instant cluster-wide writes (a VC applies ARM/SSID to every member) need <c>AllowClusterWide=true</c>; the member
/// count recorded at proposal time must still match the live cluster at execution.
/// </summary>
public sealed record DeviceWritePolicy(bool Enabled,string[] AllowedIps,string[] AllowedCidrs,bool AllowClusterWide)
{
    public static DeviceWritePolicy From(IConfiguration c)
    {
        var enabled=c.GetValue<bool>("Jcf:DeviceWrites:Enabled") || c.GetValue<bool>("Jcf:LiveLabWrites:Enabled");
        var ips=(c.GetSection("Jcf:DeviceWrites:AllowedIps").Get<string[]>() ?? [])
            .Concat(c.GetSection("Jcf:LiveLabWrites:AllowedIps").Get<string[]>() ?? []).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct().ToArray();
        var cidrs=(c.GetSection("Jcf:DeviceWrites:AllowedCidrs").Get<string[]>() ?? []).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).ToArray();
        return new DeviceWritePolicy(enabled,ips,cidrs,c.GetValue<bool>("Jcf:DeviceWrites:AllowClusterWide"));
    }
    public bool Permits(string ip) => Enabled && (AllowedIps.Contains(ip,StringComparer.Ordinal) || AddressPolicy.IsAllowed(ip,AllowedCidrs));
}
