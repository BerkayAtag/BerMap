using System;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;

namespace BerMap.Infrastructure.Data;

public class BerMapDbContext : DbContext
{
    public BerMapDbContext(DbContextOptions<BerMapDbContext> options) : base(options)
    {
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceSnapshotImmutability();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceSnapshotImmutability();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceSnapshotImmutability()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is Snapshot or SnapshotNode or SnapshotRelationship &&
                entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    "Snapshot rows are immutable. Capture a new snapshot instead of modifying or deleting an existing one.");
            }
        }
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRoleAssignment> UserRoles => Set<UserRoleAssignment>();
    public DbSet<InfrastructureNode> InfrastructureNodes => Set<InfrastructureNode>();
    public DbSet<InfrastructureRelationship> InfrastructureRelationships => Set<InfrastructureRelationship>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<SnapshotNode> SnapshotNodes => Set<SnapshotNode>();
    public DbSet<SnapshotRelationship> SnapshotRelationships => Set<SnapshotRelationship>();
    public DbSet<Simulation> Simulations => Set<Simulation>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<NodeSourceLink> NodeSourceLinks => Set<NodeSourceLink>();
    public DbSet<Collector> Collectors => Set<Collector>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DiscoveryJob> DiscoveryJobs => Set<DiscoveryJob>();
    public DbSet<ReconciliationReview> ReconciliationReviews => Set<ReconciliationReview>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User & Role Mapping
        modelBuilder.Entity<UserRoleAssignment>()
            .HasKey(ur => new { ur.UserId, ur.RoleId });

        modelBuilder.Entity<UserRoleAssignment>()
            .HasOne(ur => ur.User)
            .WithMany(u => u.Roles)
            .HasForeignKey(ur => ur.UserId);

        modelBuilder.Entity<UserRoleAssignment>()
            .HasOne(ur => ur.Role)
            .WithMany(r => r.Users)
            .HasForeignKey(ur => ur.RoleId);

        // InfrastructureNode Mapping
        modelBuilder.Entity<InfrastructureNode>(b =>
        {
            b.HasKey(n => n.Id);
            b.HasIndex(n => n.FQDN);
            b.HasIndex(n => n.NodeType);
            b.HasIndex(n => n.BiosUuid);
            b.HasIndex(n => n.VmInstanceUuid);
            b.HasIndex(n => n.DomainSid);
            b.HasIndex(n => n.MacAddress);
            b.HasIndex(n => n.IsStale);
            b.HasIndex(n => n.LastSeen);
            b.HasIndex(n => n.SnapshotVersion);
            b.Property(n => n.PropertiesJson).HasColumnType("jsonb");
        });

        // InfrastructureRelationship Mapping
        modelBuilder.Entity<InfrastructureRelationship>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.SourceNodeId, r.RelationshipType });
            b.HasIndex(r => new { r.TargetNodeId, r.RelationshipType });
            b.HasIndex(r => new { r.SourceNodeId, r.TargetNodeId, r.RelationshipType }).IsUnique();
            b.HasIndex(r => r.SnapshotVersion);
            b.HasIndex(r => r.SourceSystem);
            b.HasIndex(r => r.IntegrationId);
            b.HasIndex(r => r.IsStale);
            b.Property(r => r.PropertiesJson).HasColumnType("jsonb");

            b.HasOne(r => r.SourceNode)
                .WithMany(n => n.OutgoingRelationships)
                .HasForeignKey(r => r.SourceNodeId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(r => r.TargetNode)
                .WithMany(n => n.IncomingRelationships)
                .HasForeignKey(r => r.TargetNodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Snapshot Mapping
        modelBuilder.Entity<Snapshot>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => s.CreatedAt);
            b.HasIndex(s => s.SnapshotVersion).IsUnique();
            b.Property(s => s.WarningsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SnapshotNode>(b =>
        {
            b.HasKey(n => n.Id);
            b.HasIndex(n => new { n.SnapshotId, n.NodeId });
            b.HasIndex(n => new { n.SnapshotId, n.BiosUuid });
            b.HasIndex(n => new { n.SnapshotId, n.FQDN });
            b.Property(n => n.PropertiesJson).HasColumnType("jsonb");

            b.HasOne(n => n.Snapshot)
                .WithMany(s => s.Nodes)
                .HasForeignKey(n => n.SnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SnapshotRelationship>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.SnapshotId, r.RelationshipId });
            b.HasIndex(r => new { r.SnapshotId, r.SourceNodeId, r.RelationshipType });
            b.HasIndex(r => r.SourceSystem);
            b.Property(r => r.PropertiesJson).HasColumnType("jsonb");

            b.HasOne(r => r.Snapshot)
                .WithMany(s => s.Relationships)
                .HasForeignKey(r => r.SnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Integration Mapping
        modelBuilder.Entity<Integration>(b =>
        {
            b.HasKey(i => i.Id);
            b.HasIndex(i => i.AdapterType);
            b.HasIndex(i => i.Status);
            b.HasIndex(i => new { i.IsEnabled, i.DeletedAt });
            b.Property(i => i.ConfigurationJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<DiscoveryJob>(b =>
        {
            b.HasKey(j => j.Id);
            b.HasIndex(j => new { j.IntegrationId, j.StartedAt });
            b.HasIndex(j => j.Status);
            b.Property(j => j.ErrorsJson).HasColumnType("jsonb");
            b.Property(j => j.WarningsJson).HasColumnType("jsonb");
            b.Property(j => j.StatisticsJson).HasColumnType("jsonb");

            b.HasOne(j => j.Integration)
                .WithMany()
                .HasForeignKey(j => j.IntegrationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // NodeSourceLink Mapping
        modelBuilder.Entity<NodeSourceLink>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.IntegrationId, l.ExternalId }).IsUnique();
            b.HasIndex(l => new { l.NodeId, l.SourceSystem });
            b.HasIndex(l => l.IsStale);
            b.Property(l => l.PropertiesJson).HasColumnType("jsonb");

            b.HasOne(l => l.Integration)
                .WithMany()
                .HasForeignKey(l => l.IntegrationId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(l => l.Node)
                .WithMany(n => n.SourceLinks)
                .HasForeignKey(l => l.NodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ReconciliationReview Mapping
        modelBuilder.Entity<ReconciliationReview>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.Status);
            b.HasIndex(r => r.CreatedAt);
            b.HasIndex(r => new { r.IntegrationId, r.Status });
            b.HasIndex(r => r.Signature);
            b.Property(r => r.DiscoveredNodeJson).HasColumnType("jsonb");
            b.Property(r => r.CandidateNodeIdsJson).HasColumnType("jsonb");
        });

        // AuditLog Mapping
        modelBuilder.Entity<AuditLog>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.UserId);
            b.HasIndex(a => a.CreatedAt);
            b.Property(a => a.DetailsJson).HasColumnType("jsonb");
        });

        // Simulation Mapping
        modelBuilder.Entity<Simulation>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.ParametersJson).HasColumnType("jsonb");
            b.Property(s => s.ResultsJson).HasColumnType("jsonb");
        });
    }
}
