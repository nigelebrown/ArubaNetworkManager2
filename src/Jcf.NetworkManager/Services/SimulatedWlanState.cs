using System.Text.Json;
using Jcf.NetworkManager.Domain;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Persisted simulation state is deterministically replayed from applied, SQL-backed and audited
/// configuration proposals. No device REST activity, no new database tables, and no raw secrets.
/// This is not evidence that an actual WLAN exists on a physical AP.
/// </summary>
public static class SimulatedWlanState
{
    public sealed record Wlan(
        string Ssid,int Vlan,string SecurityMode,string Band,string Purpose,
        string CredentialReference,bool ClientIsolation,string Pmf,bool Enabled,DateTime UpdatedUtc);

    public sealed record Rf(
        string? AirtimeFairnessMode,string? PowerBand,int? MinDbm,int? MaxDbm);

    public sealed record State(IReadOnlyList<Wlan> Wlans,Rf Rf);

    public static State Replay(IEnumerable<ConfigProposal> all)
    {
        var profiles=new Dictionary<string,Wlan>(StringComparer.Ordinal);
        string? airtime=null,powerBand=null;
        int? min=null,max=null;
        foreach(var p in all.Where(x=>x.State==ConfigProposalState.Applied &&
                       ApProfileAction(x.Action))
                     .OrderBy(x=>x.FinishedUtc??x.RequestedUtc).ThenBy(x=>x.Id))
        {
            // DB records may predate current schema or be malformed; never allow corrupted sample data
            // to crash the management page or be mistaken for an operational baseline.
            try
            {
                var normalized=Jcf.NetworkManager.Workflow.ApProfilePolicy.Normalize(p.Action,p.PayloadJson);
                using var doc=JsonDocument.Parse(normalized);
                var root=doc.RootElement;
                var when=p.FinishedUtc??p.RequestedUtc;
                switch(p.Action)
                {
                    case ConfigAction.UpsertWlanProfile:
                    {
                        var mode=root.GetProperty("securityMode").GetString()!;
                        var reference=root.GetProperty(mode.EndsWith("enterprise",StringComparison.Ordinal)?
                            "radiusProfileRef":"pskSecretRef").GetString()!;
                        var ssid=root.GetProperty("ssid").GetString()!;
                        profiles[ssid]=new Wlan(ssid,root.GetProperty("vlan").GetInt32(),mode,
                            root.GetProperty("band").GetString()!,root.GetProperty("purpose").GetString()!,
                            reference,root.GetProperty("clientIsolation").GetBoolean(),
                            root.GetProperty("pmf").GetString()!,root.GetProperty("enabled").GetBoolean(),when);
                        break;
                    }
                    case ConfigAction.SetWlanEnabled:
                    {
                        var ssid=root.GetProperty("ssid").GetString()!;
                        if(profiles.TryGetValue(ssid,out var saved))
                            profiles[ssid]=saved with {Enabled=root.GetProperty("enabled").GetBoolean(),UpdatedUtc=when};
                        break;
                    }
                    case ConfigAction.DeleteWlanProfile:
                        profiles.Remove(root.GetProperty("ssid").GetString()!);
                        break;
                    case ConfigAction.SetAirtimeFairness:
                        airtime=root.GetProperty("mode").GetString();
                        break;
                    case ConfigAction.SetRfPowerBounds:
                        powerBand=root.GetProperty("band").GetString();
                        min=root.GetProperty("minDbm").GetInt32();
                        max=root.GetProperty("maxDbm").GetInt32();
                        break;
                }
            }
            catch(ArgumentException) { }
            catch(JsonException) { }
            catch(InvalidOperationException) { }
            catch(KeyNotFoundException) { }
        }
        return new State(profiles.Values.OrderBy(x=>x.Ssid,StringComparer.Ordinal).ToArray(),
            new Rf(airtime,powerBand,min,max));
    }

    private static bool ApProfileAction(ConfigAction a)=>a is
        ConfigAction.UpsertWlanProfile or ConfigAction.SetWlanEnabled or
        ConfigAction.DeleteWlanProfile or ConfigAction.SetAirtimeFairness or ConfigAction.SetRfPowerBounds;
}
