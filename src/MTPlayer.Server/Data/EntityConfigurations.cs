using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MTPlayer.Server.Data;

internal sealed class UserEntityConfiguration : IEntityTypeConfiguration<UserEntity>
{
    public void Configure(EntityTypeBuilder<UserEntity> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Email).HasMaxLength(320);
        builder.Property(user => user.NormalizedEmail).HasMaxLength(320);
        builder.Property(user => user.PasswordHash).HasMaxLength(512);
        builder.Property(user => user.Role).HasMaxLength(32).HasDefaultValue("user");
        builder.Property(user => user.CreatedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.HasIndex(user => user.NormalizedEmail).IsUnique();
    }
}

internal sealed class DeviceSessionEntityConfiguration : IEntityTypeConfiguration<DeviceSessionEntity>
{
    public void Configure(EntityTypeBuilder<DeviceSessionEntity> builder)
    {
        builder.ToTable("device_sessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.DeviceName).HasMaxLength(200);
        builder.Property(session => session.Platform).HasMaxLength(50);
        builder.Property(session => session.RefreshTokenHash).HasMaxLength(64);
        builder.Property(session => session.CreatedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(session => session.LastActivityAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(session => session.ExpiresAtUtc);
        builder.HasIndex(session => session.RefreshTokenHash).IsUnique();
        builder.HasIndex(session => new { session.UserId, session.RevokedAtUtc });
        builder.HasOne(session => session.User)
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SyncRecordEntityConfiguration : IEntityTypeConfiguration<SyncRecordEntity>
{
    public void Configure(EntityTypeBuilder<SyncRecordEntity> builder)
    {
        builder.ToTable("sync_records");
        builder.HasKey(record => new { record.UserId, record.Id, record.Kind });
        builder.Property(record => record.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(record => record.Version).IsConcurrencyToken();
        builder.Property(record => record.PayloadJson).HasColumnType("jsonb");
        builder.HasOne(record => record.User)
            .WithMany()
            .HasForeignKey(record => record.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConsumedRefreshTokenEntityConfiguration : IEntityTypeConfiguration<ConsumedRefreshTokenEntity>
{
    public void Configure(EntityTypeBuilder<ConsumedRefreshTokenEntity> builder)
    {
        builder.ToTable("consumed_refresh_tokens");
        builder.HasKey(token => token.TokenHash);
        builder.Property(token => token.TokenHash).HasMaxLength(64);
        builder.HasIndex(token => token.ExpiresAtUtc);
        builder.HasIndex(token => token.SessionId);
    }
}

internal sealed class ChangeLogEntityConfiguration : IEntityTypeConfiguration<ChangeLogEntity>
{
    public void Configure(EntityTypeBuilder<ChangeLogEntity> builder)
    {
        builder.ToTable("change_log");
        builder.HasKey(change => change.Id);
        builder.Property(change => change.Cursor)
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("nextval('\"change_cursor_seq\"')");
        builder.Property(change => change.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(change => change.PayloadJson).HasColumnType("jsonb");
        builder.HasIndex(change => new { change.UserId, change.Cursor }).IsUnique();
        builder.HasOne(change => change.User)
            .WithMany()
            .HasForeignKey(change => change.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EmailTokenEntityConfiguration : IEntityTypeConfiguration<EmailTokenEntity>
{
    public void Configure(EntityTypeBuilder<EmailTokenEntity> builder)
    {
        builder.ToTable("email_tokens");
        builder.HasKey(token => token.Id);
        builder.Property(token => token.TokenHash).HasMaxLength(64);
        builder.Property(token => token.Purpose).HasMaxLength(32);
        builder.Property(token => token.CreatedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasIndex(token => token.ExpiresAtUtc);
        builder.HasOne(token => token.User)
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MailOutboxEntityConfiguration : IEntityTypeConfiguration<MailOutboxEntity>
{
    public void Configure(EntityTypeBuilder<MailOutboxEntity> builder)
    {
        builder.ToTable("mail_outbox");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.RecipientEmail).HasMaxLength(320);
        builder.Property(message => message.Subject).HasMaxLength(500);
        builder.Property(message => message.Status).HasMaxLength(32).HasDefaultValue("pending");
        builder.Property(message => message.CreatedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Property(message => message.NextAttemptAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.HasIndex(message => new { message.Status, message.NextAttemptAtUtc });
        builder.HasIndex(message => new { message.Status, message.ClaimedAtUtc });
    }
}

internal sealed class SystemSettingEntityConfiguration : IEntityTypeConfiguration<SystemSettingEntity>
{
    public void Configure(EntityTypeBuilder<SystemSettingEntity> builder)
    {
        builder.ToTable("system_settings");
        builder.HasKey(setting => setting.Key);
        builder.Property(setting => setting.Key).HasMaxLength(200);
        builder.Property(setting => setting.UpdatedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}

internal sealed class AuditLogEntityConfiguration : IEntityTypeConfiguration<AuditLogEntity>
{
    public void Configure(EntityTypeBuilder<AuditLogEntity> builder)
    {
        builder.ToTable("audit_log");
        builder.HasKey(audit => audit.Id);
        builder.Property(audit => audit.Action).HasMaxLength(200);
        builder.Property(audit => audit.Target).HasMaxLength(500);
        builder.Property(audit => audit.DetailsJson).HasColumnType("jsonb");
        builder.Property(audit => audit.CreatedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.HasIndex(audit => audit.CreatedAtUtc);
        builder.HasOne(audit => audit.User)
            .WithMany()
            .HasForeignKey(audit => audit.UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
