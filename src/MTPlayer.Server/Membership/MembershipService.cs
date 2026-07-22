using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Server.Data;

namespace MTPlayer.Server.Membership;

public sealed record MemberSource(string Name, string Address);
public sealed record MemberAdvertisement(string Title, string MediaUrl, string? ClickUrl);
public sealed record MemberPushView(
    Guid Id,
    string Title,
    string Message,
    string MinimumMembershipLevel,
    IReadOnlyList<MemberSource> ConfigurationSources,
    IReadOnlyList<MemberSource> LiveSources,
    string? AndroidVersion,
    string? AndroidDownloadUrl,
    bool ForceAndroidUpdate,
    MemberAdvertisement? StartupAdvertisement,
    MemberAdvertisement? PreRollAdvertisement,
    bool Enabled,
    DateTimeOffset UpdatedAtUtc);
public sealed record MemberPushUpdate(
    string Title,
    string MinimumMembershipLevel,
    IReadOnlyList<MemberSource>? ConfigurationSources,
    IReadOnlyList<MemberSource>? LiveSources,
    bool Enabled,
    string? Message = null,
    string? AndroidVersion = null,
    string? AndroidDownloadUrl = null,
    bool ForceAndroidUpdate = false,
    MemberAdvertisement? StartupAdvertisement = null,
    MemberAdvertisement? PreRollAdvertisement = null);
public sealed record MembershipUpdate(string Level, DateTimeOffset? ExpiresAtUtc);

public sealed class MembershipService(ApiDbContext db, TimeProvider timeProvider)
{
    private static readonly string[] Levels = ["free", "member", "vip"];

