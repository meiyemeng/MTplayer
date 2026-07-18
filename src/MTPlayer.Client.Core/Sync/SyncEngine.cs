using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MTPlayer.Client.Core.Account;
using MTPlayer.Client.Core.Library;
using MTPlayer.Client.Core.Settings;
using MTPlayer.Contracts;

namespace MTPlayer.Client.Core.Sync;

public interface ISyncApiClient
{
    Task<IReadOnlyList<SyncPushResult>> PushAsync(
        SyncPushRequest request,
        CancellationToken cancellationToken = default);
    Task<SyncPullResponse> PullAsync(
        long cursor,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class AccountSyncApiClient(IAccountApiClient account) : ISyncApiClient
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SyncPushResult>> PushAsync(
        SyncPushRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await account.SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "api/v1/sync/push")
            {
                Content = JsonContent.Create(request, options: WebJson),
            },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<SyncPushResult>>(WebJson, cancellationToken) ?? [];
    }

    public async Task<SyncPullResponse> PullAsync(
        long cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var uri = $"api/v1/sync/pull?cursor={cursor.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        using var response = await account.SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, uri),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SyncPullResponse>(WebJson, cancellationToken) ??
            throw new InvalidDataException("Sync pull response was empty.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = "sync_request_failed";
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("code", out var property))
            {
                code = property.GetString() ?? code;
            }
        }
        catch (JsonException)
        {
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new AccountApiException(response.StatusCode, code);
        }

        throw new InvalidDataException($"Sync API rejected the request ({(int)response.StatusCode}, {code}).");
    }
}

public enum SyncRunStatus
{
    Success,
    Offline,
    AuthenticationRequired,
}

public sealed record SyncRunResult(SyncRunStatus Status, int Pushed, int Pulled, int Pending);

