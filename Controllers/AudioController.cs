using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;
using Microsoft.Extensions.Logging; // Keep existing using

[ApiController]
[Route("api")]
public class AudioController : ControllerBase
{
    private readonly IRecognizer _recognizer;
    private readonly IChatService _chatService;
    private readonly ChatLogManager _chatLogManager;
    private readonly ILogger<AudioController> _logger;
    private readonly PipelineOptions _globalPipelineOptions; // Added for default settings

    public AudioController(
        IRecognizer recognizer,
        IChatService chatService,
        ChatLogManager chatLogManager,
        ILogger<AudioController> logger,
        PipelineOptions globalPipelineOptions) // Injected PipelineOptions
    {
        _recognizer = recognizer;
        _chatService = chatService;
        _chatLogManager = chatLogManager;
        _logger = logger;
        _globalPipelineOptions = globalPipelineOptions; // Store injected options
    }

    [HttpPost("processAudio")]
    public async Task<IActionResult> ProcessAudio([FromForm] IFormFile file)
    {
        if (file == null)
            return BadRequest("No file uploaded");

        // Determine language to use
        string? languageFromRequest = Request.Form["language"].ToString();
        string languageToUse = !string.IsNullOrEmpty(languageFromRequest) ? languageFromRequest : _globalPipelineOptions.Language;
        _logger.LogInformation("AudioController: Using language: {Language}", languageToUse);

        // Determine chat model to use
        string? modelFromRequest = Request.Form["model"].ToString();
        string chatModelToUse = !string.IsNullOrEmpty(modelFromRequest) ? modelFromRequest : _globalPipelineOptions.ChatModel;
        _logger.LogInformation("AudioController: Using chat model: {ChatModel}", chatModelToUse);

        string prompt;
        await using (var stream = file.OpenReadStream())
        {
            // Pass languageToUse to RecognizeAsync
            prompt = await _recognizer.RecognizeAsync(stream, file.ContentType, file.FileName, languageToUse);
        }
        _logger.LogInformation("ProcessAudio recognized prompt: {Prompt} (length {Length}) using language {Language}", prompt, prompt.Length, languageToUse);

        _chatLogManager.AddMessage(ChatRole.User, prompt);
        // Pass chatModelToUse to GenerateResponseAsync
        var reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages(), chatModelToUse);
        var botMsg = _chatLogManager.AddMessage(ChatRole.Bot, reply);
        
        // Annotate metadata for UI
        botMsg.Model = chatModelToUse; // Use the resolved chatModelToUse
        botMsg.Voice = null; // This controller does not handle TTS, so voice is not applicable here.

        return new JsonResult(new { prompt, response = reply, model = chatModelToUse, language = languageToUse });
    }
}