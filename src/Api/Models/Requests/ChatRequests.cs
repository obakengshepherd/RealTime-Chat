namespace RealtimeChat;

using System.ComponentModel.DataAnnotations;

public record CreateConversationRequest
{
    [Required] public string Type { get; init; } = string.Empty; // direct | group
    [StringLength(128)] public string? Name { get; init; }
    [Required][MinLength(2)] public IEnumerable<string> MemberIds { get; init; } = [];
}

public record SendMessageRequest
{
    [Required][StringLength(4000, MinimumLength = 1)] public string Content { get; init; } = string.Empty;
    public string Type { get; init; } = "text";
}

public record GetMessagesRequest
{
    [Range(1, 100)] public int Limit { get; init; } = 50;
    public string? Cursor { get; init; }
    public string? Before { get; init; }
}

public record PaginationRequest
{
    [Range(1, 50)] public int Limit { get; init; } = 20;
    public string? Cursor { get; init; }
}
