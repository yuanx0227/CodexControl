using Microsoft.EntityFrameworkCore;

namespace CodexControl.Relay.Persistence;

public sealed class RelayDbContext(DbContextOptions<RelayDbContext> options) : DbContext(options)
{
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<ControllerEntity> Controllers => Set<ControllerEntity>();
    public DbSet<PairingEntity> Pairings => Set<PairingEntity>();
    public DbSet<PairingSessionEntity> PairingSessions => Set<PairingSessionEntity>();
    public DbSet<PairingRequestEntity> PairingRequests => Set<PairingRequestEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceEntity>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).HasMaxLength(80);
            entity.Property(value => value.Name).HasMaxLength(200);
            entity.Property(value => value.PublicKey).HasMaxLength(200);
        });

        modelBuilder.Entity<ControllerEntity>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).HasMaxLength(80);
            entity.Property(value => value.Name).HasMaxLength(200);
            entity.Property(value => value.PublicKey).HasMaxLength(200);
        });

        modelBuilder.Entity<PairingEntity>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => new { value.DeviceId, value.ControllerId }).IsUnique();
            entity.HasOne(value => value.Device)
                .WithMany(value => value.Pairings)
                .HasForeignKey(value => value.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(value => value.Controller)
                .WithMany(value => value.Pairings)
                .HasForeignKey(value => value.ControllerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PairingSessionEntity>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.DeviceId);
            entity.HasIndex(value => value.ExpiresAt);
            entity.HasIndex(value => value.CodeLookupHash);
            entity.Property(value => value.CodeHash).HasMaxLength(100);
            entity.Property(value => value.CodeLookupHash).HasMaxLength(100);
            entity.HasOne(value => value.Device)
                .WithMany()
                .HasForeignKey(value => value.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PairingRequestEntity>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.SessionId).IsUnique();
            entity.HasIndex(value => value.DeviceId);
            entity.HasIndex(value => value.ExpiresAt);
            entity.Property(value => value.ControllerId).HasMaxLength(80);
            entity.Property(value => value.ControllerName).HasMaxLength(200);
            entity.Property(value => value.PublicKey).HasMaxLength(200);
            entity.Property(value => value.Outcome).HasMaxLength(40);
        });

        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.CreatedAt);
            entity.Property(value => value.EventType).HasMaxLength(120);
            entity.Property(value => value.Outcome).HasMaxLength(80);
        });
    }
}
