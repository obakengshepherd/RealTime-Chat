namespace RealtimeChat.Domain.Entities;

public class Conversation
{
    public string Id { get; private set; } = string.Empty;
    public ConversationType Type { get; private set; }
    public string? Name { get; private set; }
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public List<ConversationMember> Members { get; private set; } = [];
}

public class ConversationMember
{
    public string ConversationId { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string? LastReadMessageId { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }
}

public class Message
{
    public string Id { get; private set; } = string.Empty;
    public string ConversationId { get; private set; } = string.Empty;
    public string SenderId { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public MessageType Type { get; private set; }
    public DateTimeOffset SentAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    public void SoftDelete() => throw new NotImplementedException();
}

public enum ConversationType { Direct, Group }
public enum MessageType { Text, System }