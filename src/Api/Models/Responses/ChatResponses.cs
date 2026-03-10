namespace RealtimeChat.Api.Models.Responses;

public record ConversationResponse
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Name { get; init; }
    public int MemberCount { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public LastMessagePreview? LastMessage { get; init; }
    public int UnreadCount { get; init; }
}

public record LastMessagePreview
{
    public string Content { get; init; } = string.Empty;
    public string SenderId { get; init; } = string.Empty;
    public DateTimeOffset SentAt { get; init; }
}

public record MessageResponse
{
    public string Id { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string SenderId { get; init; } = string.Empty;
    public string? Content { get; init; }
    public string Type { get; init; } = string.Empty;
    public DateTimeOffset SentAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
}

public record ReadReceiptResponse
{
    public string MessageId { get; init; } = string.Empty;
    public DateTimeOffset ReadAt { get; init; }
}

public record MessageDeletedResponse
{
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset DeletedAt { get; init; }
}

public record ApiResponse<T> { public T Data { get; init; } = default!; public ApiMeta Meta { get; init; } = new(); }
public record PagedApiResponse<T> { public IEnumerable<T> Data { get; init; } = []; public PaginationMeta Pagination { get; init; } = new(); public ApiMeta Meta { get; init; } = new(); }
public record ApiMeta { public string RequestId { get; init; } = Guid.NewGuid().ToString(); public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow; }
public record PaginationMeta { public string? Cursor { get; init; } public bool HasMore { get; init; } public int Limit { get; init; } }
public static class ApiResponse { public static ApiResponse<T> Success<T>(T data) => new() { Data = data }; }