public sealed class SyncEngine(
    ISyncApiClient api,
    SyncQueueStore queueStore,
    ILibraryStore libraryStore,
    IClientSettingsStore settingsStore,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SyncRunResult> SynchronizeAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var upload = await UploadAsync(deviceId, cancellationToken);
        if (upload.Status != SyncRunStatus.Success) return upload;
        var download = await DownloadAsync(deviceId, cancellationToken);
        return new SyncRunResult(download.Status, upload.Pushed, download.Pulled, download.Pending);
    }

    public async Task<SyncRunResult> UploadAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);
        var pushed = 0;
        var queue = await queueStore.LoadAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var batch = queue.Items.Where(item => item.NextAttemptAtUtc <= now).Take(200).ToArray();
        if (batch.Length > 0)
        {
            try
            {
                var results = await api.PushAsync(
                    new SyncPushRequest(deviceId, batch.Select(item => item.Mutation).ToArray()),
                    cancellationToken);
                if (results.Count != batch.Length)
                {
                    throw new InvalidDataException("Sync push response count does not match the request.");
                }

                var serverChanges = new List<SyncMutation>();
                var completed = new HashSet<Guid>();
                for (var index = 0; index < batch.Length; index++)
                {
                    var item = batch[index];
                    var result = results[index];
                    if (result.Id != item.Mutation.Id)
                    {
                        throw new InvalidDataException("Sync push response order does not match the request.");
                    }

                    if (result.Accepted)
                    {
                        serverChanges.Add(item.Mutation with
                        {
                            BaseVersion = result.Version,
                            ModifiedAtUtc = result.ServerModifiedAtUtc,
                        });
                        completed.Add(item.QueueId);
                        pushed++;
                    }
                    else if (result.ErrorCode == "version_conflict" && result.Current is not null)
                    {
                        serverChanges.Add(result.Current);
                        completed.Add(item.QueueId);
                    }
                    else
                    {
                        ScheduleRetry(queue, item.QueueId, now);
                    }
                }

                if (serverChanges.Count > 0)
                {
                    await ApplyChangesAsync(serverChanges, cursor: null, cancellationToken);
                }

                queue.Items.RemoveAll(item => completed.Contains(item.QueueId));
                await queueStore.SaveAsync(queue, cancellationToken);
            }
            catch (Exception exception) when (IsOffline(exception, cancellationToken))
            {
                foreach (var item in batch)
                {
                    ScheduleRetry(queue, item.QueueId, now);
                }

                await queueStore.SaveAsync(queue, cancellationToken);
                return new SyncRunResult(SyncRunStatus.Offline, 0, 0, queue.Items.Count);
            }
            catch (AccountApiException exception) when (exception.Code == "authentication_required" ||
                exception.Code == "invalid_refresh_token")
            {
                return new SyncRunResult(SyncRunStatus.AuthenticationRequired, 0, 0, queue.Items.Count);
            }
        }

        queue = await queueStore.LoadAsync(cancellationToken);
        return new SyncRunResult(SyncRunStatus.Success, pushed, 0, queue.Items.Count);
    }

    public async Task<SyncRunResult> DownloadAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);
        var pulled = 0;
        var settings = await settingsStore.LoadAsync(cancellationToken);
        while (true)
        {
            SyncPullResponse page;
            try
            {
                page = await api.PullAsync(settings.SyncCursor, 500, cancellationToken);
            }
            catch (Exception exception) when (IsOffline(exception, cancellationToken))
            {
                var pending = (await queueStore.LoadAsync(cancellationToken)).Items.Count;
                return new SyncRunResult(SyncRunStatus.Offline, 0, pulled, pending);
            }
            catch (AccountApiException exception) when (exception.Code == "authentication_required" ||
                exception.Code == "invalid_refresh_token")
            {
                var pending = (await queueStore.LoadAsync(cancellationToken)).Items.Count;
                return new SyncRunResult(SyncRunStatus.AuthenticationRequired, 0, pulled, pending);
            }

            if (page.Cursor < settings.SyncCursor)
            {
                throw new InvalidDataException("Sync cursor moved backwards.");
            }

            if (page.Changes.Count > 500 ||
                page.Changes.Count > 0 && page.Cursor == settings.SyncCursor)
            {
                throw new InvalidDataException("Sync pull page is invalid or did not advance the cursor.");
            }

            await ApplyChangesAsync(page.Changes, page.Cursor, cancellationToken);
            settings.SyncCursor = page.Cursor;
            pulled += page.Changes.Count;
            if (page.Changes.Count < 500)
            {
                break;
            }
        }

        var queue = await queueStore.LoadAsync(cancellationToken);
        return new SyncRunResult(SyncRunStatus.Success, 0, pulled, queue.Items.Count);
    }

    private static void ValidateDeviceId(Guid deviceId)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));
    }

    public async Task MergeGuestDataAsync(CancellationToken cancellationToken = default)
    {
        var library = await libraryStore.LoadAsync(cancellationToken);
        var settings = await settingsStore.LoadAsync(cancellationToken);
        library.Favorites = library.Favorites.Where(item => !item.IsDeleted)
            .GroupBy(item => (item.SourceKey, item.ContentId))
            .Select(group => group.MaxBy(item => item.ModifiedAtUtc)!)
            .ToList();
        library.PlaybackHistory = library.PlaybackHistory.Where(item => !item.IsDeleted)
            .GroupBy(item => (item.SourceKey, item.ContentId))
            .Select(group => group.MaxBy(item => item.WatchedAtUtc)!)
            .ToList();
        library.SkipMarkers = library.SkipMarkers.Where(item => !item.IsDeleted)
            .GroupBy(item => (item.SourceKey, item.ContentId, item.InterfaceKey, item.LineName))
            .Select(group => group.MaxBy(item => item.ModifiedAtUtc)!)
            .ToList();
        settings.ConfigurationGroups = settings.ConfigurationGroups.Where(item => !item.IsDeleted)
            .GroupBy(item => NormalizeAddress(item.Address), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var newest = group.MaxBy(item => item.ModifiedAtUtc)!;
                return newest with { IsEnabled = group.Any(item => item.IsEnabled) };
            })
            .ToList();
        await libraryStore.SaveAsync(library, cancellationToken);
        await settingsStore.SaveAsync(settings, cancellationToken);

        foreach (var mutation in library.Favorites.Select(SyncMapper.ToMutation)
            .Concat(library.PlaybackHistory.Select(SyncMapper.ToMutation))
            .Concat(library.SkipMarkers.Select(SyncMapper.ToMutation))
            .Concat(settings.ConfigurationGroups.Select(SyncMapper.ToMutation))
            .Concat(PreferenceMutations(settings)))
        {
            await queueStore.EnqueueAsync(mutation with { BaseVersion = 0 }, cancellationToken);
        }
    }

    private async Task ApplyChangesAsync(
        IReadOnlyList<SyncMutation> changes,
        long? cursor,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0 && cursor is null)
        {
            return;
        }

        var library = await libraryStore.LoadAsync(cancellationToken);
        var settings = await settingsStore.LoadAsync(cancellationToken);
        foreach (var change in changes)
        {
            SyncMapper.Apply(library, settings, change);
        }

        await libraryStore.SaveAsync(library, cancellationToken);
        if (cursor is not null)
        {
            settings.SyncCursor = cursor.Value;
        }

        await settingsStore.SaveAsync(settings, cancellationToken);
    }

    private IEnumerable<SyncMutation> PreferenceMutations(ClientSettings settings)
    {
        var now = _timeProvider.GetUtcNow();
        yield return SyncMapper.Preference(settings, "defaultSpeed", settings.DefaultSpeed, now);
        yield return SyncMapper.Preference(settings, "defaultVolume", settings.DefaultVolume, now);
        yield return SyncMapper.Preference(settings, "useSourceCovers", settings.UseSourceCovers, now);
        yield return SyncMapper.Preference(settings, "posterDensity", settings.PosterDensity, now);
    }

    private static string NormalizeAddress(string address)
    {
        var value = address.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value.TrimEnd('/').ToUpperInvariant();
        }

        return new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Path = uri.AbsolutePath.TrimEnd('/'),
            Query = uri.Query.TrimStart('?'),
            Fragment = string.Empty,
        }.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static void ScheduleRetry(SyncQueueDocument queue, Guid queueId, DateTimeOffset now)
    {
        var index = queue.Items.FindIndex(item => item.QueueId == queueId);
        if (index < 0)
        {
            return;
        }

        var item = queue.Items[index];
        var attempt = item.AttemptCount + 1;
        var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
        queue.Items[index] = item with { AttemptCount = attempt, NextAttemptAtUtc = now + delay };
    }

    private static bool IsOffline(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException { StatusCode: null } or IOException ||
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;
}
