using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MTPlayer.Server.Data;
using Xunit;

namespace MTPlayer.Server.Tests.Data;

public sealed class ApiDbContextTests
{
    [Fact]
    public void User_email_has_unique_normalized_index()
    {
        using var db = TestDb.CreateContext();
        var entity = db.Model.FindEntityType(typeof(UserEntity))!;

        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Single().Name == nameof(UserEntity.NormalizedEmail));
    }

    [Fact]
    public void Context_exposes_the_eight_planned_business_tables()
    {
        using var db = TestDb.CreateContext();

        Assert.NotNull(db.Users);
        Assert.NotNull(db.DeviceSessions);
        Assert.NotNull(db.SyncRecords);
        Assert.NotNull(db.ChangeLog);
        Assert.NotNull(db.EmailTokens);
        Assert.NotNull(db.MailOutbox);
        Assert.NotNull(db.SystemSettings);
        Assert.NotNull(db.AuditLog);
    }

    [Fact]
    public void Sync_record_uses_composite_key_and_jsonb_payload()
    {
        using var db = TestDb.CreateContext();
        var entity = db.Model.FindEntityType(typeof(SyncRecordEntity))!;

        Assert.Equal(
            [nameof(SyncRecordEntity.UserId), nameof(SyncRecordEntity.Id), nameof(SyncRecordEntity.Kind)],
            entity.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal("jsonb", entity.FindProperty(nameof(SyncRecordEntity.PayloadJson))!.GetColumnType());
    }

    [Fact]
    public void Operational_lookup_columns_are_indexed()
    {
        using var db = TestDb.CreateContext();

        AssertIndex(db, typeof(DeviceSessionEntity), nameof(DeviceSessionEntity.RefreshTokenHash));
        AssertIndex(db, typeof(ChangeLogEntity), nameof(ChangeLogEntity.UserId), nameof(ChangeLogEntity.Cursor));
        AssertIndex(db, typeof(MailOutboxEntity), nameof(MailOutboxEntity.Status));
        AssertIndex(db, typeof(EmailTokenEntity), nameof(EmailTokenEntity.ExpiresAtUtc));
    }

    [Fact]
    public void Initial_migration_contains_the_eight_business_tables_and_unique_email_index()
    {
        using var db = TestDb.CreateContext();
        var migrations = db.GetService<IMigrationsAssembly>();
        var migrationId = Assert.Single(migrations.Migrations.Keys);
        var migration = migrations.CreateMigration(
            migrations.Migrations[migrationId],
            db.Database.ProviderName!);

        Assert.EndsWith("_InitialServerSchema", migrationId, StringComparison.Ordinal);
        Assert.Equal(
            [
                "audit_log",
                "change_log",
                "device_sessions",
                "email_tokens",
                "mail_outbox",
                "sync_records",
                "system_settings",
                "users",
            ],
            migration.UpOperations
                .OfType<CreateTableOperation>()
                .Select(operation => operation.Name)
                .Order());

        Assert.Contains(
            migration.UpOperations.OfType<CreateIndexOperation>(),
            operation => operation.Table == "users" &&
                operation.IsUnique &&
                operation.Columns.SequenceEqual([nameof(UserEntity.NormalizedEmail)]));
    }

    private static void AssertIndex(ApiDbContext db, Type entityType, params string[] propertyNames)
    {
        var entity = db.Model.FindEntityType(entityType)!;
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }

    private static class TestDb
    {
        public static ApiDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ApiDbContext>()
                .UseNpgsql("Host=localhost;Database=mtplayer;Username=mtplayer")
                .Options;

            return new ApiDbContext(options);
        }
    }
}
