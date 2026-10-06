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

    public ChatHub(AppDbContext db, IConnectionMultiplexer redis)
    {
        _db = db;
        _redis = redis;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("ConnectedAs", Context.User?.Identity?.Name);
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
            await db.KeyExpireAsync(key, TimeSpan.FromDays(1));
        }
        catch (RedisException ex)
        {
            Console.WriteLine($"Redis cache skipped: {ex.Message}");
        }


    }
    // public override async Task OnDisconnectedAsync(Exception? exception)
    // {
    //     var username = Users
    //         .FirstOrDefault(x => x.Value == Context.ConnectionId)
    //         .Key;

    //     if (!string.IsNullOrEmpty(username))
    //     {
    //         Users.Remove(username);

    //         Console.WriteLine(
    //             $"{username} disconnected."
    //         );
    //     }

    //     await base.OnDisconnectedAsync(exception);
   // }
}