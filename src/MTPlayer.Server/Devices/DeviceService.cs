using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Contracts;
using MTPlayer.Server.Auth;
using MTPlayer.Server.Data;
using MTPlayer.Server.Security;
using MTPlayer.Server.Settings;
using Npgsql;

namespace MTPlayer.Server.Devices;

public enum DeviceCodeStatus
{
    Success,
    InvalidInput,
    PublicUrlNotConfigured,
    Pending,
    SlowDown,
    InvalidOrExpired,
    Disabled,
}

public sealed record DeviceCodeResult(
    DeviceCodeStatus Status,
    DeviceCodeResponse? DeviceCode = null,
    TokenResponse? Tokens = null,
    int RetryAfterSeconds = 0);

public sealed record DeviceSummary(
    Guid Id,
    string Name,
    string Platform,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActivityAtUtc);

public sealed record AdminUserSummary(
    Guid Id,
    string Email,
    string Role,
    bool EmailVerified,
    bool Disabled,
    DateTimeOffset CreatedAtUtc,
    int ActiveDeviceCount,
    string MembershipLevel,
    DateTimeOffset? MembershipExpiresAtUtc,
    string? LastLoginIp,
    string? LastLoginCity,
    DateTimeOffset? LastLoginAtUtc,
    IReadOnlyList<string> ConfigurationSourceAddresses,
    IReadOnlyList<string> VideoInterfaceAddresses,
    IReadOnlyList<string> LiveSourceAddresses);

