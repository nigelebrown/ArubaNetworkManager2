using Jcf.NetworkManager.Domain;

namespace Jcf.NetworkManager.Connectors;

public sealed record DetectedMember(string Name, string Model, string? Zone, string? IpMasked, string Confidence);
public sealed record DetectedClient(string Mac,string? Ssid,string? Band,decimal? RssiDbm,decimal? SnrDb,int? Vlan);
public sealed record DetectedPort(string Name,string? State,long? RxBytes,long? TxBytes,long? RxErrors,long? TxErrors,decimal? PoeWatts);
public sealed record RfObservation(int? Channel,int? WidthMhz,decimal? UtilizationPct,decimal? NoiseFloorDbm,decimal? ClientRetryPct,string Source);
public sealed record ProbeResult(
    bool Connected, string? Firmware, int? ApCount, int? ClientCount,
    int? VlanCount, int? PortCount,
    Dictionary<string,string> Arm, Dictionary<string,bool> Fields,
    List<DetectedMember> Members, string? Issue,
    List<string> ApiChecks,RfObservation? Observation = null, List<DetectedPort>? PortReadings = null, List<DetectedClient>? Clients = null,
    InstantWlanReadback.Result? WlanReadback = null,
    CxSwitchReadback.Result? CxReadback = null);

public interface IDeviceConnector
{
    /// <summary>True only for the built-in simulator that serves sample (IsSimulated) devices.</summary>
    bool IsSimulator => false;
    bool Supports(DeviceKind kind);
    Task<ProbeResult> ProbeAsync(Device device, CancellationToken cancellationToken);
}

/// <summary>Operator-actionable, credential-free descriptions of device API failures. Raw exception text is never
/// persisted: an AOS-CX login URI carries credentials in its query string.</summary>
public static class DeviceErrors
{
    public static string Describe(Exception ex, string api) => ex switch
    {
        HttpRequestException h when h.InnerException is System.Security.Authentication.AuthenticationException
            || h.HttpRequestError == HttpRequestError.SecureConnectionError =>
            api + ": TLS certificate not trusted. Capture and pin the device certificate under Devices, or install a CA-issued certificate on the device.",
        HttpRequestException h when h.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError =>
            api + ": management port unreachable from the application server (routing, ACL or API disabled).",
        HttpRequestException h when h.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            api + ": credentials rejected by the device.",
        HttpRequestException => api + ": communication or HTTP error.",
        OperationCanceledException => api + ": request timeout.",
        System.Text.Json.JsonException => api + ": invalid JSON response.",
        InvalidOperationException => ex.Message.Length > 280 ? ex.Message[..280] : ex.Message,
        _ => api + ": " + ex.GetType().Name
    };
}

/// <summary>JSON request bodies for device REST APIs with an explicit Content-Length. <c>PostAsJsonAsync</c>/<c>JsonContent</c>
/// stream with chunked transfer encoding, which embedded management web servers commonly reject or read as empty.</summary>
public static class DeviceJson
{
    public static HttpContent Body(object value) =>
        new StringContent(System.Text.Json.JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}
