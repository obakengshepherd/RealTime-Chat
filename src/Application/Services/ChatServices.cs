using System.Text.Json;
using Dapper;
using Npgsql;
using RealtimeChat.Api.Models.Requests;
using RealtimeChat.Api.Models.Responses;
using RealtimeChat.Application.Interfaces;
using StackExchange.Redis;

namespace RealtimeChat.Infrastructure.Persistence;

// ════════════════════════════════════════════════════════════════════════════
// CHAT REPOSITORY
// ════════════════════════════════════════════════════════════════════════════

public class ChatRepository
{
    private readonly string _connectionString;

    public ChatRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task<bool> IsMemberAsync(string conversationId, string userId)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleAsync<bool>("""
            SELECT EXISTS(
                SELECT 1 FROM conversation_members
                WHERE conversation_id = @ConversationId AND user_id = @UserId
            )
            """, new { ConversationId = conversationId, UserId = userId });
    }

    public async Task InsertMessageAsync(MessageRecord message, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO messages (id, conversation_id, sender_id, content, type, sent_at)
            VALUES (@Id, @ConversationId, @SenderId, @Content, @Type::msg_type, @SentAt)
            """;
        await conn.ExecuteAsync(sql, message);
    }

    public async Task<(IEnumerable<MessageRecord> Items, string? NextCursor)> GetMessagesPageAsync(
        string conversationId, int limit, string? cursor, string? before)
    {
        using var conn = CreateConnection();

        var conditions = new List<string> { "conversation_id = @ConversationId" };

        // Cursor-based: use message id as cursor — stable under concurrent inserts
        if (!string.IsNullOrEmpty(cursor))
            conditions.Add("id < @Cursor");

        if (!string.IsNullOrEmpty(before) && DateTimeOffset.TryParse(before, out var beforeDt))
            conditions.Add("sent_at < @Before");

        var where = "WHERE " + string.Join(" AND ", conditions);
        var sql = $"""
            SELECT id, conversation_id, sender_id, content, type, sent_at, deleted_at
            FROM messages
            {where}
            ORDER BY id DESC
            LIMIT @Limit
            """;

        var items = (await conn.QueryAsync<MessageRecord>(sql, new
        {
            ConversationId = conversationId,
            Cursor         = cursor,
            Before         = before,
            Limit          = limit + 1
        })).ToList();

        var hasMore = items.Count > limit;
        if (hasMore) items.RemoveAt(items.Count - 1);

        string? nextCursor = items.Count > 0 ? items[^1].Id : null;
        return (items, hasMore ? nextCursor : null);
    }

    public async Task UpdateLastReadAsync(string conversationId, string userId, string messageId)
    {
        using var conn = CreateConnection();
        const string sql = """
            UPDATE conversation_members
            SET last_read_message_id = @MessageId
            WHERE conversation_id = @ConversationId AND user_id = @UserId
            """;
        await conn.ExecuteAsync(sql, new { ConversationId = conversationId, UserId = userId, MessageId = messageId });
    }

    public async Task InsertReceiptAsync(string messageId, string userId, DateTimeOffset readAt)
    {
        using var conn = CreateConnection();
        const string sql = """
            INSERT INTO message_receipts (message_id, user_id, delivered_at, read_at)
            VALUES (@MessageId, @UserId, @ReadAt, @ReadAt)
            ON CONFLICT (message_id, user_id) DO UPDATE SET read_at = @ReadAt
            """;
        await conn.ExecuteAsync(sql, new { MessageId = messageId, UserId = userId, ReadAt = readAt });
    }

    public async Task<MessageRecord?> FindMessageByIdAsync(string messageId)
    {
        using var conn = CreateConnection();
        const string sql = "SELECT * FROM messages WHERE id = @MessageId";
        return await conn.QuerySingleOrDefaultAsync<MessageRecord>(sql, new { MessageId = messageId });
    }

    public async Task SoftDeleteMessageAsync(string messageId, string userId)
    {
        using var conn = CreateConnection();
        const string sql = """
            UPDATE messages
            SET content = NULL, deleted_at = NOW()
            WHERE id = @MessageId AND sender_id = @UserId AND deleted_at IS NULL
            """;
        var rows = await conn.ExecuteAsync(sql, new { MessageId = messageId, UserId = userId });
        if (rows == 0) throw new MessageDeleteException(messageId);
    }

    public async Task InsertConversationAsync(ConversationRecord conversation, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO conversations (id, type, name, created_by, created_at)
            VALUES (@Id, @Type::conv_type, @Name, @CreatedBy, @CreatedAt)
            """;
        await conn.ExecuteAsync(sql, conversation);
    }

    public async Task InsertMemberAsync(string conversationId, string userId, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO conversation_members (conversation_id, user_id, joined_at)
            VALUES (@ConversationId, @UserId, NOW())
            ON CONFLICT DO NOTHING
            """;
        await conn.ExecuteAsync(sql, new { ConversationId = conversationId, UserId = userId });
    }

    public async Task<IEnumerable<ConversationListRecord>> GetUserConversationsAsync(
        string userId, int limit, string? cursor)
    {
        using var conn = CreateConnection();
        var cursorClause = cursor is not null ? "AND c.id < @Cursor" : string.Empty;
        var sql = $"""
            SELECT c.id, c.type, c.name, c.created_at,
                   (SELECT COUNT(*) FROM conversation_members cm2
                    WHERE cm2.conversation_id = c.id) AS member_count,
                   m.id AS last_msg_id, m.content AS last_msg_content,
                   m.sender_id AS last_msg_sender, m.sent_at AS last_msg_sent_at
            FROM conversations c
            JOIN conversation_members cm ON cm.conversation_id = c.id AND cm.user_id = @UserId
            LEFT JOIN LATERAL (
                SELECT id, content, sender_id, sent_at
                FROM messages
                WHERE conversation_id = c.id AND deleted_at IS NULL
                ORDER BY id DESC LIMIT 1
            ) m ON true
            WHERE true {cursorClause}
            ORDER BY c.updated_at DESC
            LIMIT @Limit
            """;
        return await conn.QueryAsync<ConversationListRecord>(sql, new { UserId = userId, Cursor = cursor, Limit = limit });
    }
}

public record MessageRecord
{
    public string Id { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string SenderId { get; init; } = string.Empty;
    public string? Content { get; init; }
    public string Type { get; init; } = "text";
    public DateTimeOffset SentAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
}

public record ConversationRecord
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public record ConversationListRecord
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Name { get; init; }
    public int MemberCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? LastMsgId { get; init; }
    public string? LastMsgContent { get; init; }
    public string? LastMsgSender { get; init; }
    public DateTimeOffset? LastMsgSentAt { get; init; }
}

namespace RealtimeChat.Infrastructure.Cache;

// ════════════════════════════════════════════════════════════════════════════
// CHAT CACHE SERVICE (Redis pub/sub + presence)
// ════════════════════════════════════════════════════════════════════════════

public class ChatCacheService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ILogger<ChatCacheService> _logger;
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(35);

    public ChatCacheService(IConnectionMultiplexer redis, ILogger<ChatCacheService> logger)
    {
        _redis  = redis;
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    // ── Pub/Sub ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes a message to the conversation channel.
    /// All WebSocket Gateway instances subscribed to this channel receive it
    /// and push to connected clients.
    /// </summary>
    public async Task PublishMessageAsync(string conversationId, object message)
    {
        try
        {
            var sub     = _redis.GetSubscriber();
            var channel = RedisChannel.Literal($"conversation:{conversationId}");
            await sub.PublishAsync(channel, JsonSerializer.Serialize(message));
        }
        catch (Exception ex)
        {
            // At-most-once delivery — failed publish is non-fatal.
            // Clients recover missed messages on reconnect via message cursor.
            _logger.LogWarning(ex, "Redis pub/sub publish failed for conversation {ConversationId} — non-fatal", conversationId);
        }
    }

    // ── Presence ──────────────────────────────────────────────────────────────

    public async Task SetOnlineAsync(string userId)
    {
        try { await _db.StringSetAsync($"presence:{userId}", "1", PresenceTtl); }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis presence set failed"); }
    }

    public async Task SetOfflineAsync(string userId)
    {
        try { await _db.KeyDeleteAsync($"presence:{userId}"); }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis presence delete failed"); }
    }

    public async Task RenewPresenceAsync(string userId)
    {
        try { await _db.KeyExpireAsync($"presence:{userId}", PresenceTtl); }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis presence renew failed"); }
    }

    public async Task<bool> IsOnlineAsync(string userId)
    {
        try { return await _db.KeyExistsAsync($"presence:{userId}"); }
        catch { return false; }
    }
}

namespace RealtimeChat.Application.Services;

// ════════════════════════════════════════════════════════════════════════════
// MESSAGE SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class MessageService : IMessageService
{
    private readonly ChatRepository _repo;
    private readonly ChatCacheService _cache;
    private readonly ILogger<MessageService> _logger;

    public MessageService(ChatRepository repo, ChatCacheService cache, ILogger<MessageService> logger)
    {
        _repo   = repo;
        _cache  = cache;
        _logger = logger;
    }

    public async Task<MessageResponse> SendMessageAsync(
        string conversationId,
        string senderId,
        string? idempotencyKey,
        SendMessageRequest request,
        CancellationToken ct)
    {
        // Validate sender is a member of the conversation
        if (!await _repo.IsMemberAsync(conversationId, senderId))
            throw new NotConversationMemberException(senderId, conversationId);

        var message = new MessageRecord
        {
            Id             = $"msg_{Guid.NewGuid():N}",
            ConversationId = conversationId,
            SenderId       = senderId,
            Content        = request.Content,
            Type           = request.Type ?? "text",
            SentAt         = DateTimeOffset.UtcNow
        };

        // Persist FIRST — before any pub/sub
        // A failed DB write means no delivery; a failed pub/sub is recovered on reconnect
        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await _repo.InsertMessageAsync(message, conn);

        // After successful DB write, publish to Redis pub/sub for real-time delivery
        await _cache.PublishMessageAsync(conversationId, new
        {
            Type           = "new_message",
            message.Id,
            message.ConversationId,
            message.SenderId,
            message.Content,
            message.SentAt
        });

        _logger.LogInformation("Message {MessageId} sent to conversation {ConvId}", message.Id, conversationId);

        return MapMessage(message);
    }

    public async Task<PagedApiResponse<MessageResponse>> GetMessagesAsync(
        string conversationId,
        string userId,
        GetMessagesRequest query,
        CancellationToken ct)
    {
        if (!await _repo.IsMemberAsync(conversationId, userId))
            throw new NotConversationMemberException(userId, conversationId);

        var (items, nextCursor) = await _repo.GetMessagesPageAsync(
            conversationId, query.Limit, query.Cursor, query.Before);

        return new PagedApiResponse<MessageResponse>
        {
            Data = items.Select(MapMessage),
            Pagination = new PaginationMeta
            {
                Cursor  = nextCursor,
                HasMore = nextCursor is not null,
                Limit   = query.Limit
            }
        };
    }

    public async Task<ReadReceiptResponse> MarkReadAsync(
        string messageId, string userId, CancellationToken ct)
    {
        var message = await _repo.FindMessageByIdAsync(messageId)
            ?? throw new MessageNotFoundException(messageId);

        if (!await _repo.IsMemberAsync(message.ConversationId, userId))
            throw new NotConversationMemberException(userId, message.ConversationId);

        var readAt = DateTimeOffset.UtcNow;
        await _repo.UpdateLastReadAsync(message.ConversationId, userId, messageId);
        await _repo.InsertReceiptAsync(messageId, userId, readAt);

        return new ReadReceiptResponse { MessageId = messageId, ReadAt = readAt };
    }

    public async Task<MessageDeletedResponse> DeleteMessageAsync(
        string messageId, string userId, CancellationToken ct)
    {
        await _repo.SoftDeleteMessageAsync(messageId, userId);
        var deletedAt = DateTimeOffset.UtcNow;

        // Notify conversation members of deletion via pub/sub
        var message = await _repo.FindMessageByIdAsync(messageId);
        if (message is not null)
        {
            await _cache.PublishMessageAsync(message.ConversationId, new
            {
                Type      = "message_deleted",
                MessageId = messageId,
                DeletedAt = deletedAt
            });
        }

        return new MessageDeletedResponse { Id = messageId, DeletedAt = deletedAt };
    }

    private static MessageResponse MapMessage(MessageRecord m) => new()
    {
        Id             = m.Id,
        ConversationId = m.ConversationId,
        SenderId       = m.SenderId,
        Content        = m.DeletedAt.HasValue ? null : m.Content,
        Type           = m.Type,
        SentAt         = m.SentAt,
        DeletedAt      = m.DeletedAt
    };
}

// ════════════════════════════════════════════════════════════════════════════
// CONVERSATION SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class ConversationService : IConversationService
{
    private readonly ChatRepository _repo;

    public ConversationService(ChatRepository repo) => _repo = repo;

    public async Task<ConversationResponse> CreateConversationAsync(
        string creatorId, CreateConversationRequest request, CancellationToken ct)
    {
        if (request.Type == "direct")
        {
            var members = request.MemberIds.ToList();
            if (members.Count != 2)
                throw new ArgumentException("Direct conversations require exactly 2 members.");
            if (!members.Contains(creatorId))
                throw new ArgumentException("Creator must be one of the 2 members.");
        }

        var conv = new ConversationRecord
        {
            Id        = $"conv_{Guid.NewGuid():N}",
            Type      = request.Type,
            Name      = request.Name,
            CreatedBy = creatorId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await _repo.InsertConversationAsync(conv, conn);
        foreach (var memberId in request.MemberIds.Distinct())
            await _repo.InsertMemberAsync(conv.Id, memberId, conn);

        await tx.CommitAsync(ct);

        return new ConversationResponse
        {
            Id          = conv.Id,
            Type        = conv.Type,
            Name        = conv.Name,
            MemberCount = request.MemberIds.Count(),
            CreatedBy   = creatorId,
            CreatedAt   = conv.CreatedAt
        };
    }

    public async Task<PagedApiResponse<ConversationResponse>> GetUserConversationsAsync(
        string targetUserId, string requestingUserId, PaginationRequest query, CancellationToken ct)
    {
        if (targetUserId != requestingUserId)
            throw new UnauthorizedAccessException("Cannot view another user's conversations.");

        var convos = await _repo.GetUserConversationsAsync(targetUserId, query.Limit, query.Cursor);

        return new PagedApiResponse<ConversationResponse>
        {
            Data = convos.Select(c => new ConversationResponse
            {
                Id          = c.Id,
                Type        = c.Type,
                Name        = c.Name,
                MemberCount = c.MemberCount,
                CreatedBy   = string.Empty,
                CreatedAt   = c.CreatedAt,
                LastMessage = c.LastMsgId is null ? null : new LastMessagePreview
                {
                    Content  = c.LastMsgContent ?? "(deleted)",
                    SenderId = c.LastMsgSender ?? string.Empty,
                    SentAt   = c.LastMsgSentAt ?? default
                }
            }),
            Pagination = new PaginationMeta { Limit = query.Limit }
        };
    }
}

// ════════════════════════════════════════════════════════════════════════════
// PRESENCE SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class PresenceService : IPresenceService
{
    private readonly ChatCacheService _cache;

    public PresenceService(ChatCacheService cache) => _cache = cache;

    public async Task SetOnlineAsync(string userId, CancellationToken ct)
        => await _cache.SetOnlineAsync(userId);

    public async Task SetOfflineAsync(string userId, CancellationToken ct)
        => await _cache.SetOfflineAsync(userId);

    public async Task RenewPresenceAsync(string userId, CancellationToken ct)
        => await _cache.RenewPresenceAsync(userId);

    public async Task<bool> IsOnlineAsync(string userId, CancellationToken ct)
        => await _cache.IsOnlineAsync(userId);
}

// ── Chat exceptions ───────────────────────────────────────────────────────────

public class NotConversationMemberException(string userId, string convId)
    : Exception($"User '{userId}' is not a member of conversation '{convId}'.");

public class MessageNotFoundException(string messageId)
    : Exception($"Message '{messageId}' not found.");

public class MessageDeleteException(string messageId)
    : Exception($"Message '{messageId}' could not be deleted. It may already be deleted or not owned by you.");
