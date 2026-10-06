using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using StackExchange.Redis;
using Microsoft.AspNetCore.SignalR;
using InternalCommunicationApp.Data;
using InternalCommunicationApp.Models;
using Microsoft.EntityFrameworkCore;

namespace InternalCommunicationApp.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private const int MaxMessageLength = 2000;
    private readonly AppDbContext _db;
    private readonly IConnectionMultiplexer _redis;
    private readonly PresenceService _presence;

    public ChatHub(AppDbContext db, IConnectionMultiplexer redis, PresenceService presence)
    {
        _db = db;
        _redis = redis;
        _presence = presence;
    }
    private record Partner(int Id, string Username);

    private static readonly LuaScript RateLimitScript = LuaScript.Prepare(@"
        local n = redis.call('INCR', @key)
        if n == 1 then redis.call('PEXPIRE', @key, @ms) end
        return n");

    // Fixed-window rate limit (atomic). If Redis is down we allow the request.
    private async Task<bool> RateLimitedAsync(string scope, int max, TimeSpan window)
    {
        try
        {
            var key = $"rl:{scope}:{Context.UserIdentifier}";
            var result = await _redis.GetDatabase().ScriptEvaluateAsync(
                RateLimitScript,
                new { key = (RedisKey)key, ms = (long)window.TotalMilliseconds });
            return (long)result > max;
        }
        catch (RedisException) { return false; }
    }

    // username -> id, cached in Redis so typing events never hit SQL
    private async Task<int?> ResolveUserIdAsync(string username)
    {
        var key = $"userid:{username.ToLowerInvariant()}";
        try
        {
            var cached = await _redis.GetDatabase().StringGetAsync(key);
            if (cached.HasValue) return (int)cached;
        }
        catch (RedisException) { }

        var id = await _db.Users.AsNoTracking()
            .Where(u => u.Username == username)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync();

        if (id != null)
        {
            try { await _redis.GetDatabase().StringSetAsync(key, id.Value, TimeSpan.FromDays(7)); }
            catch (RedisException) { }
        }
        return id;
    }

    private Task<List<Partner>> GetPartnersAsync(int userId) =>
        _db.ConversationMembers.AsNoTracking()
            .Where(m => m.UserId != userId
                && !m.Conversation.IsGroup
                && m.Conversation.ConversationMembers.Any(x => x.UserId == userId))
            .Select(m => new Partner(m.UserId, m.User.Username))
            .Distinct()
            .ToListAsync();
    public record PresenceDto(bool Online, long? LastSeen);
    private async Task InvalidateRecentAsync(int conversationId)
    {
        try { await _redis.GetDatabase().KeyDeleteAsync($"chat:{conversationId}:recent"); }
        catch (RedisException) { }
    }

    private async Task<(Message Msg, List<string> UserIds, string OtherName, bool IsLast)> LoadOwnMessageAsync(long messageId)
    {
        var me = int.Parse(Context.UserIdentifier!);

        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.SenderId == me);
        if (msg == null) throw new HubException("Message not found.");
        if (msg.DeletedAt != null) throw new HubException("Message was already deleted.");

        var members = await _db.ConversationMembers.AsNoTracking()
            .Where(m => m.ConversationId == msg.ConversationId)
            .Select(m => new { m.UserId, m.User.Username })
            .ToListAsync();

        var other = members.FirstOrDefault(m => m.UserId != me)?.Username ?? "";
        var isLast = !await _db.Messages.AnyAsync(m => m.ConversationId == msg.ConversationId && m.Id > msg.Id);

        return (msg, members.Select(m => m.UserId.ToString()).ToList(), other, isLast);
    }

    public async Task EditMessage(long messageId, string newText)
    {
        newText = newText?.Trim() ?? "";
        if (newText.Length == 0) throw new HubException("Message is empty.");
        if (newText.Length > MaxMessageLength) throw new HubException("Message is too long.");
        if (await RateLimitedAsync("edit", 10, TimeSpan.FromSeconds(10)))
            throw new HubException("You are editing too fast. Please wait a few seconds.");

        var (msg, userIds, other, isLast) = await LoadOwnMessageAsync(messageId);

        msg.Content = newText;
        msg.EditedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await InvalidateRecentAsync(msg.ConversationId);   // cached copy is stale now

        await Clients.Users(userIds).SendAsync("MessageEdited", new
        {
            id = msg.Id,
            conversationId = msg.ConversationId,
            sender = Context.User!.Identity!.Name,
            recipient = other,
            text = msg.Content,
            editedAt = msg.EditedAt,
            isLast
        });
    }

    public async Task DeleteMessage(long messageId)
    {
        var (msg, userIds, other, isLast) = await LoadOwnMessageAsync(messageId);

        msg.DeletedAt = DateTime.UtcNow;                   // soft delete: row and content stay in the DB
        await _db.SaveChangesAsync();
        await InvalidateRecentAsync(msg.ConversationId);

        await Clients.Users(userIds).SendAsync("MessageDeleted", new
        {
            id = msg.Id,
            conversationId = msg.ConversationId,
            sender = Context.User!.Identity!.Name,
            recipient = other,
            isLast
        });
    }
    public override async Task OnConnectedAsync()
    {
        var userId = int.Parse(Context.UserIdentifier!);
        var username = Context.User?.Identity?.Name;
        await Clients.Caller.SendAsync("ConnectedAs", username);

        try
        {
            var cameOnline = await _presence.TouchAsync(userId, Context.ConnectionId);
            var partners = await GetPartnersAsync(userId);

            if (cameOnline && partners.Count > 0)
                await Clients.Users(partners.Select(p => p.Id.ToString()).ToList())
                    .SendAsync("PresenceChanged", username, true, (long?)null);

            var online = await _presence.OnlineAmongAsync(partners.Select(p => p.Id));
            await Clients.Caller.SendAsync("OnlineList",
                partners.Where(p => online.Contains(p.Id)).Select(p => p.Username).ToList());
        }
        catch (RedisException ex) 
        { 
            Console.WriteLine($"Presence skipped: {ex.Message}"); 
        }
        await base.OnConnectedAsync();
    }

    public async Task SendMessage(string recipient, string message,string clientId)
    {
        message = message?.Trim() ?? "";
        if (message.Length == 0) throw new HubException("Message is empty.");
        if (message.Length > MaxMessageLength) throw new HubException("Message is too long.");
        if (string.IsNullOrWhiteSpace(recipient)) throw new HubException("Choose someone to message first.");

        // Context.UserIdentifier = the NameIdentifier claim set in Login (the user's Id)
        var senderId = int.Parse(Context.UserIdentifier!);
        var senderName = Context.User!.Identity!.Name!;

        var receiver = await _db.Users.AsNoTracking()
            .Where(u => u.Username == recipient)
            .Select(u => new { u.Id, u.Username })
            .FirstOrDefaultAsync();

        if (receiver == null) throw new HubException("That user does not exist.");
        if (receiver.Id == senderId) throw new HubException("You cannot message yourself.");
        if (await RateLimitedAsync("msg", 10, TimeSpan.FromSeconds(10)))
        {
            throw new HubException("You are sending too fast. Please wait a few seconds.");
        }
        var conversation = await _db.Conversations
            .Where(c => !c.IsGroup
                && c.ConversationMembers.Any(m => m.UserId == senderId)
                && c.ConversationMembers.Any(m => m.UserId == receiver.Id))
            .FirstOrDefaultAsync();

        if (conversation == null)
        {
            conversation = new Conversation { IsGroup = false, CreatedByUserId = senderId };
            conversation.ConversationMembers.Add(new ConversationMember { UserId = senderId });
            conversation.ConversationMembers.Add(new ConversationMember { UserId = receiver.Id });
            _db.Conversations.Add(conversation);
            await _db.SaveChangesAsync();
        }

        var msg = new Message
        {
            ConversationId = conversation.Id,
            SenderId = senderId,
            Content = message,
            SentAt = DateTime.UtcNow
        };
        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();

        var payload = new
        {
            clientId,
            conversationId = conversation.Id,
            id = msg.Id,
            sender = senderName,
            recipient = receiver.Username,
            text = msg.Content,
            sentAt = msg.SentAt
        };
        // Goes to every open tab of BOTH users, and to nobody else.
        await Clients.Users(new[] { senderId.ToString(), receiver.Id.ToString() })
            .SendAsync("ReceiveMessage", payload);
        // Redis = fast cache of the newest 50 messages. SQL Server stays the source of truth.
        try
        {
            var db = _redis.GetDatabase();
            var key = $"chat:{conversation.Id}:recent";
            await db.ListLeftPushAsync(key, JsonSerializer.Serialize(payload));
            await db.ListTrimAsync(key, 0, 49);
            await db.HashIncrementAsync($"unread:{receiver.Id}", senderName);
            await db.KeyExpireAsync(key, TimeSpan.FromDays(1));
        }
        catch (RedisException ex)
        {
            Console.WriteLine($"Redis cache skipped: {ex.Message}");
        }


    }
    public async Task<PresenceDto> GetPresence(string username)
    {
        var id = await ResolveUserIdAsync(username);
        if (id == null) 
        {
            return new PresenceDto(false, null);
        }

        try
        {
            if (await _presence.IsOnlineAsync(id.Value)) 
            {
                return new PresenceDto(true, null);
            }
            return new PresenceDto(false, await _presence.LastSeenAsync(id.Value));
        }
        catch (RedisException) 
        { 
            return new PresenceDto(false, null); 
        }
    }

    // Heartbeat: client calls this every 25s while the tab is open
    public async Task Ping()
    {
        try
        {
            var userId = int.Parse(Context.UserIdentifier!);

            // true only if this user had no live connection (e.g. was swept as stale) -> announce online again
            if (await _presence.TouchAsync(userId, Context.ConnectionId))
            {
                var partners = await GetPartnersAsync(userId);
                if (partners.Count > 0)
                    await Clients.Users(partners.Select(p => p.Id.ToString()).ToList())
                        .SendAsync("PresenceChanged", Context.User!.Identity!.Name, true, (long?)null);
            }
        }
        catch (RedisException) { }
    }
    public async Task Typing(string recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient)) return;
        if (await RateLimitedAsync("typing", 1, TimeSpan.FromSeconds(1))) return;   // max 1/sec

        var receiverId = await ResolveUserIdAsync(recipient);
        if (receiverId == null) return;

        await Clients.User(receiverId.Value.ToString())
            .SendAsync("UserTyping", Context.User!.Identity!.Name);
    }

    public async Task MarkRead(string username)
    {
        try { await _redis.GetDatabase().HashDeleteAsync($"unread:{Context.UserIdentifier}", username); }
        catch (RedisException) { }

        // clears the badge in the user's other tabs too
        await Clients.User(Context.UserIdentifier!).SendAsync("UnreadCleared", username);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            var userId = int.Parse(Context.UserIdentifier!);

            if (await _presence.DisconnectAsync(userId, Context.ConnectionId))
            {
                var partners = await GetPartnersAsync(userId);
                if (partners.Count > 0)
                    await Clients.Users(partners.Select(p => p.Id.ToString()).ToList())
                        .SendAsync("PresenceChanged", Context.User!.Identity!.Name, false,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }
        catch (RedisException ex) { Console.WriteLine($"Presence cleanup skipped: {ex.Message}"); }

        await base.OnDisconnectedAsync(exception);
    }
}