using System.ComponentModel.DataAnnotations;

namespace Jcf.NetworkManager.Domain;

public enum ConfigAction {
    CreateSsid, ChangeBandSteering, ChangeMinTxPower, CreateSwitchVlan,
    ChangeClientMatch, SetChannelPolicy, SetSwitchPortVlan, SetSwitchPoe,
    UpsertWlanProfile, SetWlanEnabled, DeleteWlanProfile, SetAirtimeFairness, SetRfPowerBounds,
    PlanCxTrunk, PlanCxPortAdmin, PlanCxLag, PlanCxLldp, PlanCxPoePolicy
}
public enum ConfigProposalState { PendingApproval, PendingSecondApproval, Approved, Rejected, Executing, Applied, Failed, RecoveryRequired }
public enum ApiCapabilityState { Unknown, ReadVerified, WriteVerified, Unsupported, Blocked }

public sealed class DeviceGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(100)] public string? Notes { get; set; }
    public Guid? LocationId { get; set; }
    [MaxLength(30)] public string Kind { get; set; } = "Administrative";
}
public sealed class GroupMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime AssignedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class DeviceCapability
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    [MaxLength(70)] public string Operation { get; set; } = "";
    [MaxLength(50)] public string Firmware { get; set; } = "";
    public ApiCapabilityState State { get; set; } = ApiCapabilityState.Unknown;
    [MaxLength(150)] public string EvidenceReference { get; set; } = "";
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class ConfigProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public ConfigAction Action { get; set; }
    // Non-secret, canonical JSON, contains only allowlisted fields (e.g., credential reference, never a PSK).
    [MaxLength(2000)] public string PayloadJson { get; set; } = "{}";
    [MaxLength(64)] public string PayloadSha256 { get; set; } = "";
    [MaxLength(64)] public string ApprovedPayloadSha256 { get; set; } = "";
    [MaxLength(150)] public string RequestedBy { get; set; } = "";
    [MaxLength(150)] public string? ApprovedBy { get; set; }
    [MaxLength(150)] public string? SecondApprovedBy { get; set; }
    [MaxLength(500)] public string? Justification { get; set; }
    [MaxLength(600)] public string? LastOutcome { get; set; }
    [MaxLength(50)] public string? BaselineFirmware { get; set; }
    public int? BaselineClusterMemberCount { get; set; }
    public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public ConfigProposalState State { get; set; } = ConfigProposalState.PendingApproval;
    [Timestamp] public byte[]? RowVersion { get; set; }
}
public sealed class ConfigExecutionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProposalId { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(60)] public string Stage { get; set; } = "";
    [MaxLength(40)] public string Status { get; set; } = "";
    [MaxLength(800)] public string Detail { get; set; } = "";
}
public sealed class NetworkAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? DeviceId { get; set; }
    [MaxLength(60)] public string Fingerprint { get; set; } = "";
    [MaxLength(20)] public string Severity { get; set; } = "Warning";
    [MaxLength(120)] public string Category { get; set; } = "";
    [MaxLength(500)] public string Detail { get; set; } = "";
    [MaxLength(30)] public string State { get; set; } = "Open";
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}
