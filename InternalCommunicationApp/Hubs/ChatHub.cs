using Microsoft.AspNetCore.SignalR;

namespace InternalCommunicationApp.Hubs;

public class ChatHub : Hub
{
    private static readonly Dictionary<string, string> Users = new();

    public override async Task OnConnectedAsync()
    {
        var username = Context.GetHttpContext()?
            .Request.Query["username"]
            .ToString();

        if (!string.IsNullOrWhiteSpace(username))
        {
            Users[username] = Context.ConnectionId;

            Console.WriteLine(
                $"{username} connected with ID {Context.ConnectionId}"
            );
        }

        await base.OnConnectedAsync();
    }

    public async Task SendMessage(string recipient, string message)
    {
        if (Users.TryGetValue(recipient, out var connectionId))
        {
            var sender = Users
                .FirstOrDefault(x => x.Value == Context.ConnectionId)
                .Key;

            await Clients.Client(connectionId)
                .SendAsync("ReceiveMessage", sender, message);
        }
        else
        {
            Console.WriteLine(
                $"User '{recipient}' not found or not connected."
            );
        }
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