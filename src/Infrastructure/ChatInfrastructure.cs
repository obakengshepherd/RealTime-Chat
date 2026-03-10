namespace RealtimeChat.Infrastructure.Persistence;
/// <summary>Message + Conversation repositories — PostgreSQL. Implemented Day 9.</summary>
public class MessageRepository { }
public class ConversationRepository { }

namespace RealtimeChat.Infrastructure.Cache;
/// <summary>
/// Redis pub/sub publisher. Channel: conversation:{id}
/// Presence keys: presence:{user_id} EX 35
/// Implemented Day 17.
/// </summary>
public class ChatCacheService { }

namespace RealtimeChat.Api;
using System.Security.Claims;
public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal p)
    {
        var id = p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(id)) throw new UnauthorizedAccessException("User ID claim missing.");
        return id;
    }
}
