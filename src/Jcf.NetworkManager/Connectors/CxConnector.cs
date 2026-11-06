using System.Net.Http.Headers;
using System.Text.Json;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;

namespace Jcf.NetworkManager.Connectors;

/// <summary>AOS-CX: REST session + CSRF, read-only inventory/port/VLAN inspection.</summary>
public sealed class CxConnector(DeviceHttpFactory factory,SecretResolver secrets) : IDeviceConnector
{
    public bool Supports(DeviceKind kind) => kind is DeviceKind.Cx6100 or DeviceKind.Cx6200;

    public async Task<ProbeResult> ProbeAsync(Device d,CancellationToken ct)
    {
        using var http = factory.Create(d,DevicePorts.Management(d.Kind));
        var tests = new List<string>();
        string? firmware=null,problem=null,csrf=null;
        int? vlans=null,ports=null;
        var portReadings=new List<DetectedPort>();
        bool authenticated=false;
        try
        {
            var (user,password) = await secrets.ResolveAsync(d.SecretRef,ct);
            // AOS-CX REST 10.09+ documents a POST with username/password query parameters,
            // session cookie and x-use-csrf-token header. Do not log the request URI: it
            // contains credentials. The adapter uses its own HttpClient without logging.
            var loginUrl="rest/latest/login?username="+Uri.EscapeDataString(user)+
                "&password="+Uri.EscapeDataString(password);
            using var login = new HttpRequestMessage(HttpMethod.Post,loginUrl)
                {Content=new ByteArrayContent(Array.Empty<byte>())};
            login.Headers.TryAddWithoutValidation("x-use-csrf-token","true");
            using var response = await http.SendAsync(login,ct);
            response.EnsureSuccessStatusCode();
            authenticated=true;
            csrf = response.Headers.TryGetValues("x-csrf-token",out var values) ? values.FirstOrDefault() : null;
            if (csrf is not null) http.DefaultRequestHeaders.TryAddWithoutValidation("x-csrf-token",csrf);
            tests.Add("PASS AOS-CX session authentication");
            using var system = await Read("rest/latest/system",true);
            if (system is not null && system.RootElement.TryGetProperty("software_version",out var v)) firmware=v.ToString();
            using var vlan = await Read("rest/latest/system/vlans?depth=2",true);
            if (vlan is not null) vlans = vlan.RootElement.ValueKind==JsonValueKind.Object? vlan.RootElement.EnumerateObject().Count() :
                vlan.RootElement.ValueKind==JsonValueKind.Array? vlan.RootElement.GetArrayLength(): null;
            // AOS-CX depth=1 commonly returns URI references rather than per-port objects.
            // Request depth=2 to obtain actual port state/counters for SQL-backed monitoring.
            using var interfaces = await Read("rest/latest/system/interfaces?depth=2",true);
            if (interfaces is not null) {
                ports=interfaces.RootElement.ValueKind==JsonValueKind.Object? interfaces.RootElement.EnumerateObject().Count():
                    interfaces.RootElement.ValueKind==JsonValueKind.Array? interfaces.RootElement.GetArrayLength():null;
                portReadings=ParsePortReadings(interfaces.RootElement);
            }
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        { // Never persist raw HttpClient exception text: login URI contains a credential query parameter.
          problem = DeviceErrors.Describe(ex,"AOS-CX REST API");
          tests.Add("FAIL "+problem); }
        finally
        {
            if(authenticated)
            {
                try { using var _ = await http.PostAsync("rest/latest/logout",null,CancellationToken.None); } catch { }
            }
        }
        return new ProbeResult(authenticated,firmware,null,null,vlans,ports,new(),new(),new(),problem,tests,null,portReadings);

        async Task<JsonDocument?> Read(string path,bool required)
        {
            try
            {
                using var response=await http.GetAsync(path,ct);
                response.EnsureSuccessStatusCode();
                var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                tests.Add("PASS "+path);
                return document;
            }
            catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException)
            {tests.Add((required?"FAIL ":"WARN ")+path+": "+ex.GetType().Name);problem??="AOS-CX read unavailable: "+path;return null;}
        }
    }
    // Do not manufacture counters. Only fields verified present in the REST JSON are populated.
    public static List<DetectedPort> ParsePortReadings(JsonElement root)
    {
        var result=new List<DetectedPort>();
        if(root.ValueKind!=JsonValueKind.Object) return result;
        foreach(var property in root.EnumerateObject())
        {
            if(property.Value.ValueKind!=JsonValueKind.Object) continue;
            var obj=property.Value;
            string? Field(string key)=>obj.TryGetProperty(key,out var f) ? f.ToString() : null;
            long? Counter(string key)=>obj.TryGetProperty(key,out var f) && f.ValueKind==JsonValueKind.Number && f.TryGetInt64(out var n) && n>=0 ? n:null;
            decimal? Power()=>obj.TryGetProperty("poe_power",out var f) && f.ValueKind==JsonValueKind.Number && f.TryGetDecimal(out var n) ? n:null;
            result.Add(new DetectedPort(property.Name,Field("link_state")??Field("oper_state"),Counter("rx_bytes"),
                Counter("tx_bytes"),Counter("rx_errors"),Counter("tx_errors"),Power()));
        }
        return result;
    }

}
