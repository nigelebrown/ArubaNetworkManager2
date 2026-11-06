using Jcf.NetworkManager.Domain;

namespace Jcf.NetworkManager.Connectors;

/// <summary>
/// Built-in simulator for SAMPLE devices only (<see cref="Device.IsSimulated"/>). It never opens a socket and never
/// writes configuration. Values vary slowly with time so dashboards, analysis and compliance have something to show,
/// and every observation is labelled SIMULATED. Real (operator-registered) devices always use the Aruba connectors.
/// </summary>
public sealed class DemoConnector : IDeviceConnector
{
    public bool IsSimulator => true;
    public bool Supports(DeviceKind kind) => true;

    public Task<ProbeResult> ProbeAsync(Device device,CancellationToken ct)
    {
        var ap=device.IsAccessPoint;
        // Deterministic per device and 10-minute slot: repeatable demos, but not a frozen screen.
        var seed=Math.Abs(HashCode.Combine(device.Id,DateTime.UtcNow.Ticks/TimeSpan.TicksPerMinute/10));
        var rng=new Random(seed);
        if(ap)
        {
            var members=new List<DetectedMember> {
                new(device.Name.Replace(' ','-')+"-1",device.Kind==DeviceKind.Ap535?"AP-535":"AP-505","Sample site","192.0.2.xxx","SIMULATED"),
                new(device.Name.Replace(' ','-')+"-2","AP-535","Sample site","192.0.2.xxx","SIMULATED") };
            var clients=Enumerable.Range(0,rng.Next(3,9)).Select(i=>new DetectedClient(
                $"02:00:5e:{rng.Next(256):x2}:{rng.Next(256):x2}:{i:x2}","JCF-SAMPLE",i%3==0?"2.4GHz":"5GHz",-48-rng.Next(30),20+rng.Next(20),120)).ToList();
            return Task.FromResult(new ProbeResult(true,"8.10.0.21",members.Count,clients.Count,null,null,
                new Dictionary<string,string> { ["80mhz-support"]="enabled",["air-time-fairness-mode"]="default-access",
                    ["band-steering-mode"]="prefer-higher-band",["min-tx-power"]="9",["max-tx-power"]="127",["client-match"]="enabled",
                    ["wide-bands"]="5ghz",["a-channels"]="36,40,44,48,149,153,157,161" },
                new Dictionary<string,bool> { ["ChannelUtilization"]=true,["NoiseFloor"]=true,["RSSI"]=true,["SNR"]=true },
                members,null,new List<string> {"SIMULATED login","SIMULATED read","NO device contacted"},
                new RfObservation(rng.Next(2)==0?149:36,40,35+rng.Next(50),-96+rng.Next(6),2+rng.Next(8),"SIMULATED data - not live RF measurements"),
                null,clients));
        }
        var ports=Enumerable.Range(1,device.Kind==DeviceKind.Cx6200?24:12).Select(i=>new DetectedPort($"1/1/{i}",i%5==0?"down":"up",
            i%5==0?null:1_000_000L*rng.Next(1,900),i%5==0?null:1_000_000L*rng.Next(1,900),i==3?rng.Next(1,20):0,0,i<=8?Math.Round(4+(decimal)rng.NextDouble()*20,1):null)).ToList();
        return Task.FromResult(new ProbeResult(true,"PL.10.13.1010",null,null,9,ports.Count,new(),new(),new(),null,
            new List<string> {"SIMULATED login","SIMULATED read","NO device contacted"},null,ports));
    }
}
