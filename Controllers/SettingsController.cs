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
        _vadSettings.Threshold = dto.Threshold;
        _vadSettings.SilenceTimeoutSec = dto.SilenceTimeoutSec;
        _vadSettings.MinSpeechDurationSec = dto.MinSpeechDurationSec;
        return NoContent();
    }
}