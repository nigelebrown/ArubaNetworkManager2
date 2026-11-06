using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jcf.NetworkManager.Migrations
{
    /// <inheritdoc />
    public partial class InitialProduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalysisRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DevicesEvaluated = table.Column<int>(type: "int", nullable: false),
                    Findings = table.Column<int>(type: "int", nullable: false),
                    InitiatedBy = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    EveryMinutes = table.Column<int>(type: "int", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    IsDisabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Ssid = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Band = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RssiDbm = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    SnrDb = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    RetryPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    AssociatedVlan = table.Column<int>(type: "int", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClusterMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConductorDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Zone = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    IpMasked = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    DiscoveryConfidence = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ObservedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComplianceFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rule = table.Column<string>(type: "nvarchar(70)", maxLength: 70, nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    DetectedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigExecutionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigExecutionEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ApprovedPayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    SecondApprovedBy = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Justification = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastOutcome = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    BaselineFirmware = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    BaselineClusterMemberCount = table.Column<int>(type: "int", nullable: true),
                    RequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApprovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    State = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigProposals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Firmware = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    DataSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SanitizedJson = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CapturedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceCapabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(70)", maxLength: 70, nullable: false),
                    Firmware = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    State = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    EvidenceReference = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCapabilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DevicePlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ThroughUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChangedBy = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePlacements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    ManagementIp = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SecretRef = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Firmware = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ClusterName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LastStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LastIssue = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LastProbeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMemberships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HardwareAcceptanceRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Verdict = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SqlServerVerified = table.Column<bool>(type: "bit", nullable: false),
                    DeviceApiAuthenticated = table.Column<bool>(type: "bit", nullable: false),
                    FreshSnapshotPersisted = table.Column<bool>(type: "bit", nullable: false),
                    ExpectedInventoryPersisted = table.Column<bool>(type: "bit", nullable: false),
                    ReportedApCount = table.Column<int>(type: "int", nullable: true),
                    StoredClusterMembers = table.Column<int>(type: "int", nullable: false),
                    StoredPortSamples = table.Column<int>(type: "int", nullable: false),
                    Firmware = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    EvidenceJson = table.Column<string>(type: "nvarchar(1500)", maxLength: 1500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardwareAcceptanceRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SecretRef = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationTargets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Address = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NetworkAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Fingerprint = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    State = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationsIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1200)", maxLength: 1200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: true),
                    OpenedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationsIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PhysicalSpaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentSpaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalSpaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PolicyTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NonSecretJson = table.Column<string>(type: "nvarchar(max)", maxLength: 7000, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(140)", maxLength: 140, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PortObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Port = table.Column<string>(type: "nvarchar(90)", maxLength: 90, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RxBytes = table.Column<long>(type: "bigint", nullable: true),
                    TxBytes = table.Column<long>(type: "bigint", nullable: true),
                    RxErrors = table.Column<long>(type: "bigint", nullable: true),
                    TxErrors = table.Column<long>(type: "bigint", nullable: true),
                    UtilizationPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    PoeWatts = table.Column<decimal>(type: "decimal(7,2)", precision: 7, scale: 2, nullable: true),
                    LinkState = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortObservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RadioObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RadioName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: true),
                    WidthMhz = table.Column<int>(type: "int", nullable: true),
                    PowerDbm = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    NoiseFloorDbm = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    UtilizationPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    ErrorPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Quality = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RadioObservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Recommendations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    ProposedSetting = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Risk = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    ReviewedBy = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: true),
                    ReviewedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Executed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recommendations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObservedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1200)", maxLength: 1200, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityObservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServicePathProbes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(350)", maxLength: 350, nullable: false),
                    TestAgent = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePathProbes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShadowPredictions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModelVersion = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CongestionRisk = table.Column<decimal>(type: "decimal(6,3)", precision: 6, scale: 3, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(700)", maxLength: 700, nullable: false),
                    ShadowOnly = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShadowPredictions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SitePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    GrantedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GrantedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SitePermissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelemetryRollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BucketUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BucketMinutes = table.Column<int>(type: "int", nullable: false),
                    SampleCount = table.Column<int>(type: "int", nullable: false),
                    AverageUtilizationPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    MaxUtilizationPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    AverageNoiseFloorDbm = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryRollups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TelemetrySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApiConnected = table.Column<bool>(type: "bit", nullable: false),
                    ApCount = table.Column<int>(type: "int", nullable: true),
                    ClientCount = table.Column<int>(type: "int", nullable: true),
                    VlanCount = table.Column<int>(type: "int", nullable: true),
                    PortCount = table.Column<int>(type: "int", nullable: true),
                    RadioChannel = table.Column<int>(type: "int", nullable: true),
                    ChannelWidthMhz = table.Column<int>(type: "int", nullable: true),
                    ChannelUtilizationPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    NoiseFloorDbm = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    ClientRetryPct = table.Column<decimal>(type: "decimal(6,2)", precision: 6, scale: 2, nullable: true),
                    ObservationSource = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ArmJson = table.Column<string>(type: "nvarchar(max)", maxLength: 6000, nullable: false),
                    MetricAvailabilityJson = table.Column<string>(type: "nvarchar(3000)", maxLength: 3000, nullable: false),
                    Problem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetrySnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TopologyLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocalPort = table.Column<string>(type: "nvarchar(90)", maxLength: 90, nullable: false),
                    NeighborDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NeighborName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    NeighborPort = table.Column<string>(type: "nvarchar(90)", maxLength: 90, nullable: true),
                    Evidence = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ObservedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopologyLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_AtUtc",
                table: "AuditEvents",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSessions_ClientHash",
                table: "ClientSessions",
                column: "ClientHash");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSessions_DeviceId_LastSeenUtc",
                table: "ClientSessions",
                columns: new[] { "DeviceId", "LastSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSessions_LastSeenUtc",
                table: "ClientSessions",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClusterMembers_ConductorDeviceId_Name",
                table: "ClusterMembers",
                columns: new[] { "ConductorDeviceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceFindings_DeviceId_Rule_State",
                table: "ComplianceFindings",
                columns: new[] { "DeviceId", "Rule", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigExecutionEvents_ProposalId_AtUtc",
                table: "ConfigExecutionEvents",
                columns: new[] { "ProposalId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigProposals_DeviceId_State",
                table: "ConfigProposals",
                columns: new[] { "DeviceId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationSnapshots_DeviceId_CapturedUtc",
                table: "ConfigurationSnapshots",
                columns: new[] { "DeviceId", "CapturedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCapabilities_DeviceId_Operation",
                table: "DeviceCapabilities",
                columns: new[] { "DeviceId", "Operation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroups_Name",
                table: "DeviceGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevicePlacements_DeviceId_ThroughUtc",
                table: "DevicePlacements",
                columns: new[] { "DeviceId", "ThroughUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ManagementIp",
                table: "Devices",
                column: "ManagementIp",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SecretRef",
                table: "Devices",
                column: "SecretRef");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMemberships_GroupId_DeviceId",
                table: "GroupMemberships",
                columns: new[] { "GroupId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HardwareAcceptanceRuns_DeviceId_CompletedUtc",
                table: "HardwareAcceptanceRuns",
                columns: new[] { "DeviceId", "CompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ParentId_Name",
                table: "Locations",
                columns: new[] { "ParentId", "Name" },
                unique: true,
                filter: "[ParentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkAlerts_Fingerprint",
                table: "NetworkAlerts",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalSpaces_LocationId_ParentSpaceId_Name",
                table: "PhysicalSpaces",
                columns: new[] { "LocationId", "ParentSpaceId", "Name" },
                unique: true,
                filter: "[ParentSpaceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PortObservations_AtUtc",
                table: "PortObservations",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PortObservations_DeviceId_Port_AtUtc",
                table: "PortObservations",
                columns: new[] { "DeviceId", "Port", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RadioObservations_AtUtc",
                table: "RadioObservations",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RadioObservations_DeviceId_AtUtc",
                table: "RadioObservations",
                columns: new[] { "DeviceId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Recommendations_Fingerprint",
                table: "Recommendations",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityObservations_ObservedUtc",
                table: "SecurityObservations",
                column: "ObservedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePathProbes_AtUtc",
                table: "ServicePathProbes",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ShadowPredictions_DeviceId_AtUtc",
                table: "ShadowPredictions",
                columns: new[] { "DeviceId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SitePermissions_UserId_LocationId_Role",
                table: "SitePermissions",
                columns: new[] { "UserId", "LocationId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryRollups_DeviceId_BucketUtc_BucketMinutes",
                table: "TelemetryRollups",
                columns: new[] { "DeviceId", "BucketUtc", "BucketMinutes" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetrySnapshots_AtUtc",
                table: "TelemetrySnapshots",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TelemetrySnapshots_DeviceId_AtUtc",
                table: "TelemetrySnapshots",
                columns: new[] { "DeviceId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TopologyLinks_DeviceId_LocalPort",
                table: "TopologyLinks",
                columns: new[] { "DeviceId", "LocalPort" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisRuns");

            migrationBuilder.DropTable(
                name: "AnalysisSchedules");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "ClientSessions");

            migrationBuilder.DropTable(
                name: "ClusterMembers");

            migrationBuilder.DropTable(
                name: "ComplianceFindings");

            migrationBuilder.DropTable(
                name: "ConfigExecutionEvents");

            migrationBuilder.DropTable(
                name: "ConfigProposals");

            migrationBuilder.DropTable(
                name: "ConfigurationSnapshots");

            migrationBuilder.DropTable(
                name: "DeviceCapabilities");

            migrationBuilder.DropTable(
                name: "DeviceGroups");

            migrationBuilder.DropTable(
                name: "DevicePlacements");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "GroupMemberships");

            migrationBuilder.DropTable(
                name: "HardwareAcceptanceRuns");

            migrationBuilder.DropTable(
                name: "IntegrationTargets");

            migrationBuilder.DropTable(
                name: "Locations");

            migrationBuilder.DropTable(
                name: "NetworkAlerts");

            migrationBuilder.DropTable(
                name: "OperationsIncidents");

            migrationBuilder.DropTable(
                name: "PhysicalSpaces");

            migrationBuilder.DropTable(
                name: "PolicyTemplates");

            migrationBuilder.DropTable(
                name: "PortObservations");

            migrationBuilder.DropTable(
                name: "RadioObservations");

            migrationBuilder.DropTable(
                name: "Recommendations");

            migrationBuilder.DropTable(
                name: "ReportDefinitions");

            migrationBuilder.DropTable(
                name: "SecurityObservations");

            migrationBuilder.DropTable(
                name: "ServicePathProbes");

            migrationBuilder.DropTable(
                name: "ShadowPredictions");

            migrationBuilder.DropTable(
                name: "SitePermissions");

            migrationBuilder.DropTable(
                name: "TelemetryRollups");

            migrationBuilder.DropTable(
                name: "TelemetrySnapshots");

            migrationBuilder.DropTable(
                name: "TopologyLinks");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}
