using Microsoft.AspNetCore.SignalR;
using InternalCommunicationApp.Data;
using InternalCommunicationApp.Models;
using Microsoft.EntityFrameworkCore;

namespace InternalCommunicationApp.Hubs;

public class ChatHub : Hub
{
    private static readonly Dictionary<string, string> Users = new();
    private readonly AppDbContext _db;
    public ChatHub(AppDbContext db)
    {
        _db = db;
    }

    public override async Task OnConnectedAsync()
    {
        var username = Context.User?.Identity?.Name;
        Console.WriteLine($"Authenticated username: {username}");
        if (!string.IsNullOrWhiteSpace(username))
        {
            Users[username] = Context.ConnectionId;

            Console.WriteLine(
                $"{username} connected with ID {Context.ConnectionId}"
            );
            await Clients.Caller.SendAsync(
                "ConnectedAs",
                username
            );
        }

        await base.OnConnectedAsync();
    }

    public async Task SendMessage(string recipient, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var senderUsername = Context.User?.Identity?.Name;

        if (string.IsNullOrWhiteSpace(senderUsername))
        {
            Console.WriteLine("Sender could not be identified.");
            return;
        }

        var sender = await _db.Users
            .FirstOrDefaultAsync(u => u.Username == senderUsername);

        var receiver = await _db.Users
            .FirstOrDefaultAsync(u => u.Username == recipient);

        if (sender == null || receiver == null)
        {
            Console.WriteLine("Sender or recipient does not exist.");
            return;
        }

        var conversation = await _db.Conversations
            .Where(c => !c.IsGroup)
            .Where(c => c.ConversationMembers.Any(m => m.UserId == sender.Id))
            .Where(c => c.ConversationMembers.Any(m => m.UserId == receiver.Id))
            .FirstOrDefaultAsync();

        if (conversation == null)
        {
            conversation = new Conversation
            {
                IsGroup = false,
                CreatedByUserId = sender.Id
            };

            conversation.ConversationMembers.Add(new ConversationMember
            {
                UserId = sender.Id,
                IsAdmin = false
            });

            conversation.ConversationMembers.Add(new ConversationMember
            {
                UserId = receiver.Id,
                IsAdmin = false
            });

            _db.Conversations.Add(conversation);

            await _db.SaveChangesAsync();
        }

        var newMessage = new Message
        {
            ConversationId = conversation.Id,
            SenderId = sender.Id,
            Content = message,
            SentAt = DateTime.UtcNow
        };

        _db.Messages.Add(newMessage);

        await _db.SaveChangesAsync();

        if (Users.TryGetValue(recipient, out var connectionId))
        {
            await Clients.Client(connectionId)
                .SendAsync(
                    "ReceiveMessage",
                    senderUsername,
                    message
                );
        }

        Console.WriteLine(
            $"{senderUsername} → {recipient}: {message}"
        );
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var username = Users
            .FirstOrDefault(x => x.Value == Context.ConnectionId)
            .Key;

        if (!string.IsNullOrEmpty(username))
        {
            Users.Remove(username);

            Console.WriteLine(
                $"{username} disconnected."
            );
        }

        await base.OnDisconnectedAsync(exception);
    }
}