    public async Task<bool> SetMembershipAsync(Guid userId, MembershipUpdate update, CancellationToken cancellationToken)
    {
        var level = NormalizeLevel(update.Level);
        var user = await db.Users.SingleOrDefaultAsync(value => value.Id == userId, cancellationToken);
        if (user is null) return false;
        user.MembershipLevel = level;
        user.MembershipExpiresAtUtc = level == "free" ? null : update.ExpiresAtUtc;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<MemberPushView>> ListAllAsync(CancellationToken cancellationToken) =>
        (await db.MemberPushes.AsNoTracking().OrderByDescending(push => push.UpdatedAtUtc).ToListAsync(cancellationToken))
            .Select(ToView).ToArray();

    public async Task<IReadOnlyList<MemberPushView>> ListForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var membership = await db.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new { user.MembershipLevel, user.MembershipExpiresAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (membership is null) return [];
        var level = membership.MembershipExpiresAtUtc is not null && membership.MembershipExpiresAtUtc <= now
            ? "free"
            : NormalizeLevel(membership.MembershipLevel);
        var rank = Rank(level);
        var pushes = await db.MemberPushes.AsNoTracking().Where(push => push.Enabled)
            .OrderByDescending(push => push.UpdatedAtUtc).ToListAsync(cancellationToken);
        return pushes.Where(push => Rank(push.MinimumMembershipLevel) <= rank).Select(ToView).ToArray();
    }

    public async Task<MemberPushView> SaveAsync(Guid? id, MemberPushUpdate update, CancellationToken cancellationToken)
    {
        var title = update.Title?.Trim() ?? string.Empty;
        if (title.Length is 0 or > 200) throw new ArgumentException("推送标题长度必须为 1 到 200 个字符。");
        var message = update.Message?.Trim() ?? string.Empty;
        if (message.Length > 2_000) throw new ArgumentException("推送正文不能超过 2000 个字符。");
        var androidVersion = string.IsNullOrWhiteSpace(update.AndroidVersion) ? null : update.AndroidVersion.Trim();
        if (androidVersion?.Length > 64) throw new ArgumentException("Android 版本号不能超过 64 个字符。");
        var androidDownloadUrl = string.IsNullOrWhiteSpace(update.AndroidDownloadUrl) ? null : update.AndroidDownloadUrl.Trim();
        var startupAdvertisement = NormalizeAdvertisement(update.StartupAdvertisement, "Startup advertisement");
        var preRollAdvertisement = NormalizeAdvertisement(update.PreRollAdvertisement, "Pre-roll advertisement");
        if (androidDownloadUrl is not null && (!Uri.TryCreate(androidDownloadUrl, UriKind.Absolute, out var updateUri) || updateUri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Android 更新地址必须是 HTTP 或 HTTPS 地址。");
        if (update.ForceAndroidUpdate && (androidVersion is null || androidDownloadUrl is null))
            throw new ArgumentException("强制更新需要同时填写 Android 版本号和下载地址。");
        var level = NormalizeLevel(update.MinimumMembershipLevel);
        var configurations = NormalizeSources(update.ConfigurationSources);
        var lives = NormalizeSources(update.LiveSources);
        var now = timeProvider.GetUtcNow();
        var entity = id is { } existingId
            ? await db.MemberPushes.SingleOrDefaultAsync(push => push.Id == existingId, cancellationToken)
            : null;
        if (id is not null && entity is null) throw new KeyNotFoundException("未找到推送配置。");
        entity ??= new MemberPushEntity { Id = Guid.NewGuid(), Title = title, CreatedAtUtc = now };
        entity.Title = title;
        entity.Message = message;
        entity.MinimumMembershipLevel = level;
        entity.ConfigurationSourcesJson = JsonSerializer.Serialize(configurations);
        entity.LiveSourcesJson = JsonSerializer.Serialize(lives);
        entity.AndroidVersion = androidVersion;
        entity.AndroidDownloadUrl = androidDownloadUrl;
        entity.ForceAndroidUpdate = update.ForceAndroidUpdate;
        entity.StartupAdvertisementJson = SerializeAdvertisement(startupAdvertisement);
        entity.PreRollAdvertisementJson = SerializeAdvertisement(preRollAdvertisement);
        entity.Enabled = update.Enabled;
        entity.UpdatedAtUtc = now;
        if (db.Entry(entity).State == EntityState.Detached) db.MemberPushes.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(entity);
    }

    public Task<int> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        db.MemberPushes.Where(push => push.Id == id).ExecuteDeleteAsync(cancellationToken);

    private static MemberPushView ToView(MemberPushEntity value) => new(
        value.Id,
        value.Title,
        value.Message,
        value.MinimumMembershipLevel,
        ParseSources(value.ConfigurationSourcesJson),
        ParseSources(value.LiveSourcesJson),
        value.AndroidVersion,
        value.AndroidDownloadUrl,
        value.ForceAndroidUpdate,
        ParseAdvertisement(value.StartupAdvertisementJson),
        ParseAdvertisement(value.PreRollAdvertisementJson),
        value.Enabled,
        value.UpdatedAtUtc);

    private static string NormalizeLevel(string? value)
    {
        var level = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return Levels.Contains(level, StringComparer.Ordinal) ? level : throw new ArgumentException("会员等级仅支持 free、member 或 vip。");
    }

    private static int Rank(string value) => Array.IndexOf(Levels, value) switch { < 0 => 0, var rank => rank };

    private static MemberSource[] NormalizeSources(IReadOnlyList<MemberSource>? sources) => (sources ?? [])
        .Take(500)
        .Select(source => new MemberSource(source.Name?.Trim() ?? string.Empty, source.Address?.Trim() ?? string.Empty))
        .Where(source => source.Name.Length is > 0 and <= 200 &&
            Uri.TryCreate(source.Address, UriKind.Absolute, out var address) &&
            address.Scheme is "http" or "https")
        .DistinctBy(source => source.Address, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static MemberSource[] ParseSources(string json)
    {
        try { return JsonSerializer.Deserialize<MemberSource[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static MemberAdvertisement? NormalizeAdvertisement(MemberAdvertisement? value, string label)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.MediaUrl)) return null;
        var title = value.Title?.Trim() ?? string.Empty;
        var mediaUrl = value.MediaUrl.Trim();
        var clickUrl = string.IsNullOrWhiteSpace(value.ClickUrl) ? null : value.ClickUrl.Trim();
        if (title.Length is 0 or > 200) throw new ArgumentException($"{label} title must be 1 to 200 characters.");
        if (!IsHttpUrl(mediaUrl)) throw new ArgumentException($"{label} media URL must use HTTP or HTTPS.");
        if (clickUrl is not null && !IsHttpUrl(clickUrl)) throw new ArgumentException($"{label} click URL must use HTTP or HTTPS.");
        return new MemberAdvertisement(title, mediaUrl, clickUrl);
    }

    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string? SerializeAdvertisement(MemberAdvertisement? value) => value is null ? null : JsonSerializer.Serialize(value);

    private static MemberAdvertisement? ParseAdvertisement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<MemberAdvertisement>(json); }
        catch (JsonException) { return null; }
    }
}
