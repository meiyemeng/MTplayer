using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Contracts;
using MTPlayer.Server.Data;

namespace MTPlayer.Server.Sync;

public sealed class SyncService(ApiDbContext db, TimeProvider timeProvider)
{
    private static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(180);

    public async Task<IReadOnlyList<SyncPushResult>> PushAsync(
        Guid userId,
        Guid sessionId,
        SyncPushRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DeviceId != sessionId)
        {
            throw new SyncRequestException("device_mismatch");
        }

        if (request.Mutations is null || request.Mutations.Count > SyncPayloadValidator.MaximumMutationCount)
        {
            throw new SyncRequestException("too_many_mutations");
        }

        foreach (var mutation in request.Mutations)
        {
            var error = SyncPayloadValidator.Validate(mutation);
            if (error is not null)
            {
                throw new SyncRequestException(error);
            }
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await EnsureActiveSessionAsync(userId, sessionId, now, cancellationToken);

        var indexed = request.Mutations.Select((mutation, index) => (mutation, index))
            .OrderBy(item => item.mutation.Kind)
            .ThenBy(item => item.mutation.Id)
            .ToArray();
        var results = new SyncPushResult[indexed.Length];
        foreach (var item in indexed)
        {
            await LockRecordAsync(userId, item.mutation, cancellationToken);
            results[item.index] = await ApplyAsync(userId, item.mutation, cancellationToken);
        }

        await db.DeviceSessions
            .Where(session => session.Id == sessionId && session.UserId == userId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(session => session.LastActivityAtUtc, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task<SyncPullResponse> PullAsync(
        Guid userId,
        Guid sessionId,
        long cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (cursor < 0 || limit is < 1 or > 500)
        {
            throw new SyncRequestException("invalid_cursor_or_limit");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EnsureActiveSessionAsync(userId, sessionId, now, cancellationToken);
        var changes = await db.ChangeLog.AsNoTracking()
            .Where(change => change.UserId == userId && change.Cursor > cursor)
            .OrderBy(change => change.Cursor)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var nextCursor = changes.Count == 0 ? cursor : changes[^1].Cursor;
        await db.DeviceSessions
            .Where(session => session.Id == sessionId && session.UserId == userId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(session => session.LastSyncCursor, current => Math.Max(current.LastSyncCursor, nextCursor))
                .SetProperty(session => session.LastActivityAtUtc, now), cancellationToken);
        await CleanupTombstonesAsync(userId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SyncPullResponse(nextCursor, changes.Select(ToMutation).ToArray());
    }

    private async Task<SyncPushResult> ApplyAsync(
        Guid userId,
        SyncMutation mutation,
        CancellationToken cancellationToken)
    {
        var record = await db.SyncRecords.SingleOrDefaultAsync(
            item => item.UserId == userId && item.Id == mutation.Id && item.Kind == mutation.Kind,
            cancellationToken);
        var clientModifiedAtUtc = NormalizeForPostgreSql(mutation.ModifiedAtUtc);
        var decision = Decide(mutation, clientModifiedAtUtc, record);
        if (decision == ConflictDecision.RejectWithServer)
        {
            return new SyncPushResult(
                mutation.Id,
                record!.Version,
                record.ModifiedAtUtc,
                false,
                "version_conflict",
                ToMutation(record));
        }

        var payloadJson = mutation.IsDeleted
            ? "{}"
            : decision == ConflictDecision.MergeFavorite
                ? MergeObjects(record!.PayloadJson, mutation.Payload)
                : mutation.Payload.GetRawText();
        if (record is null)
        {
            record = new SyncRecordEntity
            {
                UserId = userId,
                Id = mutation.Id,
                Kind = mutation.Kind,
                Version = 1,
                ModifiedAtUtc = clientModifiedAtUtc,
                IsDeleted = mutation.IsDeleted,
                PayloadJson = payloadJson,
            };
            db.SyncRecords.Add(record);
        }
        else
        {
            record.Version++;
            record.ModifiedAtUtc = clientModifiedAtUtc;
            record.IsDeleted = mutation.IsDeleted;
            record.PayloadJson = payloadJson;
        }

        db.ChangeLog.Add(new ChangeLogEntity
        {
            UserId = userId,
            RecordId = record.Id,
            Kind = record.Kind,
            Version = record.Version,
            ModifiedAtUtc = record.ModifiedAtUtc,
            IsDeleted = record.IsDeleted,
            PayloadJson = record.PayloadJson,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new SyncPushResult(record.Id, record.Version, record.ModifiedAtUtc, true, null);
    }

    private static ConflictDecision Decide(
        SyncMutation client,
        DateTimeOffset clientModifiedAtUtc,
        SyncRecordEntity? server) =>
        server is null ? ConflictDecision.Accept :
        client.BaseVersion == server.Version ? ConflictDecision.Accept :
        client.Kind == SyncEntityKind.Favorite && !client.IsDeleted && !server.IsDeleted
            ? ConflictDecision.MergeFavorite :
        clientModifiedAtUtc > server.ModifiedAtUtc ? ConflictDecision.Accept :
        ConflictDecision.RejectWithServer;

    private static DateTimeOffset NormalizeForPostgreSql(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private async Task EnsureActiveSessionAsync(
        Guid userId,
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var active = await db.DeviceSessions.AsNoTracking().AnyAsync(
            session => session.Id == sessionId &&
                session.UserId == userId &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > now,
            cancellationToken);
        if (!active)
        {
            throw new SyncRequestException("invalid_device");
        }
    }

    private Task<int> LockRecordAsync(Guid userId, SyncMutation mutation, CancellationToken cancellationToken)
    {
        var key = $"{userId:N}:{mutation.Id:N}:{(int)mutation.Kind}";
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))",
            cancellationToken);
    }

    private async Task CleanupTombstonesAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeCursors = await db.DeviceSessions.AsNoTracking()
            .Where(session => session.UserId == userId &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > now)
            .Select(session => session.LastSyncCursor)
            .ToListAsync(cancellationToken);
        if (activeCursors.Count == 0)
        {
            return;
        }

        var minimumCursor = activeCursors.Min();
        var cutoff = now - TombstoneRetention;
        var candidates = await db.SyncRecords
            .Where(record => record.UserId == userId && record.IsDeleted && record.ModifiedAtUtc < cutoff)
            .OrderBy(record => record.ModifiedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var record in candidates)
        {
            var tombstoneCursor = await db.ChangeLog
                .Where(change => change.UserId == userId &&
                    change.RecordId == record.Id && change.Kind == record.Kind)
                .MaxAsync(change => (long?)change.Cursor, cancellationToken);
            if (tombstoneCursor is not null && tombstoneCursor <= minimumCursor)
            {
                await db.ChangeLog.Where(change => change.UserId == userId &&
                        change.RecordId == record.Id && change.Kind == record.Kind)
                    .ExecuteDeleteAsync(cancellationToken);
                db.SyncRecords.Remove(record);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static SyncMutation ToMutation(SyncRecordEntity record) => new(
        record.Id,
        record.Kind,
        record.Version,
        record.ModifiedAtUtc,
        record.IsDeleted,
        Parse(record.PayloadJson));

    private static SyncMutation ToMutation(ChangeLogEntity change) => new(
        change.RecordId,
        change.Kind,
        change.Version,
        change.ModifiedAtUtc,
        change.IsDeleted,
        Parse(change.PayloadJson));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string MergeObjects(string serverJson, JsonElement client)
    {
        using var server = JsonDocument.Parse(serverJson);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in server.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        foreach (var property in client.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.Serialize(values);
    }

    private enum ConflictDecision
    {
        Accept,
        MergeFavorite,
        RejectWithServer,
    }
}

public sealed class SyncRequestException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
