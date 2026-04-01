using System.Text.Json;
using StackExchange.Redis;

namespace RealtimeChat;

// ════════════════════════════════════════════════════════════════════════════
// CHAT CACHE SERVICE (Redis pub/sub + presence)
// ════════════════════════════════════════════════════════════════════════════
// Provides pub/sub message broadcasting and presence tracking via TTL.

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
