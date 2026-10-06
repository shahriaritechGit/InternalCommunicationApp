using InternalCommunicationApp.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace InternalCommunicationApp.Hubs;

public class PresenceSweeper : BackgroundService
{
    private readonly PresenceService _presence;
    private readonly IHubContext<ChatHub> _hub;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<PresenceSweeper> _log;

    public PresenceSweeper(PresenceService presence, IHubContext<ChatHub> hub,
        IServiceScopeFactory scopes, IConnectionMultiplexer redis, ILogger<PresenceSweeper> log)
    {
        _presence = presence;
        _hub = hub;
        _scopes = scopes;
        _redis = redis;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Lock so only one server instance sweeps at a time (matters once you scale out)
                var gotLock = await _redis.GetDatabase().StringSetAsync(
                    "presence:sweeper", "1", TimeSpan.FromSeconds(25), When.NotExists);
                if (!gotLock) continue;

                var gone = await _presence.SweepStaleAsync();
                if (gone.Count == 0) continue;

                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                foreach (var (userId, lastSeen) in gone)
                {
                    var username = await db.Users.AsNoTracking()
                        .Where(u => u.Id == userId)
                        .Select(u => u.Username)
                        .FirstOrDefaultAsync(stoppingToken);

                    var partnerIds = await db.ConversationMembers.AsNoTracking()
                        .Where(m => m.UserId != userId
                            && !m.Conversation.IsGroup
                            && m.Conversation.ConversationMembers.Any(x => x.UserId == userId))
                        .Select(m => m.UserId)
                        .Distinct()
                        .ToListAsync(stoppingToken);

                    if (username != null && partnerIds.Count > 0)
                        await _hub.Clients.Users(partnerIds.Select(i => i.ToString()).ToList())
                            .SendAsync("PresenceChanged", username, false, lastSeen, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Presence sweep failed");   // never let the loop die
            }
        }
    }
}