using Jcf.NetworkManager.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Jcf.NetworkManager.Data;

public sealed class NetworkDbContext(DbContextOptions<NetworkDbContext> options) : IdentityDbContext<OperatorUser,IdentityRole<Guid>,Guid>(options)
{
    public DbSet<HardwareAcceptanceRun> HardwareAcceptanceRuns => Set<HardwareAcceptanceRun>();
    public DbSet<DeviceCredential> DeviceCredentials => Set<DeviceCredential>();
    public DbSet<DevicePlacement> DevicePlacements => Set<DevicePlacement>();
    public DbSet<PhysicalSpace> PhysicalSpaces => Set<PhysicalSpace>();
    public DbSet<RadioObservation> RadioObservations => Set<RadioObservation>();
    public DbSet<ClientSession> ClientSessions => Set<ClientSession>();
    public DbSet<PortObservation> PortObservations => Set<PortObservation>();
    public DbSet<TopologyLink> TopologyLinks => Set<TopologyLink>();
    public DbSet<PolicyTemplate> PolicyTemplates => Set<PolicyTemplate>();
    public DbSet<ConfigurationSnapshot> ConfigurationSnapshots => Set<ConfigurationSnapshot>();
    public DbSet<ComplianceFinding> ComplianceFindings => Set<ComplianceFinding>();
    public DbSet<OperationsIncident> OperationsIncidents => Set<OperationsIncident>();
    public DbSet<SecurityObservation> SecurityObservations => Set<SecurityObservation>();
    public DbSet<ServicePathProbe> ServicePathProbes => Set<ServicePathProbe>();
    public DbSet<TelemetryRollup> TelemetryRollups => Set<TelemetryRollup>();
    public DbSet<ReportDefinition> ReportDefinitions => Set<ReportDefinition>();
    public DbSet<ShadowPrediction> ShadowPredictions => Set<ShadowPrediction>();
    public DbSet<IntegrationTarget> IntegrationTargets => Set<IntegrationTarget>();
    public DbSet<SitePermission> SitePermissions => Set<SitePermission>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<DeviceCapability> DeviceCapabilities => Set<DeviceCapability>();
    public DbSet<ConfigProposal> ConfigProposals => Set<ConfigProposal>();
    public DbSet<ConfigExecutionEvent> ConfigExecutionEvents => Set<ConfigExecutionEvent>();
    public DbSet<NetworkAlert> NetworkAlerts => Set<NetworkAlert>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<ClusterMember> ClusterMembers => Set<ClusterMember>();
    public DbSet<TelemetrySnapshot> TelemetrySnapshots => Set<TelemetrySnapshot>();
    public DbSet<AnalysisRun> AnalysisRuns => Set<AnalysisRun>();
    public DbSet<Recommendation> Recommendations => Set<Recommendation>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AnalysisSchedule> AnalysisSchedules => Set<AnalysisSchedule>();

