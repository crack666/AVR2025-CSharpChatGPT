using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

[ApiController]
[Route("api")]
public class ModelsController : ControllerBase
{
    private readonly HttpClient _httpClient;

    public ModelsController(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    [HttpGet("models")]
    public async Task<IActionResult> GetModels()
    {
        var res = await _httpClient.GetAsync("https://api.openai.com/v1/models");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var modelSet = new HashSet<string>();
        foreach (var m in data.EnumerateArray())
        {
            if (!m.TryGetProperty("id", out var idProp))
                continue;
            var id = idProp.GetString();
            if (string.IsNullOrEmpty(id) || !id.StartsWith("gpt-"))
                continue;
            modelSet.Add(id);
        }
        var modelsList = modelSet.ToList();
        modelsList.Sort(StringComparer.Ordinal);
        return new JsonResult(modelsList);
    }
}