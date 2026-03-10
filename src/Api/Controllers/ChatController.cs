using RealtimeChat.Application.Interfaces;
using RealtimeChat.Api.Models.Requests;
using RealtimeChat.Api.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RealtimeChat.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly IConversationService _conversationService;
    private readonly IMessageService _messageService;

    public ChatController(IConversationService conversationService, IMessageService messageService)
    {
        _conversationService = conversationService;
        _messageService = messageService;
    }

    // POST /api/v1/conversations
    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation(
        [FromBody] CreateConversationRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _conversationService.CreateConversationAsync(userId, request, ct);
        return StatusCode(StatusCodes.Status201Created, ApiResponse.Success(result));
    }

    // POST /api/v1/conversations/{id}/messages
    [HttpPost("conversations/{id}/messages")]
    public async Task<IActionResult> SendMessage(
        [FromRoute] string id,
        [FromBody] SendMessageRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _messageService.SendMessageAsync(id, userId, idempotencyKey, request, ct);
        return StatusCode(StatusCodes.Status201Created, ApiResponse.Success(result));
    }

    // GET /api/v1/conversations/{id}/messages
    [HttpGet("conversations/{id}/messages")]
    public async Task<IActionResult> GetMessages(
        [FromRoute] string id, [FromQuery] GetMessagesRequest query, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _messageService.GetMessagesAsync(id, userId, query, ct);
        return Ok(result);
    }

    // GET /api/v1/users/{id}/conversations
    [HttpGet("users/{id}/conversations")]
    public async Task<IActionResult> GetConversations(
        [FromRoute] string id, [FromQuery] PaginationRequest query, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _conversationService.GetUserConversationsAsync(id, userId, query, ct);
        return Ok(result);
    }

    // PATCH /api/v1/messages/{id}/read
    [HttpPatch("messages/{id}/read")]
    public async Task<IActionResult> MarkRead([FromRoute] string id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _messageService.MarkReadAsync(id, userId, ct);
        return Ok(ApiResponse.Success(result));
    }

    // DELETE /api/v1/messages/{id}
    [HttpDelete("messages/{id}")]
    public async Task<IActionResult> DeleteMessage([FromRoute] string id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _messageService.DeleteMessageAsync(id, userId, ct);
        return Ok(ApiResponse.Success(result));
    }
}