using Microsoft.EntityFrameworkCore;

namespace MTPlayer.Server.Data;

public sealed class ApiDbContext(DbContextOptions<ApiDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<DeviceSessionEntity> DeviceSessions => Set<DeviceSessionEntity>();

    public DbSet<ConsumedRefreshTokenEntity> ConsumedRefreshTokens => Set<ConsumedRefreshTokenEntity>();

    public DbSet<SyncRecordEntity> SyncRecords => Set<SyncRecordEntity>();

    public DbSet<ChangeLogEntity> ChangeLog => Set<ChangeLogEntity>();

    public DbSet<EmailTokenEntity> EmailTokens => Set<EmailTokenEntity>();

    public DbSet<MailOutboxEntity> MailOutbox => Set<MailOutboxEntity>();

    public DbSet<SystemSettingEntity> SystemSettings => Set<SystemSettingEntity>();

    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>("change_cursor_seq");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApiDbContext).Assembly);
    }
}
