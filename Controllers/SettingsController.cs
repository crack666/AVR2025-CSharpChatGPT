using Microsoft.AspNetCore.Mvc;
using VoiceAssistant.Core.Models;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly VadSettings _vadSettings;
    private readonly PipelineOptions _pipelineOptions;

    public SettingsController(VadSettings vadSettings, PipelineOptions pipelineOptions)
    {
        _vadSettings = vadSettings;
        _pipelineOptions = pipelineOptions;
    }

    [HttpPut]
    public IActionResult Update([FromBody] VadSettings dto)
    {
        // WebRTC-VAD Mode immer �bernehmen
        _vadSettings.OperatingMode = dto.OperatingMode;

        // Statische Vorverstärkung (Pre-Amplification)
        if (dto.PreAmplification > 0f)
            _vadSettings.PreAmplification = dto.PreAmplification;

        // Mindest-Sprechdauer f�r Start
        if (dto.MinSpeechDurationSec > 0)
            _vadSettings.MinSpeechDurationSec = dto.MinSpeechDurationSec;

        // Pre-Roll Puffer
        if (dto.PreSpeechDurationSec > 0)
            _vadSettings.PreSpeechDurationSec = dto.PreSpeechDurationSec;

        // Hangover-Dauer f�r Ende
        if (dto.HangoverDurationSec > 0)
            _vadSettings.HangoverDurationSec = dto.HangoverDurationSec;

        // Mindestsegmentdauer (Post-Filter)
        if (dto.MinSegmentDurationSec > 0)
            _vadSettings.MinSegmentDurationSec = dto.MinSegmentDurationSec;

        // Dynamischer RMS-Schwellwert (Noise-Factor)
        if (dto.NoiseThresholdFactor > 0)
            _vadSettings.NoiseThresholdFactor = dto.NoiseThresholdFactor;

        // EMA-Alpha f�r Noise-Floor-Schätzung (0 < α < 1)
        if (dto.NoiseFloorAlpha > 0 && dto.NoiseFloorAlpha < 1)
            _vadSettings.NoiseFloorAlpha = dto.NoiseFloorAlpha;

        // Untergrenze f�r Noise-Floor
        if (dto.MinNoiseFloor > 0)
            _vadSettings.MinNoiseFloor = dto.MinNoiseFloor;

        // Wie lange Stille bevor sich Noise-Floor anpasst
        if (dto.SilenceAdaptationTimeSec > 0)
            _vadSettings.SilenceAdaptationTimeSec = dto.SilenceAdaptationTimeSec;

        return NoContent();
    }

    /// <summary>
    /// Get current pipeline feature flags.
    /// </summary>
    [HttpGet("pipeline")]
    public ActionResult<PipelineOptions> GetPipelineOptions()
        => Ok(_pipelineOptions);

    /// <summary>
    /// Update pipeline feature flags.
    /// </summary>
    [HttpPut("pipeline")]
    public IActionResult UpdatePipelineOptions([FromBody] PipelineOptions dto)
    {
        _pipelineOptions.UseLegacyHttp = dto.UseLegacyHttp;
        _pipelineOptions.DisableVad = dto.DisableVad;
        _pipelineOptions.DisableTokenStreaming = dto.DisableTokenStreaming;
        _pipelineOptions.DisableProgressiveTts = dto.DisableProgressiveTts;
        _pipelineOptions.ChatModel = dto.ChatModel;
        _pipelineOptions.TtsVoice = dto.TtsVoice;
        return NoContent();
    }
    /// <summary>
    /// Calibrate VAD thresholds based on uploaded WAV sample.
    /// </summary>
    [HttpPost("vad/calibrate")]
    public async Task<ActionResult<VadSettings>> CalibrateAsync([FromForm] IFormFile file)
    {
        if (file == null)
            return BadRequest("No audio file provided.");
        byte[] data;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            data = ms.ToArray();
        }
        const int headerSize = 44;
        if (data.Length <= headerSize)
            return BadRequest("Invalid WAV file.");
        int sampleCount = (data.Length - headerSize) / 2;
        double sumSquares = 0;
        for (int i = headerSize; i + 1 < data.Length; i += 2)
        {
            short sample = BitConverter.ToInt16(data, i);
            double norm = sample / 32768.0;
            sumSquares += norm * norm;
        }
        double avgRms = Math.Sqrt(sumSquares / sampleCount);
        // Derive thresholds
        /*
        _vadSettings.StartThreshold = avgRms * 1.5;
        _vadSettings.EndThreshold = avgRms * 1.0;
        */
        // Leave other settings unchanged
        return Ok(_vadSettings);
    }
}