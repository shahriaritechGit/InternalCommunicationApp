using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using InternalCommunicationApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InternalCommunicationApp.Pages;

[Authorize]
public class IndexModel : PageModel
{
    
    
    private readonly AppDbContext _db;
    private readonly IConnectionMultiplexer _redis;
    private const int PageSize = 30;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IndexModel(AppDbContext db, IConnectionMultiplexer redis)
    {
        _db = db;
        _redis = redis;
    }

    private int CurrentUserId => int.Parse(
        User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public record MessageDto(long Id, string Sender, string? Text, DateTime SentAt);
    public List<ConversationViewModel> Conversations { get; set; } = new();

    public async Task OnGetAsync()
    {
        var currentUserId = CurrentUserId;

        Conversations = await _db.Conversations
            .Where(c => c.ConversationMembers.Any(m => m.UserId == currentUserId) && c.ConversationMembers.Any(m => m.UserId != currentUserId))
            .Select(c => new ConversationViewModel
            {
                ConversationId = c.Id,
                OtherUsername = c.ConversationMembers
                    .Where(m => m.UserId != currentUserId)
                    .Select(m => m.User.Username)
                    .FirstOrDefault() ?? "Unknown",
                LastMessageAt = _db.Messages
                    .Where(m => m.ConversationId == c.Id)
                    .Max(m => (DateTime?)m.SentAt)
            })
            .OrderByDescending(x => x.LastMessageAt)
            .ToListAsync();
            try
            {
                var entries = await _redis.GetDatabase().HashGetAllAsync($"unread:{currentUserId}");
                var unread = entries.ToDictionary(e => (string)e.Name!, e => (int)e.Value);
                foreach (var c in Conversations)
                    if (unread.TryGetValue(c.OtherUsername, out var n)) c.Unread = n;
            }
            catch (RedisException) { }
    }
    // GET /?handler=Users&q=af  -> ["afnan", ...]
public async Task<IActionResult> OnGetUsersAsync(string? q)
{
    q = q?.Trim();
    if (string.IsNullOrEmpty(q)) return new JsonResult(Array.Empty<string>());

    var me = CurrentUserId;
    var names = await _db.Users.AsNoTracking()
        .Where(u => u.Id != me && u.Username.StartsWith(q))
        .OrderBy(u => u.Username)
        .Select(u => u.Username)
        .Take(20)
        .ToListAsync();

    return new JsonResult(names);
}

// GET /?handler=Messages&with=afnan[&before=123]
public async Task<IActionResult> OnGetMessagesAsync(string with, long? before)
{
    var me = CurrentUserId;

    var otherId = await _db.Users.AsNoTracking()
        .Where(u => u.Username == with)
        .Select(u => (int?)u.Id)
        .FirstOrDefaultAsync();
    if (otherId == null) return new JsonResult(Array.Empty<MessageDto>());

    // Includes "I am a member" so nobody can read someone else's chat
    var convId = await _db.Conversations.AsNoTracking()
        .Where(c => !c.IsGroup
            && c.ConversationMembers.Any(m => m.UserId == me)
            && c.ConversationMembers.Any(m => m.UserId == otherId))
        .Select(c => (int?)c.Id)
        .FirstOrDefaultAsync();
    if (convId == null) return new JsonResult(Array.Empty<MessageDto>()); // chat not started yet

    // First page: try Redis, fall back to SQL
    if (before == null)
    {
        try
        {
            var cached = await _redis.GetDatabase()
                .ListRangeAsync($"chat:{convId}:recent", 0, PageSize - 1);
            if (cached.Length >= PageSize)
                return new JsonResult(cached
                    .Select(v => JsonSerializer.Deserialize<MessageDto>((string)v!, Json)!)
                    .Reverse());
        }
        catch (RedisException) { /* fall through to SQL */ }
    }

    var query = _db.Messages.AsNoTracking()
        .Where(m => m.ConversationId == convId && m.DeletedAt == null);
    if (before != null) query = query.Where(m => m.Id < before);

    var page = await query
        .OrderByDescending(m => m.Id)
        .Take(PageSize)
        .Select(m => new MessageDto(m.Id, m.Sender.Username, m.Content, m.SentAt))
        .ToListAsync();
     // oldest first for display
    page.Reverse();  
    return new JsonResult(page);
}

    public class ConversationViewModel
    {
        public int ConversationId { get; set; }

        public string OtherUsername { get; set; } = string.Empty;
        public DateTime? LastMessageAt { get; set; }
        public int Unread { get; set; }

    }
}