    private void EnforceAppendOnlyAudit()
    {
        if(ChangeTracker.Entries<AuditEvent>().Any(e=>e.State is EntityState.Modified or EntityState.Deleted) ||
           ChangeTracker.Entries<ConfigExecutionEvent>().Any(e=>e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit and change execution records are append-only.");
    }
    public override int SaveChanges() {EnforceAppendOnlyAudit();return base.SaveChanges();}
    public override int SaveChanges(bool acceptAllChangesOnSuccess) {EnforceAppendOnlyAudit();return base.SaveChanges(acceptAllChangesOnSuccess);}
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken=default) {
        EnforceAppendOnlyAudit();return base.SaveChangesAsync(cancellationToken);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,CancellationToken cancellationToken=default) {
        EnforceAppendOnlyAudit();return base.SaveChangesAsync(acceptAllChangesOnSuccess,cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<HardwareAcceptanceRun>().HasIndex(x=>new{x.DeviceId,x.CompletedUtc});
        model.Entity<DevicePlacement>().HasIndex(x=>new{x.DeviceId,x.ThroughUtc});
        model.Entity<PhysicalSpace>().HasIndex(x=>new{x.LocationId,x.ParentSpaceId,x.Name}).IsUnique();
        model.Entity<RadioObservation>().HasIndex(x=>new{x.DeviceId,x.AtUtc});
        model.Entity<ClientSession>().HasIndex(x=>new{x.DeviceId,x.LastSeenUtc});
        model.Entity<ClientSession>().HasIndex(x=>x.ClientHash);
        model.Entity<PortObservation>().HasIndex(x=>new{x.DeviceId,x.Port,x.AtUtc});
        model.Entity<TopologyLink>().HasIndex(x=>new{x.DeviceId,x.LocalPort}).IsUnique();
        model.Entity<ConfigurationSnapshot>().HasIndex(x=>new{x.DeviceId,x.CapturedUtc});
        model.Entity<ComplianceFinding>().HasIndex(x=>new{x.DeviceId,x.Rule,x.State});
        model.Entity<SecurityObservation>().HasIndex(x=>x.ObservedUtc);
        model.Entity<TelemetryRollup>().HasIndex(x=>new{x.DeviceId,x.BucketUtc,x.BucketMinutes}).IsUnique();
        model.Entity<ShadowPrediction>().HasIndex(x=>new{x.DeviceId,x.AtUtc});
        model.Entity<RadioObservation>().Property(x=>x.UtilizationPct).HasPrecision(6,2);
        model.Entity<RadioObservation>().Property(x=>x.NoiseFloorDbm).HasPrecision(6,2);
        model.Entity<PortObservation>().Property(x=>x.UtilizationPct).HasPrecision(6,2);
        model.Entity<ShadowPrediction>().Property(x=>x.CongestionRisk).HasPrecision(6,3);
        model.Entity<SitePermission>().HasIndex(x=>new {x.UserId,x.LocationId,x.Role}).IsUnique();
        model.Entity<AuditEvent>().Property(x=>x.Outcome).HasMaxLength(200);

        model.Entity<DeviceGroup>().HasIndex(d => d.Name).IsUnique();
        model.Entity<GroupMembership>().HasIndex(d => new { d.GroupId, d.DeviceId }).IsUnique();
        model.Entity<DeviceCapability>().HasIndex(d => new { d.DeviceId, d.Operation }).IsUnique();
        model.Entity<DeviceCapability>().Property(d => d.State).HasConversion<string>();
        model.Entity<ConfigProposal>().Property(d => d.State).HasConversion<string>();
        model.Entity<ConfigProposal>().Property(d => d.Action).HasConversion<string>();
        model.Entity<ConfigProposal>().HasIndex(d => new { d.DeviceId, d.State });
        model.Entity<ConfigExecutionEvent>().HasIndex(d => new { d.ProposalId, d.AtUtc });
        model.Entity<NetworkAlert>().HasIndex(d => d.Fingerprint);
        model.Entity<TelemetrySnapshot>().Property(d=>d.ChannelUtilizationPct).HasPrecision(6,2);
        model.Entity<TelemetrySnapshot>().Property(d=>d.NoiseFloorDbm).HasPrecision(6,2);
        model.Entity<TelemetrySnapshot>().Property(d=>d.ClientRetryPct).HasPrecision(6,2);
        model.Entity<Device>().HasIndex(d => d.ManagementIp).IsUnique();
        model.Entity<Device>().HasIndex(d => d.SecretRef);
        model.Entity<Location>().HasIndex(d => new { d.ParentId, d.Name }).IsUnique();
        model.Entity<ClusterMember>().HasIndex(d => new { d.ConductorDeviceId, d.Name }).IsUnique();
        model.Entity<TelemetrySnapshot>().HasIndex(d => new { d.DeviceId, d.AtUtc });
        model.Entity<Recommendation>().HasIndex(d => d.Fingerprint);
        model.Entity<AuditEvent>().HasIndex(d => d.AtUtc);
        model.Entity<Recommendation>().Property(d => d.State).HasConversion<string>();
        model.Entity<Device>().Property(d => d.Kind).HasConversion<string>();
        // Enum names stored as text: bounded columns (not nvarchar(max)) so they stay in-row and indexable.
        model.Entity<Device>().Property(d => d.Kind).HasMaxLength(48);
        model.Entity<ConfigProposal>().Property(d => d.Action).HasMaxLength(48);
        model.Entity<ConfigProposal>().Property(d => d.State).HasMaxLength(48);
        // Optimistic concurrency on the workflow state: UPDATE ... WHERE State=<value read>. Two concurrent
        // Execute (or approve) requests cannot both move the same proposal; the loser gets a conflict, not a second device write.
        model.Entity<ConfigProposal>().Property(d => d.State).IsConcurrencyToken();
        model.Entity<DeviceCapability>().Property(d => d.State).HasMaxLength(48);
        model.Entity<Recommendation>().Property(d => d.State).HasMaxLength(48);
        // Retention purges filter on time alone; these indexes keep MaintenanceWorker's batched deletes from scanning whole tables.
        model.Entity<TelemetrySnapshot>().HasIndex(x=>x.AtUtc);
        model.Entity<RadioObservation>().HasIndex(x=>x.AtUtc);
        model.Entity<PortObservation>().HasIndex(x=>x.AtUtc);
        model.Entity<ClientSession>().HasIndex(x=>x.LastSeenUtc);
        model.Entity<ServicePathProbe>().HasIndex(x=>x.AtUtc);
        // Explicit precision for every measured value (SQL Server would otherwise default to decimal(18,2) with a warning).
        model.Entity<RadioObservation>().Property(x=>x.PowerDbm).HasPrecision(6,2);
        model.Entity<RadioObservation>().Property(x=>x.ErrorPct).HasPrecision(6,2);
        model.Entity<ClientSession>().Property(x=>x.RssiDbm).HasPrecision(6,2);
        model.Entity<ClientSession>().Property(x=>x.SnrDb).HasPrecision(6,2);
        model.Entity<ClientSession>().Property(x=>x.RetryPct).HasPrecision(6,2);
        model.Entity<PortObservation>().Property(x=>x.PoeWatts).HasPrecision(7,2);
        model.Entity<TelemetryRollup>().Property(x=>x.AverageUtilizationPct).HasPrecision(6,2);
        model.Entity<TelemetryRollup>().Property(x=>x.MaxUtilizationPct).HasPrecision(6,2);
        model.Entity<TelemetryRollup>().Property(x=>x.AverageNoiseFloorDbm).HasPrecision(6,2);
        // 0.9: real/simulated equipment in one inventory, device TLS pinning and the encrypted credential vault.
        model.Entity<DeviceCredential>().HasIndex(x=>x.Reference).IsUnique();
    }
}
