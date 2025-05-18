using Microsoft.AspNetCore.Mvc;
using VoiceAssistant.Core.Interfaces;
using Microsoft.Extensions.Logging;

public record SpeechRequest(string Input, string Voice);

[ApiController]
[Route("api")]
public class SpeechController : ControllerBase
{
    private readonly ISynthesizer _synthesizer;
    private readonly ILogger<SpeechController> _logger;

    public SpeechController(ISynthesizer synthesizer, ILogger<SpeechController> logger)
    {
        _synthesizer = synthesizer;
        _logger = logger;
    }

    [HttpPost("speech")]
    public async Task<IActionResult> Speech([FromBody] SpeechRequest spec)
    {
        try
        {
            var audio = await _synthesizer.SynthesizeAsync(spec.Input, spec.Voice);
            return File(audio, "audio/mpeg");
        }
        catch (ApplicationException ex)
        {
            _logger.LogError("TTS application error: {Message}", ex.Message);
            return Problem(detail: ex.Message, statusCode: 400);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected TTS error");
            return Problem(detail: "Internal server error", statusCode: 500);
        }
    }
}