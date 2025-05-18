using Microsoft.AspNetCore.Mvc;
using VoiceAssistant.Core.Models;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly VadSettings _vadSettings;

    public SettingsController(VadSettings vadSettings)
        => _vadSettings = vadSettings;

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
}