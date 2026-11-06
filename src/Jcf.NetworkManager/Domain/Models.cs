using System.ComponentModel.DataAnnotations;

namespace Jcf.NetworkManager.Domain;

public enum DeviceKind { Ap505, Ap535, Cx6100, Cx6200 }
public enum RecommendationState { Pending, Approved, Rejected, Deferred, Superseded }

public sealed class Location
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(40)] public string Type { get; set; } = "Station";
    public Guid? ParentId { get; set; }
    [MaxLength(200)] public string? Address { get; set; }
}
public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; }
    [MaxLength(100)] public string ManagementIp { get; set; } = "";
    [MaxLength(80)] public string SecretRef { get; set; } = "";
    public Guid? LocationId { get; set; }
    [MaxLength(60)] public string? Firmware { get; set; }
    [MaxLength(60)] public string? ClusterName { get; set; }
    [MaxLength(30)] public string LastStatus { get; set; } = "Unknown";
    [MaxLength(300)] public string? LastIssue { get; set; }
    public DateTime? LastProbeUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Sample/demo equipment served by the built-in simulator. A simulated device is never contacted on the
    /// network and its "changes" are mock-only. Every device an operator registers is real (false).</summary>
    public bool IsSimulated { get; set; }
    /// <summary>Operator-confirmed SHA-256 fingerprint of the device's HTTPS certificate (64 hex). When set it is the
    /// only certificate the connector accepts for this device (Aruba ships self-signed management certificates).</summary>
    [MaxLength(64)] public string? TlsCertSha256 { get; set; }
    [MaxLength(180)] public string? TlsPinnedBy { get; set; }
    public DateTime? TlsPinnedUtc { get; set; }
    public bool IsAccessPoint => Kind is DeviceKind.Ap505 or DeviceKind.Ap535;
}

/// <summary>Device management credentials, encrypted with ASP.NET Core Data Protection. Referenced by
/// <see cref="Device.SecretRef"/> so one profile can serve many devices (for example a whole Instant cluster).
/// Plaintext never leaves <c>CredentialVault</c>; no API or page returns it.</summary>
public sealed class DeviceCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public string Reference { get; set; } = "";
    [MaxLength(2000)] public string ProtectedUsername { get; set; } = "";
    [MaxLength(4000)] public string ProtectedPassword { get; set; } = "";
    [MaxLength(200)] public string? Description { get; set; }
    [MaxLength(180)] public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class ClusterMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConductorDeviceId { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(60)] public string Model { get; set; } = "Unknown";
    [MaxLength(80)] public string? Zone { get; set; }
    [MaxLength(120)] public string? IpMasked { get; set; }
    [MaxLength(30)] public string DiscoveryConfidence { get; set; } = "Unknown";
    public DateTime ObservedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class TelemetrySnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public bool ApiConnected { get; set; }
    public int? ApCount { get; set; }
    public int? ClientCount { get; set; }
    public int? VlanCount { get; set; }
    public int? PortCount { get; set; }
    // Null means absent/unverified, never fabricate a zero. Source quality stays attached.
    public int? RadioChannel { get; set; }
    public int? ChannelWidthMhz { get; set; }
    public decimal? ChannelUtilizationPct { get; set; }
    public decimal? NoiseFloorDbm { get; set; }
    public decimal? ClientRetryPct { get; set; }
    [MaxLength(80)] public string ObservationSource { get; set; } = "Unavailable";
    [MaxLength(6000)] public string ArmJson { get; set; } = "{}";
    [MaxLength(3000)] public string MetricAvailabilityJson { get; set; } = "{}";
    [MaxLength(200)] public string? Problem { get; set; }
}
public sealed class AnalysisRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(40)] public string Trigger { get; set; } = "Manual";
    [MaxLength(40)] public string Status { get; set; } = "Completed";
    public int DevicesEvaluated { get; set; }
    public int Findings { get; set; }
    [MaxLength(180)] public string InitiatedBy { get; set; } = "system";
}
public sealed class Recommendation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    [MaxLength(100)] public string Fingerprint { get; set; } = "";
    [MaxLength(120)] public string Title { get; set; } = "";
    [MaxLength(800)] public string Evidence { get; set; } = "";
    [MaxLength(300)] public string ProposedSetting { get; set; } = "";
    [MaxLength(20)] public string Risk { get; set; } = "Medium";
    public RecommendationState State { get; set; } = RecommendationState.Pending;
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(180)] public string CreatedBy { get; set; } = "system";
    [MaxLength(180)] public string? ReviewedBy { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public bool Executed { get; set; } = false; // Not executable in Sprint 1
}
public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(180)] public string Actor { get; set; } = "";
    [MaxLength(120)] public string Action { get; set; } = "";
    [MaxLength(120)] public string Target { get; set; } = "";
    [MaxLength(200)] public string Outcome { get; set; } = "";
}
public sealed class AnalysisSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; }
    public int EveryMinutes { get; set; } = 60;
    public DateTime? LastRunUtc { get; set; }
}

/// <summary>Sanitized, SQL-persisted evidence from an actual vendor REST read against an approved pilot device.
/// Records do not contain credentials, API session IDs, raw client MACs, or unredacted configurations.</summary>
public sealed class HardwareAcceptanceRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(180)] public string Actor { get; set; } = "";
    [MaxLength(50)] public string Verdict { get; set; } = "NotVerified";
    [MaxLength(200)] public string? Summary { get; set; }
    public bool SqlServerVerified { get; set; }
    public bool DeviceApiAuthenticated { get; set; }
    public bool FreshSnapshotPersisted { get; set; }
    public bool ExpectedInventoryPersisted { get; set; }
    public int? ReportedApCount { get; set; }
    public int StoredClusterMembers { get; set; }
    public int StoredPortSamples { get; set; }
    [MaxLength(60)] public string? Firmware { get; set; }
    [MaxLength(1500)] public string EvidenceJson { get; set; } = "{}";
}
