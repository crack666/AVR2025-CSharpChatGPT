#nullable enable
using Microsoft.AspNetCore.Mvc;
using VoiceAssistant.Core.Interfaces;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Core.Models; // Added for PipelineOptions

public record SpeechRequest(string Input, string Voice, string? Language); // Added Language

[ApiController]
[Route("api")]
public class SpeechController : ControllerBase
{
    private readonly ISynthesizer _synthesizer;
    private readonly ILogger<SpeechController> _logger;
    private readonly PipelineOptions _globalPipelineOptions; // Added for default settings

    public SpeechController(ISynthesizer synthesizer, ILogger<SpeechController> logger, PipelineOptions globalPipelineOptions) // Injected
    {
        _synthesizer = synthesizer;
        _logger = logger;
        _globalPipelineOptions = globalPipelineOptions; // Store
    }

    [HttpPost("speech")]
    public async Task<IActionResult> Speech([FromBody] SpeechRequest spec)
    {
        try
        {
            // Determine voice to use
            string voiceToUse = !string.IsNullOrEmpty(spec.Voice) ? spec.Voice : _globalPipelineOptions.TtsVoice;
            _logger.LogInformation("SpeechController: Synthesizing with voice: {Voice}", voiceToUse);
            
            // Note: ISynthesizer.SynthesizeAsync and ChunkedSynthesisAsync already accept 'voice'.
            // The 'language' parameter in SpeechRequest is not directly used by ISynthesizer in the current OpenAI implementation,
            // as OpenAI TTS voices are typically multilingual or language-auto-detecting for the *input text*.
            // However, if a synthesizer implementation *did* require a language hint for voice selection or pronunciation rules,
            // it would be passed here. For now, we log it if provided.
            if (!string.IsNullOrEmpty(spec.Language))
            {
                _logger.LogInformation("SpeechController: Language hint provided: {Language}", spec.Language);
            }

            var audio = await _synthesizer.SynthesizeAsync(spec.Input, voiceToUse);
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