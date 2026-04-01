using System.Net;
using System.Text.Json;

namespace RealtimeChat;

// ════════════════════════════════════════════════════════════════════════════
// GLOBAL EXCEPTION HANDLER MIDDLEWARE
// ════════════════════════════════════════════════════════════════════════════
// Catches all unhandled exceptions and returns standardized error responses.

public class GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex, logger);
        }
    }

    private static Task HandleExceptionAsync(
        HttpContext context, Exception exception, ILogger<GlobalExceptionHandlerMiddleware> logger)
    {
        var correlationId = context.Items["CorrelationId"]?.ToString() ?? context.TraceIdentifier;

        var response = context.Response;
        response.ContentType = "application/json";

        var (statusCode, message, details) = exception switch
        {
            NotConversationMemberException ex => (
                (int)HttpStatusCode.Forbidden,
                "Access Denied",
                ex.Message),

            MessageNotFoundException ex => (
                (int)HttpStatusCode.NotFound,
                "Not Found",
                ex.Message),

            MessageDeleteException ex => (
                (int)HttpStatusCode.BadRequest,
                "Invalid Operation",
                ex.Message),

            UnauthorizedAccessException ex => (
                (int)HttpStatusCode.Unauthorized,
                "Unauthorized",
                ex.Message),

            ArgumentException ex => (
                (int)HttpStatusCode.BadRequest,
                "Invalid Request",
                ex.Message),

            InvalidOperationException ex => (
                (int)HttpStatusCode.InternalServerError,
                "Server Error",
                ex.Message),

            _ => (
                (int)HttpStatusCode.InternalServerError,
                "Internal Server Error",
                "An unexpected error occurred. Please contact support.")
        };

        logger.LogError(exception, "Unhandled exception: {Message} | CorrelationId: {CorrelationId}",
            exception.Message, correlationId);

        response.StatusCode = statusCode;

        var errorResponse = new
        {
            status = statusCode,
            error = new
            {
                message = message,
                details = details,
                timestamp = DateTimeOffset.UtcNow,
                correlationId = correlationId
            }
        };

        return response.WriteAsJsonAsync(errorResponse);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// CUSTOM CHAT EXCEPTIONS
// ════════════════════════════════════════════════════════════════════════════

public class NotConversationMemberException(string userId, string convId)
    : Exception($"User '{userId}' is not a member of conversation '{convId}'.");

public class MessageNotFoundException(string messageId)
    : Exception($"Message '{messageId}' not found.");

public class MessageDeleteException(string messageId)
    : Exception($"Message '{messageId}' could not be deleted. It may already be deleted or not owned by you.");
