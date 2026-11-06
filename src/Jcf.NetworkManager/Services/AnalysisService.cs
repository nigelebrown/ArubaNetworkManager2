using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;
using Jcf.NetworkManager.Workflow;

namespace Jcf.NetworkManager.Services;

/// <summary>Explainable recommendations only. This service has no device credentials or write adapter.</summary>
public sealed class AnalysisService(IDbContextFactory<NetworkDbContext> factory)
{
    public async Task<AnalysisRun> RunAsync(string trigger,string actor,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var devices=await db.Devices.ToListAsync(ct);
        var lastSamples=await db.TelemetrySnapshots.AsNoTracking().GroupBy(s=>s.DeviceId)
            .Select(g=>g.OrderByDescending(s=>s.AtUtc).First()).ToListAsync(ct);
        var latest=lastSamples.ToDictionary(x=>x.DeviceId);
        int findings=0;
        foreach(var device in devices.Where(d=>d.IsAccessPoint && d.Enabled))
        {
            if(!latest.TryGetValue(device.Id,out var sample)) continue;
            if(DateTime.UtcNow-sample.AtUtc>TimeSpan.FromMinutes(20)) continue;
            Dictionary<string,string>? arm;
            try {arm=JsonSerializer.Deserialize<Dictionary<string,string>>(sample.ArmJson);}
            catch(JsonException) {continue;}
            if(arm is null) continue;
            if(arm.TryGetValue("80mhz-support",out var width80) && width80.Equals("enabled",StringComparison.OrdinalIgnoreCase))
            {
                // Proposed action only; never changes the AP's operating width.
                await Propose(device,"RF_WIDTH_80_ENABLED","80 MHz policy drift",
                    "The current ARM configuration reports 80 MHz enabled; the JCF policy permits 20/40 MHz only.",
                    "Review a proposed 20/40 MHz-only RF policy for the entire Instant cluster after preflight.","High");
                findings++;
            }
            if(sample.ChannelWidthMhz is > 40) {
                await Propose(device,"OBSERVED_UNAPPROVED_WIDTH","Detected radio width exceeds JCF standard",
                    $"Observed {sample.ChannelWidthMhz} MHz from {sample.ObservationSource}; only 20 and 40 MHz are permitted.",
                    "Validate firmware-specific RF telemetry and propose an approved 20/40-only configuration change after lab certification.","High");
                findings++;
            }
            if(sample.RadioChannel is int ch && NetworkChangePolicy.ExcludedDfs5GhzChannels.Contains(ch)) {
                await Propose(device,"DFS_CHANNEL_DETECTED","5 GHz compatibility channel review",
                    $"Observed excluded DFS channel {ch} from {sample.ObservationSource}.",
                    "Investigate permitted non-DFS alternatives and regulatory/client constraints; submit a scoped approval request.","Medium");
                findings++;
            }
            // Sustained utilization is evidence, not permission for a radio change. A single spike is ignored.
            var recent=await db.TelemetrySnapshots.AsNoTracking().Where(x=>x.DeviceId==device.Id && x.AtUtc>=DateTime.UtcNow.AddMinutes(-20))
                .OrderByDescending(x=>x.AtUtc).Take(4).ToListAsync(ct);
            if(recent.Count>=3 && recent.Take(3).All(x=>x.ChannelUtilizationPct is >= 80 and <= 100) &&
               recent.Take(3).All(x=>x.ObservationSource.Contains("labeled values",StringComparison.OrdinalIgnoreCase))) {
                await Propose(device,"HIGH_UTILIZATION_PERSISTENT","Sustained RF congestion requiring review",
                    "Three consecutive RF telemetry samples within 20 minutes indicate channel utilization >=80%. Readbacks are heuristic until firmware parsing is certified.",
                    "Review airtime demand, retries, wired uplinks and native ARM behavior; propose a bounded policy adjustment only after approval.","Medium");
                findings++;
            }
            if (sample.ClientCount is null)
            {
                await Propose(device,"CLIENT_COUNT_UNKNOWN","Client experience telemetry incomplete",
                    "The API did not establish a reliable connected-client count; no zero-client assumption is made.",
                    "Improve validated client metric collection (read-only); do not change RF settings.","Low");
                findings++;
            }
        }
        var run=new AnalysisRun{Trigger=trigger,InitiatedBy=actor,DevicesEvaluated=devices.Count,Findings=findings};
        db.AnalysisRuns.Add(run);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Analyze Now",Target=run.Id.ToString(),Outcome=$"{findings} findings; zero device writes"});
        await db.SaveChangesAsync(ct);
        return run;

        async Task Propose(Device device,string key,string title,string evidence,string suggested,string risk)
        {
            var fingerprint=$"{device.Id:N}:{key}";
            if (await db.Recommendations.AnyAsync(r=>r.Fingerprint==fingerprint && r.State==RecommendationState.Pending,ct)) return;
            db.Recommendations.Add(new Recommendation {DeviceId=device.Id,Fingerprint=fingerprint,Title=title,Evidence=evidence,
                ProposedSetting=suggested,Risk=risk,CreatedBy=actor});
        }
    }

    public async Task<Recommendation> DecideAsync(Guid recommendationId,string decision,string actor,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var rec=await db.Recommendations.SingleOrDefaultAsync(x=>x.Id==recommendationId,ct)
            ?? throw new KeyNotFoundException("Recommendation not found.");
        if(rec.State!=RecommendationState.Pending) throw new InvalidOperationException("Only pending recommendations may be decided.");
        if(actor.Equals(rec.CreatedBy,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The recommendation creator cannot approve or reject their own request.");
        rec.State=decision.ToLowerInvariant() switch {
            "approve"=>RecommendationState.Approved,
            "reject"=>RecommendationState.Rejected,
            "defer"=>RecommendationState.Deferred,
            _=>throw new ArgumentException("Decision must be approve, reject or defer.")};
        rec.ReviewedBy=actor;rec.ReviewedUtc=DateTime.UtcNow;
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Recommendation decision",Target=rec.Id.ToString(),Outcome=rec.State.ToString()+"; NO deployment"});
        await db.SaveChangesAsync(ct);
        return rec;
    }
}
