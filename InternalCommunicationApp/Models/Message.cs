namespace InternalCommunicationApp.Models;

public class Message
{
    public long Id { get; set; }

    public int ConversationId { get; set; }

    public int SenderId { get; set; }

    public string? Content { get; set; }

    public long? ReplyToMessageId { get; set; }

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    public DateTime? EditedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Conversation Conversation { get; set; } = null!;

    public User Sender { get; set; } = null!;

    public Message? ReplyToMessage { get; set; }
}