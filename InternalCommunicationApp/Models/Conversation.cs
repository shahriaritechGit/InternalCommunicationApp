namespace InternalCommunicationApp.Models;
public class Conversation
{
    public int Id {get; set;}
    public string? Name {get; set;}

    public bool IsGroup {get; set;}
    public int CreatedByUserId {get; set;}
    public DateTime CreatedAt {get; set;} = DateTime.UtcNow;

    public ICollection<ConversationMember> ConversationMembers {get; set;} = new List<ConversationMember>();
    
}