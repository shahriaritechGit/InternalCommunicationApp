using InternalCommunicationApp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace InternalCommunicationApp.Pages;

[Authorize]
public class IndexModel : PageModel
{
    
    
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    public List<ConversationViewModel> Conversations { get; set; } = new();

    public async Task OnGetAsync()
    {
        var currentUserId = int.Parse(
            User.FindFirst(
                System.Security.Claims.ClaimTypes.NameIdentifier
            )!.Value
        );

        Conversations = await _db.Conversations
            .Where(c =>
                c.ConversationMembers
                    .Any(m => m.UserId == currentUserId))
            .Select(c => new ConversationViewModel
            {
                ConversationId = c.Id,

                OtherUsername = c.ConversationMembers
                    .Where(m => m.UserId != currentUserId)
                    .Select(m => m.User.Username)
                    .FirstOrDefault() ?? "Unknown"
            })
            .ToListAsync();
    }

    public class ConversationViewModel
    {
        public int ConversationId { get; set; }

        public string OtherUsername { get; set; } = string.Empty;
    }

}
