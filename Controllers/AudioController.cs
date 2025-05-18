using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;

[ApiController]
[Route("api")]
public class AudioController : ControllerBase
{
    private readonly IRecognizer _recognizer;
    private readonly IChatService _chatService;
    private readonly ChatLogManager _chatLogManager;
    private readonly ILogger<AudioController> _logger;

    public AudioController(
        IRecognizer recognizer,
        IChatService chatService,
        ChatLogManager chatLogManager,
        ILogger<AudioController> logger)
    {
        _recognizer = recognizer;
        _chatService = chatService;
        _chatLogManager = chatLogManager;
        _logger = logger;
    }

    [HttpPost("processAudio")]
    public async Task<IActionResult> ProcessAudio([FromForm] IFormFile file)
    {
        if (file == null)
            return BadRequest("No file uploaded");

        string prompt;
        await using (var stream = file.OpenReadStream())
        {
            prompt = await _recognizer.RecognizeAsync(stream, file.ContentType, file.FileName);
        }
        _logger.LogInformation("ProcessAudio recognized prompt: {Prompt} (length {Length})", prompt, prompt.Length);

        _chatLogManager.AddMessage(ChatRole.User, prompt);
        var reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages());
        _chatLogManager.AddMessage(ChatRole.Bot, reply);

        return new JsonResult(new { prompt, response = reply });
    }
}