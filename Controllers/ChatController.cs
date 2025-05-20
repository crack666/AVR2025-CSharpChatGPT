using Microsoft.AspNetCore.Mvc;
using VoiceAssistant.Core.Services;
using Microsoft.Extensions.Logging;

[ApiController]
[Route("api")]
public class ChatController : ControllerBase
{
    private readonly ChatLogManager _chatLogManager;
    private readonly ILogger<ChatController> _logger;

    public ChatController(ChatLogManager chatLogManager, ILogger<ChatController> logger)
    {
        _chatLogManager = chatLogManager;
        _logger = logger;
    }

    [HttpPost("clearChat")]
    public IActionResult ClearChat()
    {
        _chatLogManager.ClearMessages();
        _logger.LogInformation("Chat history cleared");
        return Ok(new { success = true, message = "Chat history cleared" });
    }
    /// <summary>
    /// Retrieves the full chat log.
    /// </summary>
    [HttpGet("chatLog")]
    public ActionResult<System.Collections.Generic.IReadOnlyList<VoiceAssistant.Core.Models.ChatMessage>> GetChatLog()
    {
        var logs = _chatLogManager.GetMessages();
        return Ok(logs);
    }
}