public sealed class DeviceService(
    ApiDbContext db,
    TokenFactory tokenFactory,
    AuthService authService,
    SystemSettingsService settingsService,
    TimeProvider timeProvider)
{
    public const int PollIntervalSeconds = 5;
    private const int UserCodeLength = 8;
    private const string UserCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    public async Task<DeviceCodeResult> CreateCodeAsync(
        string? deviceNameInput,
        CancellationToken cancellationToken)
    {
        var deviceName = deviceNameInput?.Trim() ?? string.Empty;
        if (deviceName.Length is 0 or > 200)
        {
            return new DeviceCodeResult(DeviceCodeStatus.InvalidInput);
        }

        var publicBaseUrl = await settingsService.GetPublicBaseUrlAsync(cancellationToken);
        if (publicBaseUrl is null)
        {
            return new DeviceCodeResult(DeviceCodeStatus.PublicUrlNotConfigured);
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            var deviceCode = tokenFactory.CreateRefreshToken();
            var userCode = CreateUserCode();
            db.DeviceCodes.Add(new DeviceCodeEntity
            {
                Id = Guid.NewGuid(),
                DeviceCodeHash = tokenFactory.HashToken(deviceCode),
                UserCodeHash = HashUserCode(userCode),
                DeviceName = deviceName,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.Add(CodeLifetime),
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new DeviceCodeResult(
                    DeviceCodeStatus.Success,
                    new DeviceCodeResponse(
                        deviceCode,
                        userCode,
                        new Uri($"{publicBaseUrl}/tv/activate", UriKind.Absolute),
                        now.Add(CodeLifetime),
                        PollIntervalSeconds));
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                })
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique TV device code.");
    }

    public async Task<DeviceCodeStatus> ApproveAsync(
        Guid userId,
        string? userCodeInput,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeUserCode(userCodeInput, out var userCode))
        {
            return DeviceCodeStatus.InvalidInput;
        }

        var hash = HashUserCode(userCode);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var code = await db.DeviceCodes
            .FromSqlInterpolated($"SELECT * FROM device_codes WHERE \"UserCodeHash\" = {hash} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (code is null || code.ExpiresAtUtc <= now || code.ConsumedAtUtc is not null)
        {
            return DeviceCodeStatus.InvalidOrExpired;
        }

        if (code.ApprovedUserId is not null && code.ApprovedUserId != userId)
        {
            return DeviceCodeStatus.InvalidOrExpired;
        }

        code.ApprovedUserId = userId;
        code.ApprovedAtUtc ??= now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DeviceCodeStatus.Success;
    }

    public async Task<DeviceCodeResult> PollAsync(
        string? deviceCodeInput,
        CancellationToken cancellationToken)
    {
        string hash;
        try
        {
            hash = tokenFactory.HashToken(deviceCodeInput!);
        }
        catch (ArgumentException)
        {
            return new DeviceCodeResult(DeviceCodeStatus.InvalidOrExpired);
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var code = await db.DeviceCodes
            .FromSqlInterpolated($"SELECT * FROM device_codes WHERE \"DeviceCodeHash\" = {hash} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (code is null || code.ExpiresAtUtc <= now || code.ConsumedAtUtc is not null)
        {
            return new DeviceCodeResult(DeviceCodeStatus.InvalidOrExpired);
        }

        if (code.LastPolledAtUtc is not null)
        {
            var nextAllowed = code.LastPolledAtUtc.Value.AddSeconds(PollIntervalSeconds);
            if (nextAllowed > now)
            {
                var retryAfter = Math.Max(
                    1,
                    (int)Math.Ceiling((nextAllowed - now).TotalSeconds));
                return new DeviceCodeResult(
                    DeviceCodeStatus.SlowDown,
                    RetryAfterSeconds: retryAfter);
            }
        }

        code.LastPolledAtUtc = now;
        if (code.ApprovedUserId is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DeviceCodeResult(DeviceCodeStatus.Pending);
        }

        var user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM users WHERE \"Id\" = {code.ApprovedUserId.Value} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var requireVerifiedEmail = await ReadBoolSettingAsync(
            "RequireVerifiedEmail",
            true,
            cancellationToken);
        if (user is null || user.Disabled || (requireVerifiedEmail && !user.EmailVerified))
        {
            code.ConsumedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DeviceCodeResult(user?.Disabled == true ? DeviceCodeStatus.Disabled : DeviceCodeStatus.InvalidOrExpired);
        }

        var refreshToken = tokenFactory.CreateRefreshToken();
        var session = new DeviceSessionEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceName = code.DeviceName,
            Platform = code.Platform,
            RefreshTokenHash = tokenFactory.HashToken(refreshToken),
            CreatedAtUtc = now,
            LastActivityAtUtc = now,
            ExpiresAtUtc = now.Add(JwtOptions.RefreshTokenLifetime),
        };
        db.DeviceSessions.Add(session);
        code.ConsumedAtUtc = now;
        var tokens = authService.CreateTokenResponse(user, session.Id, refreshToken, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DeviceCodeResult(DeviceCodeStatus.Success, Tokens: tokens);
    }

    public async Task<IReadOnlyList<DeviceSummary>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await db.DeviceSessions.AsNoTracking()
            .Where(session => session.UserId == userId && session.RevokedAtUtc == null)
            .OrderByDescending(session => session.LastActivityAtUtc)
            .Select(session => new DeviceSummary(
                session.Id,
                session.DeviceName,
                session.Platform,
                session.CreatedAtUtc,
                session.LastActivityAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<bool> RevokeAsync(
        Guid userId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await db.DeviceSessions
            .Where(session =>
                session.Id == deviceId &&
                session.UserId == userId &&
                session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                update => update.SetProperty(session => session.RevokedAtUtc, now),
                cancellationToken) == 1;
    }

    public Task<bool> RevokeAllAsync(Guid userId, CancellationToken cancellationToken) =>
        SetUserStateAsync(userId, disabled: null, revokeAll: true, cancellationToken);

    public Task<bool> SetDisabledAsync(
        Guid userId,
        bool disabled,
        CancellationToken cancellationToken) =>
        SetUserStateAsync(userId, disabled, revokeAll: disabled, cancellationToken);

    public async Task<IReadOnlyList<AdminUserSummary>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var users = await db.Users.AsNoTracking()
            .OrderBy(user => user.Email)
            .Select(user => new
            {
                user.Id,
                user.Email,
                user.Role,
                user.EmailVerified,
                user.Disabled,
                user.CreatedAtUtc,
                ActiveDeviceCount = db.DeviceSessions.Count(session => session.UserId == user.Id && session.RevokedAtUtc == null),
                user.MembershipLevel,
                user.MembershipExpiresAtUtc,
                user.LastLoginIp,
                user.LastLoginCity,
                user.LastLoginAtUtc,
            })
            .ToListAsync(cancellationToken);
        var records = await db.SyncRecords.AsNoTracking()
            .Where(record => !record.IsDeleted &&
                (record.Kind == SyncEntityKind.ConfigurationGroup || record.Kind == SyncEntityKind.Preference))
            .Select(record => new { record.UserId, record.Kind, record.PayloadJson })
            .ToListAsync(cancellationToken);
        var inventories = records.GroupBy(record => record.UserId)
            .ToDictionary(group => group.Key, group => ReadSourceInventory(group.Select(record => (record.Kind, record.PayloadJson))));

        return users.Select(user =>
        {
            var sources = inventories.GetValueOrDefault(user.Id) ?? SourceInventory.Empty;
            return new AdminUserSummary(
                user.Id,
                user.Email,
                user.Role,
                user.EmailVerified,
                user.Disabled,
                user.CreatedAtUtc,
                user.ActiveDeviceCount,
                user.MembershipLevel,
                user.MembershipExpiresAtUtc,
                user.LastLoginIp,
                user.LastLoginCity,
                user.LastLoginAtUtc,
                sources.ConfigurationSources,
                sources.VideoInterfaces,
                sources.LiveSources);
        }).ToArray();
    }

    public async Task<bool> SetMembershipAsync(
        Guid userId,
        string membershipLevel,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var normalized = membershipLevel?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized is not ("free" or "member" or "vip")) return false;
        var user = await db.Users.SingleOrDefaultAsync(value => value.Id == userId, cancellationToken);
        if (user is null) return false;
        user.MembershipLevel = normalized;
        user.MembershipExpiresAtUtc = normalized == "free" ? null : expiresAtUtc;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static SourceInventory ReadSourceInventory(IEnumerable<(SyncEntityKind Kind, string PayloadJson)> records)
    {
        var configurations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var videos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            try
            {
                using var document = JsonDocument.Parse(record.PayloadJson);
                var root = document.RootElement;
                if (record.Kind == SyncEntityKind.ConfigurationGroup)
                {
                    AddAddress(root, "address", configurations);
                    AddAddresses(root, "sites", "api", videos);
                    AddAddresses(root, "lives", "address", lives);
                }
                else if (record.Kind == SyncEntityKind.Preference &&
                    root.TryGetProperty("key", out var key) && key.GetString() == "customLives" &&
                    root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    using var custom = JsonDocument.Parse(value.GetString() ?? "[]");
                    AddAddresses(custom.RootElement, null, "address", lives);
                }
            }
            catch (JsonException) { }
        }
        return new SourceInventory(configurations.ToArray(), videos.ToArray(), lives.ToArray());
    }

    private static void AddAddresses(JsonElement root, string? arrayName, string addressName, HashSet<string> output)
    {
        var array = root;
        if (arrayName is not null && (!root.TryGetProperty(arrayName, out array) || array.ValueKind != JsonValueKind.Array)) return;
        if (array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray()) AddAddress(item, addressName, output);
    }

    private static void AddAddress(JsonElement root, string name, HashSet<string> output)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(value.GetString(), UriKind.Absolute, out var address) &&
            address.Scheme is "http" or "https")
        {
            output.Add(address.ToString());
        }
    }

    private sealed record SourceInventory(
        IReadOnlyList<string> ConfigurationSources,
        IReadOnlyList<string> VideoInterfaces,
        IReadOnlyList<string> LiveSources)
    {
        public static SourceInventory Empty { get; } = new([], [], []);
    }

    private async Task<bool> SetUserStateAsync(
        Guid userId,
        bool? disabled,
        bool revokeAll,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM users WHERE \"Id\" = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return false;
        }

        if (disabled is not null)
        {
            user.Disabled = disabled.Value;
        }

        if (revokeAll)
        {
            await db.DeviceSessions
                .Where(session => session.UserId == userId && session.RevokedAtUtc == null)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(session => session.RevokedAtUtc, now),
                    cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ReadBoolSettingAsync(
        string key,
        bool safeDefault,
        CancellationToken cancellationToken)
    {
        var value = await db.SystemSettings.AsNoTracking()
            .Where(setting => setting.Key == key && !setting.IsEncrypted)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return bool.TryParse(value, out var parsed) ? parsed : safeDefault;
    }

    private static string CreateUserCode()
    {
        Span<char> value = stackalloc char[UserCodeLength];
        for (var index = 0; index < value.Length; index++)
        {
            value[index] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        }

        return new string(value);
    }

    private static bool TryNormalizeUserCode(string? input, out string normalized)
    {
        normalized = input?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length == UserCodeLength &&
            normalized.All(character => UserCodeAlphabet.Contains(character, StringComparison.Ordinal));
    }

    private static string HashUserCode(string userCode)
    {
        var bytes = Encoding.UTF8.GetBytes(userCode);
        try
        {
            return Convert.ToBase64String(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
