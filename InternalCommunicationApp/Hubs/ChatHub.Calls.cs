using InternalCommunicationApp.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace InternalCommunicationApp.Hubs;


public partial class ChatHub
{
    private const int MaxSignalLength = 20_000;
    private CallService Calls => new(_redis);

    private static string[] BothUsers(CallInfo c) => new[] { c.CallerId.ToString(), c.CalleeId.ToString() };

    private async Task<CallInfo> LoadCallAsync(string callId)
    {
        CallInfo? info;
        try { info = await Calls.GetAsync(callId); }
        catch (RedisException) { throw new HubException("Calling is unavailable right now."); }
        if (info == null) throw new HubException("This call has ended.");

        var me = int.Parse(Context.UserIdentifier!);
        if (me != info.CallerId && me != info.CalleeId) throw new HubException("Not your call.");
        return info;
    }

    private async Task EndInternalAsync(CallInfo c, string reason)
    {
        await Calls.EndAsync(c);
        await Clients.Users(BothUsers(c)).SendAsync("CallEnded", new { callId = c.CallId, reason });
    }

    // ---- caller -> server ---------------------------------------------------------------

    public async Task<string> StartCall(string recipient, bool video)
    {
        if (string.IsNullOrWhiteSpace(recipient)) throw new HubException("Choose someone to call first.");

        var me = int.Parse(Context.UserIdentifier!);
        var myName = Context.User!.Identity!.Name!;

        var callee = await _db.Users.AsNoTracking()
            .Where(u => u.Username == recipient)
            .Select(u => new { u.Id, u.Username })
            .FirstOrDefaultAsync();
        if (callee == null) throw new HubException("That user does not exist.");
        if (callee.Id == me) throw new HubException("You cannot call yourself.");

        // Only people who already have a chat together can call each other.
        var knowEachOther = await _db.Conversations.AsNoTracking().AnyAsync(c => !c.IsGroup
            && c.ConversationMembers.Any(m => m.UserId == me)
            && c.ConversationMembers.Any(m => m.UserId == callee.Id));
        if (!knowEachOther) throw new HubException("Start a chat with this person before calling.");

        if (await RateLimitedAsync("call", 5, TimeSpan.FromMinutes(1)))
            throw new HubException("You are calling too often. Please wait a moment.");

        try
        {
            if (!await _presence.IsOnlineAsync(callee.Id))
                throw new HubException($"{callee.Username} is offline.");

            var info = new CallInfo
            {
                CallId = Guid.NewGuid().ToString("N"),
                CallerId = me,
                CalleeId = callee.Id,
                CallerName = myName,
                CalleeName = callee.Username,
                CallerConn = Context.ConnectionId,
                Video = video
            };
            if (!await Calls.TryCreateAsync(info))
                throw new HubException("Line is busy.");

            // Rings every open tab/device of the callee.
            await Clients.User(callee.Id.ToString())
                .SendAsync("IncomingCall", new { callId = info.CallId, from = myName, video });
            return info.CallId;
        }
        catch (RedisException) { throw new HubException("Calling is unavailable right now."); }
    }

    // ---- callee -> server ---------------------------------------------------------------

    public async Task AcceptCall(string callId)
    {
        var info = await LoadCallAsync(callId);
        var me = int.Parse(Context.UserIdentifier!);
        if (me != info.CalleeId) throw new HubException("Only the person being called can answer.");

        if (!await Calls.TryAcceptAsync(info, Context.ConnectionId))
            throw new HubException("This call was already answered.");

        // Caller starts the WebRTC offer now.
        await Clients.Client(info.CallerConn).SendAsync("CallAccepted", new { callId });
        // The callee's other tabs stop ringing.
        await Clients.User(info.CalleeId.ToString()).SendAsync("CallAnsweredElsewhere", new { callId });
    }

    // Either side. Reason is decided here so clients can't fake "missed" etc.
    public async Task EndCall(string callId, string reason)
    {
        var info = await LoadCallAsync(callId);
        var me = int.Parse(Context.UserIdentifier!);

        string final;
        if (info.State == "ringing")
            final = me == info.CallerId ? (reason == "timeout" ? "missed" : "cancelled") : "declined";
        else
            final = reason == "failed" ? "failed" : "ended";

        await EndInternalAsync(info, final);
    }

    // ---- both sides: WebRTC signaling relay -----------------------------------------------

    public async Task Signal(string callId, string kind, string data)
    {
        if (kind != "offer" && kind != "answer" && kind != "ice") throw new HubException("Bad signal.");
        if (data == null || data.Length > MaxSignalLength) throw new HubException("Bad signal.");

        var info = await LoadCallAsync(callId);
        if (info.State != "active") throw new HubException("Call is not active.");

        var me = int.Parse(Context.UserIdentifier!);
        var isCaller = me == info.CallerId;
        var myConn = isCaller ? info.CallerConn : info.CalleeConn;
        var peerConn = isCaller ? info.CalleeConn : info.CallerConn;

        // Only the connection that owns this side of the call may signal (after a reconnect: RejoinCall).
        if (myConn != Context.ConnectionId) throw new HubException("Rejoin the call first.");
        if (string.IsNullOrEmpty(peerConn)) return;

        await Clients.Client(peerConn).SendAsync("CallSignal", new { callId, kind, data });
    }

    // Heartbeat (client sends every ~30s during a call). Without it the call state expires.
    public async Task CallKeepAlive(string callId)
    {
        var info = await LoadCallAsync(callId);
        await Calls.RefreshAsync(info);
    }

    // After a SignalR reconnect the connection id changed. Media keeps flowing during the gap,
    // this just re-attaches signaling. Returns false if the call no longer exists.
    public async Task<bool> RejoinCall(string callId)
    {
        CallInfo info;
        try { info = await LoadCallAsync(callId); }
        catch (HubException) { return false; }

        var me = int.Parse(Context.UserIdentifier!);
        var isCaller = me == info.CallerId;
        if (!isCaller && info.State != "active") return false;

        await Calls.SetConnectionAsync(callId, isCaller, Context.ConnectionId);
        await Calls.RefreshAsync(info);

        var peerConn = isCaller ? info.CalleeConn : info.CallerConn;
        if (!string.IsNullOrEmpty(peerConn))
            await Clients.Client(peerConn).SendAsync("PeerReconnected", new { callId });
        return true;
    }

    // Call this from ChatHub.OnDisconnectedAsync (before base.OnDisconnectedAsync).
    // If the caller closes the tab while still ringing, stop ringing the callee right away.
    // Active calls are NOT ended here: the browser may just be reconnecting.
    private async Task HandleCallDisconnectAsync()
    {
        try
        {
            var me = int.Parse(Context.UserIdentifier!);
            var callId = await Calls.ActiveCallIdAsync(me);
            if (callId == null) return;
            var info = await Calls.GetAsync(callId);
            if (info is { State: "ringing" } && info.CallerId == me && info.CallerConn == Context.ConnectionId)
                await EndInternalAsync(info, "cancelled");
        }
        catch (RedisException) { }
    }
}