namespace RealtimeChat.Application.Services;

using RealtimeChat.Application.Interfaces;
using RealtimeChat.Api.Models.Requests;
using RealtimeChat.Api.Models.Responses;

public class MessageService : IMessageService
{
    public Task<MessageResponse> SendMessageAsync(string conversationId, string senderId, string? idempotencyKey, SendMessageRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
    public Task<PagedApiResponse<MessageResponse>> GetMessagesAsync(string conversationId, string userId, GetMessagesRequest query, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
    public Task<ReadReceiptResponse> MarkReadAsync(string messageId, string userId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
    public Task<MessageDeletedResponse> DeleteMessageAsync(string messageId, string userId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
}

public class ConversationService : IConversationService
{
    public Task<ConversationResponse> CreateConversationAsync(string creatorId, CreateConversationRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
    public Task<PagedApiResponse<ConversationResponse>> GetUserConversationsAsync(string targetUserId, string requestingUserId, PaginationRequest query, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
}

public class PresenceService : IPresenceService
{
    public Task SetOnlineAsync(string userId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
    public Task SetOfflineAsync(string userId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
    public Task RenewPresenceAsync(string userId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
    public Task<bool> IsOnlineAsync(string userId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
}