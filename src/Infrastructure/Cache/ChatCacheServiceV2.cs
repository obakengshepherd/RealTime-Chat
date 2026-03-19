using System.Text.Json;
using StackExchange.Redis;

namespace RealtimeChat.Infrastructure.Cache;

/// <summary>
/// Extended Chat cache service with full subscription lifecycle management.
///
/// Redis roles in this system:
///
/// 1. Pub/Sub message delivery bus
///    Channel per conversation: conversation:{id}
///    At-most-once — a missed message is recovered from PostgreSQL on reconnect.
///
/// 2. Presence store
///    Key: presence:{userId}  Value: 1  TTL: 35s
///    Heartbeat every 30s renews TTL. Expired = offline.
///
/// 3. Unread count cache
///    Key: unread:{userId}:{conversationId}  Value: int  TTL: 60s
///    Invalidated when user reads messages.
/// </summary>
public class ChatCacheServiceV2
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ILogger<ChatCacheServiceV2> _logger;

    private static readonly TimeSpan PresenceTtl  = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan UnreadTtl    = TimeSpan.FromSeconds(60);

    public ChatCacheServiceV2(IConnectionMultiplexer redis, ILogger<ChatCacheServiceV2> logger)
    {
        _redis  = redis;
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    // ── Pub/Sub — message delivery bus ───────────────────────────────────────

    /// <summary>
    /// Publishes a message to all WebSocket Gateway instances subscribed to this conversation.
    /// At-most-once delivery — Redis pub/sub does not persist messages.
    /// Failed publish is non-fatal: clients recover via message history on reconnect.
    /// </summary>
    public async Task PublishMessageAsync(string conversationId, ChatPubSubMessage message)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();
            var channel    = ConversationChannel(conversationId);
            var payload    = JsonSerializer.Serialize(message);

            var receiverCount = await subscriber.PublishAsync(channel, payload);

            _logger.LogDebug(
                "Published {EventType} to {Channel} — received by {Count} subscriber(s)",
                message.Type, channel, receiverCount);
        }
        catch (Exception ex)
        {
            // At-most-once: failed publish is explicitly non-fatal
            _logger.LogWarning(ex,
                "Redis pub/sub publish failed for conversation {ConversationId} — clients will recover on reconnect",
                conversationId);
        }
    }

    /// <summary>
    /// Subscribes a WebSocket Gateway instance to a conversation channel.
    /// Call this when a user connects and for each conversation they are a member of.
    /// </summary>
    public async Task SubscribeToConversationAsync(
        string conversationId,
        Action<RedisChannel, RedisValue> messageHandler)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();
            var channel    = ConversationChannel(conversationId);
            await subscriber.SubscribeAsync(channel, messageHandler);

            _logger.LogDebug("Subscribed to conversation channel {Channel}", channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe to conversation {ConversationId}", conversationId);
            throw;
        }
    }

    public async Task UnsubscribeFromConversationAsync(string conversationId)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();
            await subscriber.UnsubscribeAsync(ConversationChannel(conversationId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unsubscribe from conversation {ConversationId}", conversationId);
        }
    }

    // ── Presence management ───────────────────────────────────────────────────

    public async Task SetOnlineAsync(string userId)
    {
        try
        {
            await _db.StringSetAsync(PresenceKey(userId), "1", PresenceTtl);
            // Broadcast presence change to conversation members
            await PublishPresenceChangeAsync(userId, "online");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Set presence online failed for {UserId}", userId); }
    }

    public async Task SetOfflineAsync(string userId)
    {
        try
        {
            await _db.KeyDeleteAsync(PresenceKey(userId));
            await PublishPresenceChangeAsync(userId, "offline");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Set presence offline failed for {UserId}", userId); }
    }

    public async Task RenewPresenceAsync(string userId)
    {
        try { await _db.KeyExpireAsync(PresenceKey(userId), PresenceTtl); }
        catch (Exception ex) { _logger.LogWarning(ex, "Presence renew failed for {UserId}", userId); }
    }

    public async Task<bool> IsOnlineAsync(string userId)
    {
        try { return await _db.KeyExistsAsync(PresenceKey(userId)); }
        catch { return false; }
    }

    public async Task<IEnumerable<string>> GetOnlineUsersFromListAsync(IEnumerable<string> userIds)
    {
        var ids    = userIds.ToList();
        var keys   = ids.Select(id => (RedisKey)PresenceKey(id)).ToArray();
        try
        {
            var values = await _db.StringGetAsync(keys);
            return ids.Where((id, i) => values[i].HasValue).ToList();
        }
        catch { return []; }
    }

    // ── Unread count cache ────────────────────────────────────────────────────

    public async Task InvalidateUnreadCountAsync(string userId, string conversationId)
    {
        try { await _db.KeyDeleteAsync(UnreadKey(userId, conversationId)); }
        catch { /* non-fatal */ }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task PublishPresenceChangeAsync(string userId, string presenceStatus)
    {
        try
        {
            await _db.PublishAsync(
                RedisChannel.Literal($"presence:{userId}"),
                JsonSerializer.Serialize(new { UserId = userId, Status = presenceStatus }));
        }
        catch { /* presence broadcasts are best-effort */ }
    }

    private static RedisChannel ConversationChannel(string conversationId)
        => RedisChannel.Literal($"conversation:{conversationId}");

    private static string PresenceKey(string userId)  => $"presence:{userId}";
    private static string UnreadKey(string userId, string convId) => $"unread:{userId}:{convId}";
}

// ── Shared pub/sub message type ───────────────────────────────────────────────

public record ChatPubSubMessage
{
    public string Type           { get; init; } = string.Empty;  // new_message | message_deleted | presence_changed
    public string? MessageId     { get; init; }
    public string? ConversationId{ get; init; }
    public string? SenderId      { get; init; }
    public string? Content       { get; init; }
    public DateTimeOffset? SentAt{ get; init; }
}
