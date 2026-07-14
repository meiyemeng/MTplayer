using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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
    public void Design_time_factory_creates_npgsql_context_without_secrets_or_external_host()
    {
        var factory = new ApiDbContextFactory();
        using var db = factory.CreateDbContext([]);

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
        var connectionString = db.Database.GetConnectionString();
        Assert.NotNull(connectionString);
        Assert.Contains("Host=127.0.0.1", connectionString, StringComparison.Ordinal);
        Assert.Contains("Port=1", connectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", connectionString, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(db.Model.FindEntityType(typeof(UserEntity)));
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
    public void Sync_record_version_is_a_concurrency_token()
    {
        using var db = TestDb.CreateContext();
        var property = db.Model
            .FindEntityType(typeof(SyncRecordEntity))!
            .FindProperty(nameof(SyncRecordEntity.Version))!;

        Assert.True(property.IsConcurrencyToken);
    }

    [Fact]
    public void Change_cursor_is_generated_from_the_global_database_sequence()
    {
        using var db = TestDb.CreateContext();
        var property = db.Model
            .FindEntityType(typeof(ChangeLogEntity))!
            .FindProperty(nameof(ChangeLogEntity.Cursor))!;

        Assert.NotNull(db.Model.FindSequence("change_cursor_seq"));
        Assert.Equal(ValueGenerated.OnAdd, property.ValueGenerated);
        Assert.Equal("nextval('\"change_cursor_seq\"')", property.GetDefaultValueSql());
    }

    [Fact]
    public void Operational_lookup_columns_are_indexed()
    {
        using var db = TestDb.CreateContext();

        AssertIndex(db, typeof(DeviceSessionEntity), nameof(DeviceSessionEntity.RefreshTokenHash));
        AssertIndex(db, typeof(ChangeLogEntity), nameof(ChangeLogEntity.UserId), nameof(ChangeLogEntity.Cursor));
        AssertIndex(
            db,
            typeof(MailOutboxEntity),
            nameof(MailOutboxEntity.Status),
            nameof(MailOutboxEntity.NextAttemptAtUtc));
        AssertNoIndex(db, typeof(MailOutboxEntity), nameof(MailOutboxEntity.Status));
        AssertIndex(db, typeof(EmailTokenEntity), nameof(EmailTokenEntity.ExpiresAtUtc));
    }

    [Fact]
    public void Server_generated_timestamps_default_to_current_timestamp()
    {
        using var db = TestDb.CreateContext();

        AssertCurrentTimestampDefault(db, typeof(UserEntity), nameof(UserEntity.CreatedAtUtc));
        AssertCurrentTimestampDefault(db, typeof(DeviceSessionEntity), nameof(DeviceSessionEntity.CreatedAtUtc));
        AssertCurrentTimestampDefault(db, typeof(DeviceSessionEntity), nameof(DeviceSessionEntity.LastActivityAtUtc));
        AssertCurrentTimestampDefault(db, typeof(EmailTokenEntity), nameof(EmailTokenEntity.CreatedAtUtc));
        AssertCurrentTimestampDefault(db, typeof(MailOutboxEntity), nameof(MailOutboxEntity.CreatedAtUtc));
        AssertCurrentTimestampDefault(db, typeof(MailOutboxEntity), nameof(MailOutboxEntity.NextAttemptAtUtc));
        AssertCurrentTimestampDefault(db, typeof(SystemSettingEntity), nameof(SystemSettingEntity.UpdatedAtUtc));
        AssertCurrentTimestampDefault(db, typeof(AuditLogEntity), nameof(AuditLogEntity.CreatedAtUtc));

        Assert.Null(db.Model
            .FindEntityType(typeof(SyncRecordEntity))!
            .FindProperty(nameof(SyncRecordEntity.ModifiedAtUtc))!
            .GetDefaultValueSql());
        Assert.Null(db.Model
            .FindEntityType(typeof(ChangeLogEntity))!
            .FindProperty(nameof(ChangeLogEntity.ModifiedAtUtc))!
            .GetDefaultValueSql());
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

        var sequence = Assert.Single(migration.UpOperations.OfType<CreateSequenceOperation>());
        Assert.Equal("change_cursor_seq", sequence.Name);

        var tableOperations = migration.UpOperations.OfType<CreateTableOperation>().ToArray();
        Assert.Equal(
            "nextval('\"change_cursor_seq\"')",
            FindColumn(tableOperations, "change_log", nameof(ChangeLogEntity.Cursor)).DefaultValueSql);
        Assert.Equal(
            "CURRENT_TIMESTAMP",
            FindColumn(tableOperations, "users", nameof(UserEntity.CreatedAtUtc)).DefaultValueSql);
        Assert.Equal(
            "CURRENT_TIMESTAMP",
            FindColumn(tableOperations, "system_settings", nameof(SystemSettingEntity.UpdatedAtUtc)).DefaultValueSql);

        var outboxIndexes = migration.UpOperations
            .OfType<CreateIndexOperation>()
            .Where(operation => operation.Table == "mail_outbox")
            .ToArray();
        Assert.Contains(outboxIndexes, operation =>
            operation.Columns.SequenceEqual(
                [nameof(MailOutboxEntity.Status), nameof(MailOutboxEntity.NextAttemptAtUtc)]));
        Assert.DoesNotContain(outboxIndexes, operation =>
            operation.Columns.SequenceEqual([nameof(MailOutboxEntity.Status)]));
    }

    private static void AssertIndex(ApiDbContext db, Type entityType, params string[] propertyNames)
    {
        var entity = db.Model.FindEntityType(entityType)!;
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }

    private static void AssertNoIndex(ApiDbContext db, Type entityType, params string[] propertyNames)
    {
        var entity = db.Model.FindEntityType(entityType)!;
        Assert.DoesNotContain(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }

    private static void AssertCurrentTimestampDefault(ApiDbContext db, Type entityType, string propertyName)
    {
        var property = db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!;
        Assert.Equal("CURRENT_TIMESTAMP", property.GetDefaultValueSql());
    }

    private static AddColumnOperation FindColumn(
        IEnumerable<CreateTableOperation> tables,
        string tableName,
        string columnName) =>
        tables.Single(table => table.Name == tableName)
            .Columns.Single(column => column.Name == columnName);

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
