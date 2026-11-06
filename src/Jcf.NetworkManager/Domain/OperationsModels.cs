using System.ComponentModel.DataAnnotations;

namespace Jcf.NetworkManager.Domain;

// One physically located AP may belong to a technically separate Instant VC cluster.
public sealed class DevicePlacement
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Guid LocationId { get; set; }
    public DateTime FromUtc { get; set; }=DateTime.UtcNow;
    public DateTime? ThroughUtc { get; set; }
    [MaxLength(180)] public string ChangedBy { get; set; }="";
}
public sealed class PhysicalSpace
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid LocationId { get; set; }
    public Guid? ParentSpaceId { get; set; }
    [MaxLength(120)] public string Name { get; set; }="";
    [MaxLength(20)] public string Type { get; set; }="Building";
}
public sealed class RadioObservation
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid DeviceId { get; set; }
    [MaxLength(120)] public string? RadioName { get; set; }
    public DateTime AtUtc { get; set; }=DateTime.UtcNow;
    public int? Channel { get; set; }
    public int? WidthMhz { get; set; }
    public decimal? PowerDbm { get; set; }
    public decimal? NoiseFloorDbm { get; set; }
    public decimal? UtilizationPct { get; set; }
    public decimal? ErrorPct { get; set; }
    [MaxLength(120)] public string Source { get; set; }="Unknown";
    [MaxLength(30)] public string Quality { get; set; }="Uncertified";
}
public sealed class ClientSession
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid DeviceId { get; set; }
    [MaxLength(64)] public string ClientHash { get; set; }="";
    [MaxLength(80)] public string? Ssid { get; set; }
    [MaxLength(20)] public string? Band { get; set; }
    public DateTime FirstSeenUtc { get; set; }=DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; }=DateTime.UtcNow;
    public decimal? RssiDbm { get; set; }
    public decimal? SnrDb { get; set; }
    public decimal? RetryPct { get; set; }
    public int? AssociatedVlan { get; set; }
    [MaxLength(80)] public string Source { get; set; }="Unknown";
}
public sealed class PortObservation
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid DeviceId { get; set; }
    [MaxLength(90)] public string Port { get; set; }="";
    public DateTime AtUtc { get; set; }=DateTime.UtcNow;
    public long? RxBytes { get; set; }
    public long? TxBytes { get; set; }
    public long? RxErrors { get; set; }
    public long? TxErrors { get; set; }
    public decimal? UtilizationPct { get; set; }
    public decimal? PoeWatts { get; set; }
    [MaxLength(30)] public string LinkState { get; set; }="Unknown";
    [MaxLength(80)] public string Source { get; set; }="Unknown";
}
public sealed class TopologyLink
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid DeviceId { get; set; }
    [MaxLength(90)] public string LocalPort { get; set; }="";
    public Guid? NeighborDeviceId { get; set; }
    [MaxLength(120)] public string? NeighborName { get; set; }
    [MaxLength(90)] public string? NeighborPort { get; set; }
    [MaxLength(40)] public string Evidence { get; set; }="Unverified";
    public DateTime ObservedUtc { get; set; }=DateTime.UtcNow;
}
public sealed class PolicyTemplate
{
    public Guid Id { get; set; }=Guid.NewGuid();
    [MaxLength(120)] public string Name { get; set; }="";
    [MaxLength(50)] public string Scope { get; set; }="Global";
    public Guid? LocationId { get; set; }
    // Policy only; credentials and SSID PSKs are deliberately excluded.
    [MaxLength(7000)] public string NonSecretJson { get; set; }="{}";
    public int Version { get; set; }=1;
    [MaxLength(140)] public string CreatedBy { get; set; }="";
    public DateTime CreatedUtc { get; set; }=DateTime.UtcNow;
}
public sealed class ConfigurationSnapshot
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid DeviceId { get; set; }
    [MaxLength(60)] public string Firmware { get; set; }="";
    [MaxLength(64)] public string DataSha256 { get; set; }="";
    // Whitelisted non-secret configuration only. Full backups require encrypted storage integration.
    [MaxLength(12000)] public string SanitizedJson { get; set; }="{}";
    public DateTime CapturedUtc { get; set; }=DateTime.UtcNow;
    [MaxLength(120)] public string CapturedBy { get; set; }="system";
}
public sealed class ComplianceFinding
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid DeviceId { get; set; }
    [MaxLength(70)] public string Rule { get; set; }="";
    [MaxLength(600)] public string Evidence { get; set; }="";
    [MaxLength(24)] public string Severity { get; set; }="Warning";
    [MaxLength(24)] public string State { get; set; }="Open";
    public DateTime DetectedUtc { get; set; }=DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }
}
public sealed class OperationsIncident
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid? DeviceId { get; set; }
    public Guid? LocationId { get; set; }
    [MaxLength(120)] public string Title { get; set; }="";
    [MaxLength(1200)] public string Description { get; set; }="";
    [MaxLength(30)] public string Status { get; set; }="Open";
    [MaxLength(20)] public string Severity { get; set; }="Medium";
    [MaxLength(140)] public string? Owner { get; set; }
    public DateTime OpenedUtc { get; set; }=DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }
}
public sealed class SecurityObservation
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid? DeviceId { get; set; }
    public DateTime ObservedUtc { get; set; }=DateTime.UtcNow;
    [MaxLength(80)] public string EventType { get; set; }="";
    [MaxLength(30)] public string Severity { get; set; }="Info";
    [MaxLength(1200)] public string Description { get; set; }="";
    [MaxLength(80)] public string Source { get; set; }="Unknown";
}
public sealed class ServicePathProbe
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid? DeviceId { get; set; }
    public Guid? LocationId { get; set; }
    public DateTime AtUtc { get; set; }=DateTime.UtcNow;
    [MaxLength(80)] public string Type { get; set; }="DNS";
    [MaxLength(200)] public string Target { get; set; }="";
    [MaxLength(40)] public string Status { get; set; }="Unknown";
    [MaxLength(350)] public string Detail { get; set; }="";
    [MaxLength(100)] public string TestAgent { get; set; }="server";
}
public sealed class TelemetryRollup
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DateTime BucketUtc { get; set; }
    public int BucketMinutes { get; set; }=60;
    public int SampleCount { get; set; }
    public decimal? AverageUtilizationPct { get; set; }
    public decimal? MaxUtilizationPct { get; set; }
    public decimal? AverageNoiseFloorDbm { get; set; }
}
public sealed class ReportDefinition
{
    public Guid Id { get; set; }=Guid.NewGuid();
    [MaxLength(100)] public string Name { get; set; }="";
    [MaxLength(50)] public string Kind { get; set; }="DeviceHealth";
    public Guid? LocationId { get; set; }
    public bool Enabled { get; set; }
    [MaxLength(120)] public string CreatedBy { get; set; }="";
}
public sealed class ShadowPrediction
{
    public Guid Id { get; set; }=Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DateTime AtUtc { get; set; }=DateTime.UtcNow;
    [MaxLength(60)] public string ModelVersion { get; set; }="heuristic-0.1";
    public decimal? CongestionRisk { get; set; }
    [MaxLength(700)] public string Reason { get; set; }="";
    public bool ShadowOnly { get; set; }=true;
}
public sealed class IntegrationTarget
{
    public Guid Id { get; set; }=Guid.NewGuid();
    [MaxLength(80)] public string Kind { get; set; }="Webhook";
    [MaxLength(120)] public string Name { get; set; }="";
    [MaxLength(200)] public string Url { get; set; }="";
    [MaxLength(80)] public string SecretRef { get; set; }="";
    public bool Enabled { get; set; }
}
