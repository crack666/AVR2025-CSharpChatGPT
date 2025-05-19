using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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

    [HttpGet]
    public ActionResult<VadSettings> Get()
        => Ok(_vadSettings);

    [HttpPut]
    public IActionResult Update([FromBody] VadSettings dto)
    {
        _vadSettings.Threshold           = dto.Threshold;
        _vadSettings.SilenceTimeoutSec   = dto.SilenceTimeoutSec;
        _vadSettings.MinSpeechDurationSec= dto.MinSpeechDurationSec;
        // only override pre-buffer if provided (>0)
        if (dto.PreSpeechDurationSec > 0)
            _vadSettings.PreSpeechDurationSec = dto.PreSpeechDurationSec;
        // override smoothing window (EMA) if provided (>0)
        if (dto.RmsSmoothingWindowSec > 0)
            _vadSettings.RmsSmoothingWindowSec = dto.RmsSmoothingWindowSec;
        // override hysteresis thresholds if provided (>0)
        if (dto.StartThreshold > 0)
            _vadSettings.StartThreshold = dto.StartThreshold;
        if (dto.EndThreshold > 0)
            _vadSettings.EndThreshold = dto.EndThreshold;
        // override hang-over duration if provided (>0)
        if (dto.HangoverDurationSec > 0)
            _vadSettings.HangoverDurationSec = dto.HangoverDurationSec;
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
        _vadSettings.StartThreshold = avgRms * 1.5;
        _vadSettings.EndThreshold = avgRms * 1.0;
        // Leave other settings unchanged
        return Ok(_vadSettings);
    }
}