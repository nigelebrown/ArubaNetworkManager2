using System.Text.Json;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Transparent G/S/D/O/I/!/P provenance and variance. Distinguishes verified actual device
/// evidence from imported configuration or unvalidated parser output; never declares
/// live compliance from a missing/uncertified readback.
/// </summary>
public sealed class ConfigurationVarianceService(IDbContextFactory<NetworkDbContext> factory,
    TemplateCatalogService templates)
{
    public sealed record Row(Guid DeviceId,string Device,string Model,string? Site,
        string Setting,string Global,string SiteValue,string DeviceValue,string Optimization,
        string Effective,string Actual,string Source,string Indicator,string Quality,
        string EvidenceUtc);
    public sealed record Report(Guid? SiteId,DateTime GeneratedUtc,IReadOnlyList<Row> Rows,
        int Devices,int DriftFindings,int PendingChanges,int UnknownMeasurements);
    static readonly string Unknown="Unknown";
    public static Dictionary<string,string> Flatten(string json)
    {
        var rows=new Dictionary<string,string>(StringComparer.Ordinal);
        using var doc=JsonDocument.Parse(json);
        if(doc.RootElement.ValueKind!=JsonValueKind.Object)return rows;
        foreach(var section in doc.RootElement.EnumerateObject())
        {
            if(section.Value.ValueKind!=JsonValueKind.Object)continue;
            foreach(var entry in section.Value.EnumerateObject())
                rows[section.Name+"."+entry.Name]=entry.Value.GetRawText();
        }
        return rows;
    }
    public static IReadOnlyList<Row> Compare(Guid deviceId,string name,string model,string? site,
        IReadOnlyDictionary<string,string> global,IReadOnlyDictionary<string,string> siteValues,
        IReadOnlyDictionary<string,string> deviceValues,IReadOnlyDictionary<string,string> optimization,
        IReadOnlyDictionary<string,string> desired,IReadOnlyDictionary<string,string> observed,
        bool observedIsCertified,DateTime? observedUtc,bool pending)
    {
        var keys=global.Keys.Union(siteValues.Keys).Union(deviceValues.Keys)
            .Union(optimization.Keys).Union(desired.Keys).Union(observed.Keys)
            .Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        var result=new List<Row>();
        foreach(var key in keys)
        {
            var effective=desired.GetValueOrDefault(key)??Unknown;
            var actual=observed.GetValueOrDefault(key)??Unknown;
            var source=key.StartsWith("imported.",StringComparison.Ordinal)?"I":
                optimization.ContainsKey(key)?"O":deviceValues.ContainsKey(key)?"D":
                siteValues.ContainsKey(key)?"S":"G";
            var drift=observedIsCertified&&observed.ContainsKey(key)&&
                desired.ContainsKey(key)&&effective!=actual;
            var indicator=drift?"!":pending?"P":source;
            var quality=drift?"Verified drift":observed.ContainsKey(key)?
                observedIsCertified?"Verified observation":"Uncertified observation":
                "Not observed";
            result.Add(new Row(deviceId,name,model,site,key,
                global.GetValueOrDefault(key)??"Inherited / n.a.",
                siteValues.GetValueOrDefault(key)??"Inherited",
                deviceValues.GetValueOrDefault(key)??"Inherited",
                optimization.GetValueOrDefault(key)??"Inherited",
                effective,actual,source,indicator,quality,
                observedUtc?.ToString("yyyy-MM-dd HH:mm UTC")??"Never"));
        }
        return result;
    }
    public async Task<Report> BuildAsync(Guid? siteId=null,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var devices=await db.Devices.AsNoTracking().Where(x=>siteId==null||x.LocationId==siteId)
            .OrderBy(x=>x.Name).ToListAsync(ct);
        var all=new List<Row>();
        foreach(var device in devices)
        {
            TemplateCatalogService.DeviceEffective? info;
            try{info=await templates.ResolveDeviceAsync(device.Id,ct);}
            catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                all.Add(new Row(device.Id,device.Name,device.Kind.ToString(),null,
                    "template-assignment",Unknown,Unknown,Unknown,Unknown,
                    "Unavailable",Unknown,"G","P","Template missing or conflicted","Never"));
                continue;
            }
            var baseValues=Flatten(info.Definition.PolicyJson);
            var siteValues=new Dictionary<string,string>(StringComparer.Ordinal);
            if(device.LocationId is Guid site)
            {
                var siteRow=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(
                    x=>x.Scope=="Site"&&x.LocationId==site,ct);
                if(siteRow is not null)
                {
                    try{
                        var link=SiteConfigurationService.ReadLink(siteRow.NonSecretJson);
                        siteValues=Flatten(link.OverridesJson);
                    }catch(InvalidOperationException){}
                }
            }
            var section=device.IsAccessPoint?"wireless.":"switch.";
            siteValues=siteValues.Where(x=>x.Key.StartsWith(section,StringComparison.Ordinal))
                .ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            var deviceValues=Flatten(info.Binding.DeviceOverrideJson);
            var optimizations=Flatten(info.Binding.OptimizationOverrideJson);
            var desired=Flatten(info.Resolved.DesiredJson);
            var snapshot=await db.ConfigurationSnapshots.AsNoTracking()
                .Where(x=>x.DeviceId==device.Id &&
                   (x.CapturedBy=="certified-effective-readonly"||
                    x.CapturedBy=="manual-import-unverified"))
                .OrderByDescending(x=>x.CapturedUtc).FirstOrDefaultAsync(ct);
            var actual=new Dictionary<string,string>(StringComparer.Ordinal);
            var certified=false;
            if(snapshot is not null)
            {
                try
                {
                    using var json=JsonDocument.Parse(snapshot.SanitizedJson);
                    if(snapshot.CapturedBy=="certified-effective-readonly" &&
                       json.RootElement.TryGetProperty("schema",out var schema) &&
                       schema.GetString()=="jcf-certified-settings/v1" &&
                       json.RootElement.TryGetProperty("settings",out var settings))
                    {
                        certified=true;actual=Flatten(settings.GetRawText());
                    }
                    else if(snapshot.CapturedBy=="manual-import-unverified")
                    {
                        // Full original was retained encrypted, but this imported state is
                        // NEVER treated as a certified observation of the active device.
                        if(json.RootElement.TryGetProperty("content",out var imported) &&
                           imported.TryGetProperty("vlans",out var vlans) &&
                           vlans.ValueKind==JsonValueKind.Array)
                            actual["imported.vlans"]=vlans.GetRawText();
                    }
                }catch(JsonException){}
            }
            var pending=await db.ConfigProposals.AsNoTracking().AnyAsync(p=>p.DeviceId==device.Id &&
                (p.State==ConfigProposalState.PendingApproval||p.State==ConfigProposalState.PendingSecondApproval||
                 p.State==ConfigProposalState.Approved||p.State==ConfigProposalState.Executing),ct);
            all.AddRange(Compare(device.Id,device.Name,device.Kind.ToString(),info.SiteName,
                baseValues,siteValues,deviceValues,optimizations,desired,actual,
                certified,snapshot?.CapturedUtc,pending));
        }
        return new Report(siteId,DateTime.UtcNow,all,devices.Count,
            all.Count(x=>x.Indicator=="!"),all.Count(x=>x.Indicator=="P"),
            all.Count(x=>x.Quality=="Not observed"));
    }
}