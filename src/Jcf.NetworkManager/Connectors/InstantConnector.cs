using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;

namespace Jcf.NetworkManager.Connectors;

/// <summary>Aruba Instant 8: login/logout are POST; every inspection operation is GET only.</summary>
public sealed class InstantConnector(DeviceHttpFactory factory, SecretResolver secrets) : IDeviceConnector
{
    public bool Supports(DeviceKind kind) => kind is DeviceKind.Ap505 or DeviceKind.Ap535;

    public async Task<ProbeResult> ProbeAsync(Device d, CancellationToken ct)
    {
        var tests = new List<string>();
        var arm = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var fields = new Dictionary<string,bool>();
        var members = new List<DetectedMember>();
        string? version = null, issue = null;
        int? count = null, clients = null;
        bool authenticated = false;
        RfObservation? observation = null;
        var detectedClients=new List<DetectedClient>();
        using var http = factory.Create(d, DevicePorts.Management(d.Kind));
        string? sid = null;
        try
        {
            var (username, password) = await secrets.ResolveAsync(d.SecretRef, ct);
            using var login = await http.PostAsync("rest/login",DeviceJson.Body(new { user = username, passwd = password }), ct);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync(ct));
            if (!login.IsSuccessStatusCode || !loginJson.RootElement.TryGetProperty("Status", out var statusElement) ||
                !string.Equals(statusElement.GetString(), "success", StringComparison.OrdinalIgnoreCase) ||
                !loginJson.RootElement.TryGetProperty("sid", out var sidElement))
                throw new InvalidOperationException("Instant REST authentication rejected; check VC address, credentials and allow-rest-api.");
            sid = sidElement.GetString();
            if (string.IsNullOrEmpty(sid)) throw new InvalidOperationException("Instant session ID missing.");
            authenticated = true;
            tests.Add("PASS Instant session authentication");

            // Failed optional read operations do not invalidate all observations.
            var versionText = await ReadAsync("show version", true);
            var match = Regex.Match(versionText ?? "", @"\b(8\.\d+\.\d+\.\d+)(?:_\d+)?\b");
            if (match.Success) version = match.Groups[1].Value;
            var apText = await ReadAsync("show aps", true);
            (members, count) = ParseMembers(apText ?? "");
            var clientText = await ReadAsync("show clients", false);
            var clientCountMatch = Regex.Match(clientText ?? "", @"\b(\d+)\s+(?:Clients?|wireless clients?)\b", RegexOptions.IgnoreCase);
            if (clientCountMatch.Success) clients = int.Parse(clientCountMatch.Groups[1].Value);
            detectedClients=ParseClients(clientText);
            var config = await ReadAsync("show running-config", true);
            arm = ExtractArm(config ?? "");
            var armText = await ReadAsync("show arm config", false);
            var statsText = await ReadAsync("show stats ap " + d.ManagementIp, false);
            observation = ParseRfObservation(statsText);
            var combined = string.Join("\n", new[] { apText, clientText, statsText, armText });
            foreach (var (key,pattern) in new Dictionary<string,string> {
                ["RSSI"] = @"\bRSSI\b", ["SNR"] = @"\bSNR\b", ["Retries"] = @"retr(?:y|ies)",
                ["ChannelUtilization"] = @"utilization|channel\s+busy", ["NoiseFloor"] = @"noise\s+floor",
                ["TxRxRate"] = @"(?:tx|rx).{0,10}rate|throughput", ["Roaming"] = @"roam"
            }) fields[key] = Regex.IsMatch(combined,pattern,RegexOptions.IgnoreCase);
            // A field header is evidence of availability, not of a populated per-client measurement.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            issue = DeviceErrors.Describe(ex, "Instant REST API");
            tests.Add("FAIL " + issue);
        }
        finally
        {
            if (sid is not null)
            {
                try { using var logout = await http.PostAsync("rest/logout",DeviceJson.Body(new { sid }), CancellationToken.None); }
                catch { /* session will expire; do not expose credentials */ }
            }
        }
        return new ProbeResult(authenticated,version,count,clients,null,null,arm,fields,members,issue,tests,observation,null,detectedClients);

