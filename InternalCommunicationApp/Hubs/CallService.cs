using StackExchange.Redis;

namespace InternalCommunicationApp.Hubs;

public class CallInfo
{
    public string CallId { get; set; } = "";
    public int CallerId { get; set; }
    public int CalleeId { get; set; }
    public string CallerName { get; set; } = "";
    public string CalleeName { get; set; } = "";
    public string CallerConn { get; set; } = "";
    public string CalleeConn { get; set; } = "";
    public bool Video { get; set; }
    public string State { get; set; } = "ringing";   // ringing | active
}


public class CallService
{
    public static readonly TimeSpan RingTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CallTtl = TimeSpan.FromMinutes(2);

    private readonly IConnectionMultiplexer _redis;
    public CallService(IConnectionMultiplexer redis) => _redis = redis;

    private static string CallKey(string id) => $"call:{id}";
    private static string BusyKey(int userId) => $"callbusy:{userId}";

    private const string CreateScript = @"
        if redis.call('EXISTS', KEYS[2]) == 1 or redis.call('EXISTS', KEYS[3]) == 1 then return 0 end
        redis.call('HSET', KEYS[1], 'caller', ARGV[1], 'callee', ARGV[2], 'callerName', ARGV[3],
                   'calleeName', ARGV[4], 'callerConn', ARGV[5], 'calleeConn', '', 'video', ARGV[6], 'state', 'ringing')
        redis.call('EXPIRE', KEYS[1], ARGV[7])
        redis.call('SET', KEYS[2], ARGV[8], 'EX', ARGV[7])
        redis.call('SET', KEYS[3], ARGV[8], 'EX', ARGV[7])
        return 1";

    private const string AcceptScript = @"
        if redis.call('HGET', KEYS[1], 'state') ~= 'ringing' then return 0 end
        redis.call('HSET', KEYS[1], 'state', 'active', 'calleeConn', ARGV[1])
        redis.call('EXPIRE', KEYS[1], ARGV[2])
        redis.call('EXPIRE', KEYS[2], ARGV[2])
        redis.call('EXPIRE', KEYS[3], ARGV[2])
        return 1";

    /// Atomically claims both users. Returns false if either is already in a call.
    public async Task<bool> TryCreateAsync(CallInfo c)
    {
        var r = await _redis.GetDatabase().ScriptEvaluateAsync(CreateScript,
            new RedisKey[] { CallKey(c.CallId), BusyKey(c.CallerId), BusyKey(c.CalleeId) },
            new RedisValue[] { c.CallerId, c.CalleeId, c.CallerName, c.CalleeName, c.CallerConn,
                               c.Video ? "1" : "0", (int)RingTtl.TotalSeconds, c.CallId });
        return (long)r == 1;
    }

    public async Task<CallInfo?> GetAsync(string callId)
    {
        var h = await _redis.GetDatabase().HashGetAllAsync(CallKey(callId));
        if (h.Length == 0) return null;
        var d = h.ToDictionary(e => (string)e.Name!, e => (string?)e.Value ?? "");
        return new CallInfo
        {
            CallId = callId,
            CallerId = int.Parse(d["caller"]),
            CalleeId = int.Parse(d["callee"]),
            CallerName = d["callerName"],
            CalleeName = d["calleeName"],
            CallerConn = d["callerConn"],
            CalleeConn = d["calleeConn"],
            Video = d["video"] == "1",
            State = d["state"]
        };
    }

    /// ringing -> active, exactly once (two tabs pressing Accept: only one wins).
    public async Task<bool> TryAcceptAsync(CallInfo c, string calleeConn)
    {
        var r = await _redis.GetDatabase().ScriptEvaluateAsync(AcceptScript,
            new RedisKey[] { CallKey(c.CallId), BusyKey(c.CallerId), BusyKey(c.CalleeId) },
            new RedisValue[] { calleeConn, (int)CallTtl.TotalSeconds });
        return (long)r == 1;
    }

    public Task SetConnectionAsync(string callId, bool isCaller, string connectionId) =>
        _redis.GetDatabase().HashSetAsync(CallKey(callId), isCaller ? "callerConn" : "calleeConn", connectionId);

    public async Task RefreshAsync(CallInfo c)
    {
        var db = _redis.GetDatabase();
        await db.KeyExpireAsync(CallKey(c.CallId), CallTtl);
        await db.KeyExpireAsync(BusyKey(c.CallerId), CallTtl);
        await db.KeyExpireAsync(BusyKey(c.CalleeId), CallTtl);
    }

    public Task<string?> ActiveCallIdAsync(int userId) =>
        _redis.GetDatabase().StringGetAsync(BusyKey(userId)).ContinueWith(t => t.Result.HasValue ? (string?)t.Result : null);

    public async Task EndAsync(CallInfo c)
    {
        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync(new RedisKey[] { CallKey(c.CallId), BusyKey(c.CallerId), BusyKey(c.CalleeId) });
    }
}