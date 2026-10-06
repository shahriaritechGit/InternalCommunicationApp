using StackExchange.Redis;

namespace InternalCommunicationApp.Hubs;


public class PresenceService
{
    public const int StaleMs = 90_000;      // no heartbeat for this long then connection is dead
    private const string UsersKey = "presence:users";
    private readonly IConnectionMultiplexer _redis;

    public PresenceService(IConnectionMultiplexer redis) => _redis = redis;

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string ConnKey(int userId) => $"presence:{userId}";
    private static string LastSeenKey(int userId) => $"lastseen:{userId}";

    /// Call on connect AND on every heartbeat. Returns true if the user just came online.
    public async Task<bool> TouchAsync(int userId, string connectionId)
    {
        var db = _redis.GetDatabase();
        var key = ConnKey(userId);
        var now = NowMs();

        await db.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, now - StaleMs);
        var aliveBefore = await db.SortedSetLengthAsync(key);

        await db.SortedSetAddAsync(key, connectionId, now);
        await db.SortedSetAddAsync(UsersKey, userId, now);
        await db.KeyExpireAsync(key, TimeSpan.FromMinutes(10));

        return aliveBefore == 0;
    }

    /// Call on disconnect. Returns true if that was the user's last live connection.
    public async Task<bool> DisconnectAsync(int userId, string connectionId)
    {
        var db = _redis.GetDatabase();
        var key = ConnKey(userId);

        await db.SortedSetRemoveAsync(key, connectionId);
        await db.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, NowMs() - StaleMs);
        if (await db.SortedSetLengthAsync(key) > 0) return false;

        await db.SortedSetRemoveAsync(UsersKey, userId);
        await db.StringSetAsync(LastSeenKey(userId), NowMs(), TimeSpan.FromDays(30));
        return true;
    }

    public async Task<bool> IsOnlineAsync(int userId)
    {
        var db = _redis.GetDatabase();
        return await db.SortedSetLengthAsync(ConnKey(userId), NowMs() - StaleMs, double.PositiveInfinity) > 0;
    }

    public async Task<HashSet<int>> OnlineAmongAsync(IEnumerable<int> userIds)
    {
        var ids = userIds.ToList();
        var flags = await Task.WhenAll(ids.Select(IsOnlineAsync));
        return ids.Where((_, i) => flags[i]).ToHashSet();
    }

    public async Task<long?> LastSeenAsync(int userId)
    {
        var v = await _redis.GetDatabase().StringGetAsync(LastSeenKey(userId));
        return v.HasValue ? (long?)(long)v : null;
    }

    /// Finds users whose every connection went silent (crash, network loss), marks them offline.
    public async Task<List<(int UserId, long LastSeen)>> SweepStaleAsync()
    {
        var db = _redis.GetDatabase();
        var stale = await db.SortedSetRangeByScoreWithScoresAsync(
            UsersKey, double.NegativeInfinity, NowMs() - StaleMs);

        var result = new List<(int, long)>();
        foreach (var entry in stale)
        {
            var userId = (int)entry.Element;
            var lastSeen = (long)entry.Score;

            if (await db.SortedSetRemoveAsync(UsersKey, entry.Element))   // only one sweeper wins
            {
                await db.KeyDeleteAsync(ConnKey(userId));
                await db.StringSetAsync(LastSeenKey(userId), lastSeen, TimeSpan.FromDays(30));
                result.Add((userId, lastSeen));
            }
        }
        return result;
    }
}

/// Redis keys
///   presence:{userId}   ZSET    connectionId -> last heartbeat (unix ms), one entry per tab/device
///   presence:users      ZSET    userId       -> newest heartbeat of any device
///   lastseen:{userId}   STRING  unix ms