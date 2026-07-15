using System.Text.Json;
using MTPlayer.Client.Core.Library;
using MTPlayer.Client.Core.Settings;
using MTPlayer.Client.Core.Sync;
using MTPlayer.Contracts;
using Xunit;

namespace MTPlayer.Client.Core.Tests.Sync;

public sealed class SyncEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mtplayer-sync-{Guid.NewGuid():N}");

    [Fact]
    public async Task Offline_mutation_survives_restart_and_is_removed_only_after_server_accepts_it()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var path = Path.Combine(_directory, "sync-queue.json");
        using var queue = new SyncQueueStore(path);
        await queue.EnqueueAsync(FavoriteMutation(Guid.NewGuid()));
        var api = new FakeSyncApi { Offline = true };
        var library = new MemoryLibraryStore();
        var settings = new MemorySettingsStore();
        var engine = new SyncEngine(api, queue, library, settings, time);

        var offline = await engine.SynchronizeAsync(Guid.NewGuid());
        Assert.Equal(SyncRunStatus.Offline, offline.Status);
        using (var restarted = new SyncQueueStore(path))
        {
            var pending = Assert.Single((await restarted.LoadAsync()).Items);
            Assert.Equal(1, pending.AttemptCount);
            Assert.Equal(time.GetUtcNow().AddSeconds(5), pending.NextAttemptAtUtc);
        }

        time.Advance(TimeSpan.FromSeconds(6));
        api.Offline = false;
        var online = await engine.SynchronizeAsync(Guid.NewGuid());
        Assert.Equal(SyncRunStatus.Success, online.Status);
        Assert.Empty((await queue.LoadAsync()).Items);
        Assert.Single((await library.LoadAsync()).Favorites);
    }

    [Fact]
    public async Task Partial_push_removes_accepted_item_and_backs_off_rejected_item()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var queue = new SyncQueueStore(Path.Combine(_directory, "partial.json"));
        var accepted = FavoriteMutation(Guid.NewGuid());
        var rejected = FavoriteMutation(Guid.NewGuid());
        await queue.EnqueueAsync(accepted);
        await queue.EnqueueAsync(rejected);
        var api = new FakeSyncApi
        {
            Push = request =>
            [
                new SyncPushResult(request.Mutations[0].Id, 1, request.Mutations[0].ModifiedAtUtc, true, null),
                new SyncPushResult(request.Mutations[1].Id, 0, request.Mutations[1].ModifiedAtUtc, false, "temporary_error"),
            ],
        };
        var engine = new SyncEngine(api, queue, new MemoryLibraryStore(), new MemorySettingsStore(), time);

        var result = await engine.SynchronizeAsync(Guid.NewGuid());

        Assert.Equal(1, result.Pushed);
        var pending = Assert.Single((await queue.LoadAsync()).Items);
        Assert.Equal(rejected.Id, pending.Mutation.Id);
        Assert.Equal(1, pending.AttemptCount);
    }

    [Fact]
    public async Task Pull_pages_until_short_page_and_saves_cursor_after_each_applied_page()
    {
        using var queue = new SyncQueueStore(Path.Combine(_directory, "pages.json"));
        var first = Enumerable.Range(0, 500).Select(index =>
            FavoriteMutation(Guid.NewGuid(), version: 1, title: $"影片-{index}")).ToArray();
        var final = FavoriteMutation(Guid.NewGuid(), version: 1, title: "最后一项");
        var api = new FakeSyncApi();
        api.Pages.Enqueue(new SyncPullResponse(500, first));
        api.Pages.Enqueue(new SyncPullResponse(501, [final]));
        var library = new MemoryLibraryStore();
        var settings = new MemorySettingsStore();
        var engine = new SyncEngine(api, queue, library, settings);

        var result = await engine.SynchronizeAsync(Guid.NewGuid());

        Assert.Equal(501, result.Pulled);
        Assert.Equal(2, api.PullCount);
        Assert.Equal(501, (await settings.LoadAsync()).SyncCursor);
        Assert.Equal(501, (await library.LoadAsync()).Favorites.Count);
    }

    [Fact]
    public async Task Tombstone_is_idempotent_and_removes_item_from_active_library_view()
    {
        var id = Guid.NewGuid();
        var library = new MemoryLibraryStore(new LibrarySnapshot
        {
            Favorites = [Favorite(id, version: 1, title: "待删除")],
        });
        var api = new FakeSyncApi();
        api.Pages.Enqueue(new SyncPullResponse(9,
        [
            new SyncMutation(
                id,
                SyncEntityKind.Favorite,
                2,
                DateTimeOffset.UtcNow,
                true,
                JsonSerializer.SerializeToElement(new { })),
        ]));
        using var queue = new SyncQueueStore(Path.Combine(_directory, "delete.json"));
        var engine = new SyncEngine(api, queue, library, new MemorySettingsStore());

        await engine.SynchronizeAsync(Guid.NewGuid());

        var deleted = Assert.Single((await library.LoadAsync()).Favorites);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(2, deleted.Version);
    }

    [Fact]
    public async Task Cursor_is_not_committed_when_page_application_fails_and_retry_is_idempotent()
    {
        var change = FavoriteMutation(Guid.NewGuid(), version: 1, title: "可重试");
        var api = new FakeSyncApi
        {
            Pull = cursor => cursor == 0
                ? new SyncPullResponse(7, [change])
                : new SyncPullResponse(cursor, []),
        };
        var library = new MemoryLibraryStore();
        var settings = new FailingSettingsStore();
        using var queue = new SyncQueueStore(Path.Combine(_directory, "cursor.json"));
        var engine = new SyncEngine(api, queue, library, settings);

        await Assert.ThrowsAsync<IOException>(() => engine.SynchronizeAsync(Guid.NewGuid()));
        Assert.Equal(0, (await settings.LoadAsync()).SyncCursor);
        Assert.Single((await library.LoadAsync()).Favorites);

        var retried = await engine.SynchronizeAsync(Guid.NewGuid());
        Assert.Equal(SyncRunStatus.Success, retried.Status);
        Assert.Equal(7, (await settings.LoadAsync()).SyncCursor);
        Assert.Single((await library.LoadAsync()).Favorites);
    }

    [Fact]
    public async Task Version_conflict_applies_server_record_and_removes_local_queue_item()
    {
        var id = Guid.NewGuid();
        using var queue = new SyncQueueStore(Path.Combine(_directory, "conflict.json"));
        await queue.EnqueueAsync(FavoriteMutation(id, title: "客户端"));
        var server = FavoriteMutation(id, version: 3, title: "服务端");
        var api = new FakeSyncApi
        {
            Push = request =>
            [
                new SyncPushResult(
                    request.Mutations[0].Id,
                    3,
                    server.ModifiedAtUtc,
                    false,
                    "version_conflict",
                    server),
            ],
        };
        var library = new MemoryLibraryStore();
        var engine = new SyncEngine(api, queue, library, new MemorySettingsStore());

        await engine.SynchronizeAsync(Guid.NewGuid());

        Assert.Empty((await queue.LoadAsync()).Items);
        var favorite = Assert.Single((await library.LoadAsync()).Favorites);
        Assert.Equal("服务端", favorite.Title);
        Assert.Equal(3, favorite.Version);
    }

    [Fact]
    public async Task Guest_merge_uses_union_newest_and_normalized_configuration_dedupe()
    {
        var now = DateTimeOffset.UtcNow;
        var favoriteId = Guid.NewGuid();
        var library = new MemoryLibraryStore(new LibrarySnapshot
        {
            Favorites =
            [
                Favorite(favoriteId, 0, "旧标题") with
                {
                    SourceKey = "source", ContentId = "content", ModifiedAtUtc = now.AddMinutes(-1),
                },
                Favorite(Guid.NewGuid(), 0, "新标题") with
                {
                    SourceKey = "source", ContentId = "content", ModifiedAtUtc = now,
                },
            ],
            PlaybackHistory =
            [
                Playback(now.AddMinutes(-2), 1),
                Playback(now, 2),
            ],
            SkipMarkers =
            [
                Skip(now.AddMinutes(-1), 30),
                Skip(now, 60),
            ],
        });
        var settings = new MemorySettingsStore(new ClientSettings
        {
            ConfigurationGroups =
            [
                new(Guid.NewGuid(), "甲", "HTTPS://EXAMPLE.COM/config/", false, now.AddMinutes(-1)),
                new(Guid.NewGuid(), "乙", "https://example.com/config", true, now),
            ],
        });
        using var queue = new SyncQueueStore(Path.Combine(_directory, "guest.json"));
        var engine = new SyncEngine(new FakeSyncApi(), queue, library, settings);

        await engine.MergeGuestDataAsync();

        Assert.Single((await library.LoadAsync()).Favorites);
        Assert.Equal(2, Assert.Single((await library.LoadAsync()).PlaybackHistory).EpisodeIndex);
        Assert.Equal(60, Assert.Single((await library.LoadAsync()).SkipMarkers).IntroEndSeconds);
        Assert.True(Assert.Single((await settings.LoadAsync()).ConfigurationGroups).IsEnabled);
        Assert.Equal(8, (await queue.LoadAsync()).Items.Count); // 4 media/config records + 4 preferences
    }

    [Fact]
    public async Task Corrupt_queue_is_preserved_and_recovered_as_empty()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "corrupt.json");
        await File.WriteAllTextAsync(path, "{ broken json");
        using var queue = new SyncQueueStore(path);

        Assert.Empty((await queue.LoadAsync()).Items);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_directory, "corrupt.json.corrupt-*"));
    }

    private static SyncMutation FavoriteMutation(Guid id, long version = 0, string title = "影片") =>
        SyncMapper.ToMutation(Favorite(id, version, title));

    private static FavoriteRecord Favorite(Guid id, long version, string title) => new(
        id, "source", id == Guid.Empty ? "content" : id.ToString("N"), "电影", title, "说明", "cover", DateTimeOffset.UtcNow, version);

    private static PlaybackRecord Playback(DateTimeOffset watched, int episode) => new(
        JsonLibraryStore.StableId("playback", "source", "content"),
        "source", "content", "source", "line", episode, 1000, 2000, watched);

    private static SkipMarkerRecord Skip(DateTimeOffset modified, int intro) => new(
        JsonLibraryStore.StableId("skip", "source", "content", "source", "line"),
        "source", "content", "source", "line", intro, 30, modified);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeSyncApi : ISyncApiClient
    {
        public bool Offline { get; set; }
        public Func<SyncPushRequest, IReadOnlyList<SyncPushResult>>? Push { get; init; }
        public Func<long, SyncPullResponse>? Pull { get; init; }
        public Queue<SyncPullResponse> Pages { get; } = new();
        public int PullCount { get; private set; }

        public Task<IReadOnlyList<SyncPushResult>> PushAsync(
            SyncPushRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            IReadOnlyList<SyncPushResult> results = Push?.Invoke(request) ?? request.Mutations.Select(mutation =>
                new SyncPushResult(mutation.Id, mutation.BaseVersion + 1, mutation.ModifiedAtUtc, true, null)).ToArray();
            return Task.FromResult(results);
        }

        public Task<SyncPullResponse> PullAsync(
            long cursor,
            int limit,
            CancellationToken cancellationToken = default)
        {
            PullCount++;
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            return Task.FromResult(Pull?.Invoke(cursor) ??
                (Pages.Count > 0 ? Pages.Dequeue() : new SyncPullResponse(cursor, [])));
        }
    }

    private sealed class MemoryLibraryStore(LibrarySnapshot? initial = null) : ILibraryStore
    {
        private LibrarySnapshot _value = Clone(initial ?? new LibrarySnapshot());

        public Task<LibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Clone(_value));

        public Task SaveAsync(LibrarySnapshot library, CancellationToken cancellationToken = default)
        {
            _value = Clone(library);
            return Task.CompletedTask;
        }

        private static LibrarySnapshot Clone(LibrarySnapshot value) =>
            JsonSerializer.Deserialize<LibrarySnapshot>(JsonSerializer.Serialize(value))!;
    }

    private sealed class MemorySettingsStore(ClientSettings? initial = null) : IClientSettingsStore
    {
        private ClientSettings _value = Clone(initial ?? new ClientSettings());

        public Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Clone(_value));

        public Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default)
        {
            _value = Clone(settings);
            return Task.CompletedTask;
        }

        private static ClientSettings Clone(ClientSettings value) =>
            JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(value))!;
    }

    private sealed class FailingSettingsStore : IClientSettingsStore
    {
        private ClientSettings _value = new();
        private bool _fail = true;

        public Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(_value))!);

        public Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default)
        {
            if (_fail && settings.SyncCursor > 0)
            {
                _fail = false;
                throw new IOException("simulated atomic settings failure");
            }

            _value = JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(settings))!;
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
