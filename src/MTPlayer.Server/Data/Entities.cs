using MTPlayer.Contracts;

namespace MTPlayer.Server.Data;

public sealed class UserEntity
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    public required string NormalizedEmail { get; set; }

    public required string PasswordHash { get; set; }

    public bool EmailVerified { get; set; }

    public bool Disabled { get; set; }

    public string Role { get; set; } = "user";

    public string MembershipLevel { get; set; } = "free";

    public DateTimeOffset? MembershipExpiresAtUtc { get; set; }

    public string? LastLoginIp { get; set; }

    public string? LastLoginCity { get; set; }

    public DateTimeOffset? LastLoginAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class MemberPushEntity
{
    public Guid Id { get; set; }

    public required string Title { get; set; }

    public string Message { get; set; } = string.Empty;

    public string MinimumMembershipLevel { get; set; } = "member";

    public string ConfigurationSourcesJson { get; set; } = "[]";

    public string LiveSourcesJson { get; set; } = "[]";

    public string? AndroidVersion { get; set; }

    public string? AndroidDownloadUrl { get; set; }

    public bool ForceAndroidUpdate { get; set; }

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class DeviceSessionEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required string DeviceName { get; set; }

    public required string Platform { get; set; }

    public required string RefreshTokenHash { get; set; }

    public long LastSyncCursor { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastActivityAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public UserEntity? User { get; set; }
}

public sealed class DeviceCodeEntity
{
    public Guid Id { get; set; }

    public required string DeviceCodeHash { get; set; }

    public required string UserCodeHash { get; set; }

    public required string DeviceName { get; set; }

    public string Platform { get; set; } = "android-tv";

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? LastPolledAtUtc { get; set; }

    public Guid? ApprovedUserId { get; set; }

    public DateTimeOffset? ApprovedAtUtc { get; set; }

    public DateTimeOffset? ConsumedAtUtc { get; set; }

    public UserEntity? ApprovedUser { get; set; }
}

public sealed class ConsumedRefreshTokenEntity
{
    // Expired replay history is intentionally retained here; Task 9 maintenance owns pruning it.
    public required string TokenHash { get; set; }

    public Guid SessionId { get; set; }

    public DateTimeOffset ConsumedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class SyncRecordEntity
{
    public Guid UserId { get; set; }

    public Guid Id { get; set; }

    public SyncEntityKind Kind { get; set; }

    public long Version { get; set; }

    public DateTimeOffset ModifiedAtUtc { get; set; }

    public bool IsDeleted { get; set; }

    public required string PayloadJson { get; set; }

    public UserEntity? User { get; set; }
}

public sealed class ChangeLogEntity
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public long Cursor { get; set; }

    public Guid RecordId { get; set; }

    public SyncEntityKind Kind { get; set; }

    public long Version { get; set; }

    public DateTimeOffset ModifiedAtUtc { get; set; }

    public bool IsDeleted { get; set; }

    public required string PayloadJson { get; set; }

    public UserEntity? User { get; set; }
}

public sealed class EmailTokenEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required string TokenHash { get; set; }

    public required string Purpose { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? UsedAtUtc { get; set; }

    public UserEntity? User { get; set; }
}

public sealed class MailOutboxEntity
{
    public long Id { get; set; }

    public required string RecipientEmail { get; set; }

    public required string Subject { get; set; }

    public required string BodyHtml { get; set; }

    public string Status { get; set; } = "pending";

    public int AttemptCount { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset NextAttemptAtUtc { get; set; }

    public Guid? ClaimToken { get; set; }

    public DateTimeOffset? ClaimedAtUtc { get; set; }

    public DateTimeOffset? SentAtUtc { get; set; }

    public string? LastError { get; set; }
}

public sealed class SystemSettingEntity
{
    public required string Key { get; set; }

    public string? Value { get; set; }

    public bool IsEncrypted { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class AuditLogEntity
{
    public long Id { get; set; }

    public Guid? UserId { get; set; }

    public required string Action { get; set; }

    public string? Target { get; set; }

    public string? DetailsJson { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public UserEntity? User { get; set; }
}
