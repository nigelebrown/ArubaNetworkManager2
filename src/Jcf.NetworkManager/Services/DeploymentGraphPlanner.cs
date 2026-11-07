using Jcf.NetworkManager.Domain;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Pure, deterministic dependency planner. Only confirmed LLDP links with a
/// manually assigned uplink/AP access-port role are accepted as ordering evidence.
/// Additive VLAN preparation works upstream->downstream; disruptive changes reverse.
/// Unconfirmed links/cycles/unknown management paths are BLOCKERS, not guessed routes.
/// </summary>
public static class DeploymentGraphPlanner
{
    public enum ChangeClass { AdditiveVlan, DisruptivePort, ManagementPath, RadioCluster }
    public sealed record Link(Guid LocalDevice,Guid PeerDevice,string LocalPort,
        string PortRole,bool Confirmed);
    public sealed record Plan(IReadOnlyList<Guid> OrderedDevices,
        IReadOnlyList<(Guid Upstream,Guid Downstream)> Dependencies,
        IReadOnlyList<string> Blockers,bool HasUnverifiedPaths);
    public static Plan Calculate(IReadOnlyList<Device> siteDevices,
        IEnumerable<Link> links,ChangeClass changeClass)
    {
        var devices=siteDevices.ToDictionary(x=>x.Id);
        var blockers=new List<string>();
        var edges=new HashSet<(Guid Upstream,Guid Downstream)>();
        var encountered=new HashSet<Guid>();
        var unverified=false;
        foreach(var link in links)
        {
            if(!devices.ContainsKey(link.LocalDevice) ||
               !devices.ContainsKey(link.PeerDevice)) continue;
            if(link.LocalDevice==link.PeerDevice)
            {
                blockers.Add("Self-referential topology link.");continue;
            }
            if(!link.Confirmed)
            {
                blockers.Add("Unconfirmed physical link at "+link.LocalPort);
                unverified=true;continue;
            }
            if(link.PortRole=="UPLINK")
            {
                edges.Add((link.PeerDevice,link.LocalDevice));
                encountered.Add(link.PeerDevice);encountered.Add(link.LocalDevice);
            }
            else if(link.PortRole=="AP" &&
                devices[link.PeerDevice].IsAccessPoint && !devices[link.LocalDevice].IsAccessPoint)
            {
                edges.Add((link.LocalDevice,link.PeerDevice));
                encountered.Add(link.PeerDevice);encountered.Add(link.LocalDevice);
            }
            else
            {
                blockers.Add("Unclassified topology direction at "+link.LocalPort+
                    "; confirm uplink or AP connection role.");
                unverified=true;
            }
        }
        if(devices.Count>1 && edges.Count==0)
        {
            blockers.Add("No verified upstream/downstream topology links for a multi-device site.");
            unverified=true;
        }
        if(devices.Count>1)
        {
            foreach(var device in devices.Values.Where(x=>!encountered.Contains(x.Id)))
            {
                blockers.Add(device.Name+" has no verified dependency path.");
                unverified=true;
            }
        }
        if(changeClass==ChangeClass.ManagementPath)
            blockers.Add("Management-path modifications require certified external recovery and separate impact approval.");
        if(changeClass==ChangeClass.RadioCluster &&
            devices.Values.Any(x=>x.IsAccessPoint&&string.IsNullOrWhiteSpace(x.ClusterName)))
            blockers.Add("Unverified Aruba Instant cluster scope.");
        var outgoing=devices.Keys.ToDictionary(k=>k,k=>new HashSet<Guid>());
        var indegree=devices.Keys.ToDictionary(k=>k,k=>0);
        foreach(var edge in edges)
        {
            if(outgoing[edge.Upstream].Add(edge.Downstream))
                indegree[edge.Downstream]++;
        }
        var queue=new SortedSet<Guid>(indegree.Where(x=>x.Value==0).Select(x=>x.Key));
        var sorted=new List<Guid>();
        while(queue.Count>0)
        {
            var n=queue.Min;queue.Remove(n);sorted.Add(n);
            foreach(var target in outgoing[n].OrderBy(x=>x))
                if(--indegree[target]==0)queue.Add(target);
        }
        if(sorted.Count!=devices.Count)
        {
            blockers.Add("Topology contains a cycle or unresolved redundant path; manual engineering review required.");
            return new Plan(Array.Empty<Guid>(),edges.OrderBy(x=>x.Upstream).ToArray(),blockers,true);
        }
        if(changeClass is ChangeClass.DisruptivePort or ChangeClass.ManagementPath)
            sorted.Reverse();
        return new Plan(sorted,edges.OrderBy(x=>x.Upstream).ThenBy(x=>x.Downstream).ToArray(),
            blockers,unverified);
    }
}