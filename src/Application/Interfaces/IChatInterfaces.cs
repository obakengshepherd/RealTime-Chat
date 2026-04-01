namespace RealtimeChat;

/// <summary>
/// Persists messages and publishes to Redis pub/sub for real-time delivery.
/// Publish always follows a successful database insert — never speculatively.
/// </summary>
public interface IMessageService
{
    Task<MessageResponse> SendMessageAsync(string conversationId, string senderId, string? idempotencyKey, SendMessageRequest request, CancellationToken ct);
    Task<PagedApiResponse<MessageResponse>> GetMessagesAsync(string conversationId, string userId, GetMessagesRequest query, CancellationToken ct);
    Task<ReadReceiptResponse> MarkReadAsync(string messageId, string userId, CancellationToken ct);
    Task<MessageDeletedResponse> DeleteMessageAsync(string messageId, string userId, CancellationToken ct);
}

/// <summary>Manages conversation lifecycle and membership.</summary>
public interface IConversationService
{
    Task<ConversationResponse> CreateConversationAsync(string creatorId, CreateConversationRequest request, CancellationToken ct);
    Task<PagedApiResponse<ConversationResponse>> GetUserConversationsAsync(string targetUserId, string requestingUserId, PaginationRequest query, CancellationToken ct);
}

/// <summary>
/// Manages online/offline presence via Redis TTL.
/// Key: presence:{user_id} — TTL 35s, renewed on WebSocket heartbeat.
/// </summary>
public interface IPresenceService
{
    Task SetOnlineAsync(string userId, CancellationToken ct);
    Task SetOfflineAsync(string userId, CancellationToken ct);
    Task RenewPresenceAsync(string userId, CancellationToken ct);
    Task<bool> IsOnlineAsync(string userId, CancellationToken ct);
}