        async Task<string?> ReadAsync(string command, bool required)
        {
            try
            {
                var url = $"rest/show-cmd?iap_ip_addr={Uri.EscapeDataString(d.ManagementIp)}&cmd={Uri.EscapeDataString(command)}&sid={Uri.EscapeDataString(sid!)}";
                using var response = await http.GetAsync(url,ct);
                response.EnsureSuccessStatusCode();
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = result.RootElement;
                if (!root.TryGetProperty("Status-code",out var code) || code.ToString() != "0" ||
                    !root.TryGetProperty("Command output", out var output))
                    throw new InvalidOperationException("Device rejected " + command);
                tests.Add("PASS " + command);
                return output.GetString();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                tests.Add((required ? "FAIL " : "WARN ") + command + ": " + ex.GetType().Name);
                if (required) issue ??= "Required read failed: " + command;
                return null;
            }
        }
    }

    /// <summary>
    /// Conservative discovery of client MACs in show clients output. Values are never
    /// accepted as a validated client signal/roaming measurement until firmware-certified.
    /// Raw MACs must be immediately HMAC-pseudonymized by the persistence service.
    /// </summary>
    public static List<DetectedClient> ParseClients(string? output)
    {
        var result=new List<DetectedClient>();
        if(string.IsNullOrWhiteSpace(output))return result;
        var macRegex=new Regex(@"(?i)(?<![0-9a-f])(?:[0-9a-f]{2}:){5}[0-9a-f]{2}(?![0-9a-f])",
            RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(200));
        foreach(var line in output.Split('\n').Take(1000))
        {
            var match=macRegex.Match(line);
            if(!match.Success)continue;
            // No reliable SSID/PHY/SNR inference from variable-width CLI columns.
            result.Add(new DetectedClient(match.Value.ToLowerInvariant(),null,null,null,null,null));
        }
        return result.DistinctBy(x=>x.Mac).Take(500).ToList();
    }

    /// <summary>Conservative pattern extraction, never infer a value from a mere field name.
    /// Firmware-specific certification is needed before numeric metrics drive automated changes.</summary>
    public static RfObservation? ParseRfObservation(string? stats)
    {
        if(string.IsNullOrWhiteSpace(stats))return null;
        int? IntField(string key,int min,int max) {
            var found=Regex.Match(stats,@"(?im)^\s*"+key+@"\s*[:=]\s*(\d+)\b",RegexOptions.IgnoreCase);
            if(found.Success && int.TryParse(found.Groups[1].Value,out var n) && n>=min && n<=max)return n;
            return null;
        }
        decimal? DecField(string key,int min,int max) {
            var found=Regex.Match(stats,@"(?im)^\s*"+key+@"\s*[:=]\s*(-?\d+(?:\.\d+)?)\b",RegexOptions.IgnoreCase);
            if(found.Success && decimal.TryParse(found.Groups[1].Value,System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,out var n) && n>=min && n<=max)return n;
            return null;
        }
        var channel=IntField("Channel",1,196);
        var width=IntField(@"Channel Width(?: \(MHz\))?",20,160);
        var utilization=DecField(@"Channel Utilization(?: \(percent\))?",0,100);
        var noise=DecField(@"Noise Floor(?: \(dBm\))?",-130,-20);
        var retries=DecField(@"Client Retry Rate(?: \(percent\))?",0,100);
        if(channel is null && width is null && utilization is null && noise is null && retries is null)return null;
        return new RfObservation(channel,width,utilization,noise,retries,"Instant show stats ap; labeled values (heuristic parsing)");
    }

    /// <summary>ARM keys the platform reads, compares and (when certified) writes. Never includes secrets.</summary>
    public static readonly HashSet<string> ArmKeys = new(StringComparer.OrdinalIgnoreCase) { "80mhz-support", "min-tx-power", "max-tx-power", "wide-bands",
        "band-steering-mode", "air-time-fairness-mode", "client-aware", "scanning", "client-match",
        "cm-min-snr", "spectrum-load-balancing", "a-channels", "g-channels" };

    /// <summary>Parses the Instant <c>arm</c> block. Valued settings ("min-tx-power 9") keep their value; bare
    /// flags ("client-match", "80mhz-support") are recorded as "enabled" and "no &lt;flag&gt;" as "disabled".</summary>
    public static Dictionary<string,string> ExtractArm(string config)
    {
        var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        bool inArm = false;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim() == "arm") { inArm = true; continue; }
            if (!inArm) continue;
            if (!string.IsNullOrWhiteSpace(line) && !char.IsWhiteSpace(line[0])) break;
            var parts = line.Trim().Split(' ',2,StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length == 1 && ArmKeys.Contains(parts[0])) { values[parts[0]] = "enabled"; continue; }
            if (parts.Length == 2 && parts[0] == "no" && ArmKeys.Contains(parts[1].Trim())) { values[parts[1].Trim()] = "disabled"; continue; }
            if (parts.Length == 2 && ArmKeys.Contains(parts[0]) && Regex.IsMatch(parts[1].Trim(), @"^[\w.,: -]{1,80}$"))
                values[parts[0]] = parts[1].Trim();
        }
        return values;
    }

    public static (List<DetectedMember> Members,int? ReportedCount) ParseMembers(string output)
    {
        var entries = new List<DetectedMember>();
        int? count = null;
        var m = Regex.Match(output,@"\b(\d+)\s+Access Points?\b",RegexOptions.IgnoreCase);
        if (m.Success) count = int.Parse(m.Groups[1].Value);
        // AP table formatting varies by firmware. Refuse to invent member identity.
        foreach (var line in output.Split('\n'))
        {
            var ip = Regex.Match(line,@"(?<!\d)(?:10|172|192)\.\d{1,3}\.\d{1,3}\.\d{1,3}(?!\d)");
            if (!ip.Success || !Regex.IsMatch(line,@"\bAP[- ]?\d{3}\b",RegexOptions.IgnoreCase)) continue;
            var model = Regex.Match(line,@"\bAP[- ]?\d{3}\b",RegexOptions.IgnoreCase);
            if (!model.Success) continue;
            var name = line.Split(' ',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Unknown";
            var masked = ip.Value[..ip.Value.LastIndexOf('.')]+".xxx";
            entries.Add(new DetectedMember(name,model.Value,null,masked,"Tentative (table parsing requires firmware check)"));
        }
        return (entries.DistinctBy(x=> x.Name).ToList(),count);
    }
}
