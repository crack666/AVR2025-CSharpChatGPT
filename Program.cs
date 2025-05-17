using System.Net.Http.Headers;
using System.Text.Json;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Please set the OPENAI_API_KEY environment variable.");
    return;
}


var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var logger = app.Logger;

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/processAudio", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var model = form["model"].ToString();
    var language = form["language"].ToString();
    var file = form.Files.GetFile("file");
    if (file == null)
        return Results.BadRequest("No file uploaded");

    // Transcribe directly from uploaded file (preserving original format/content type)
    var prompt = await TranscribeAudio(file, apiKey);
    var response = await GetCompletion(prompt, model, apiKey);
    return Results.Json(new { prompt, response, model });
});
// Endpoint for OpenAI Text-to-Speech using models like Juniper or Alloy
app.MapPost("/api/speech", async (SpeechRequest spec) =>
{
    try
    {
        var audio = await SynthesizeSpeech(spec.Input, spec.Voice, apiKey);
        return Results.File(audio, "audio/mpeg");
    }
    catch (ApplicationException ex)
    {
        logger.LogError("TTS application error: {Message}", ex.Message);
        return Results.Problem(detail: ex.Message, statusCode: 400);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unexpected TTS error");
        return Results.Problem(detail: "Internal server error", statusCode: 500);
    }
});

// Endpoint to list available chat models
app.MapGet("/api/models", async () =>
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    var res = await http.GetAsync("https://api.openai.com/v1/models");
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
    return Results.Json(modelsList);
});

app.Run();

async Task<string> TranscribeAudio(IFormFile file, string apiKey)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    using var multipart = new MultipartFormDataContent();
    multipart.Add(new StringContent("whisper-1"), "model");
    // Read directly from the uploaded file stream
    await using var fileStream = file.OpenReadStream();
    using var fileContent = new StreamContent(fileStream);
    // Use original content type (e.g., audio/webm) for correct format detection
    fileContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
    multipart.Add(fileContent, "file", file.FileName);

    var res = await http.PostAsync("https://api.openai.com/v1/audio/transcriptions", multipart);
    res.EnsureSuccessStatusCode();
    var json = await res.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.GetProperty("text").GetString() ?? string.Empty;
}

async Task<string> GetCompletion(string prompt, string model, string apiKey)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    var payload = JsonSerializer.Serialize(new
    {
        model = model,
        messages = new object[] { new { role = "user", content = prompt } }
    });
    using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
    var res = await http.PostAsync("https://api.openai.com/v1/chat/completions", content);
    res.EnsureSuccessStatusCode();
    var json = await res.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.GetProperty("choices")[0]
        .GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
}

// OpenAI Text-to-Speech synthesis using the /v1/audio/speech endpoint
async Task<byte[]> SynthesizeSpeech(string text, string voice, string apiKey)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    http.DefaultRequestHeaders.Accept.Clear();
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

    // Prepare JSON payload for TTS
    var payload = new { model = "tts-1", voice = voice, input = text };
    var body = JsonSerializer.Serialize(payload);
    using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
    logger.LogDebug("Sending TTS request: {Payload}", body);

    var res = await http.PostAsync("https://api.openai.com/v1/audio/speech", content);
    var responseBytes = await res.Content.ReadAsByteArrayAsync();
    if (!res.IsSuccessStatusCode)
    {
        var respText = System.Text.Encoding.UTF8.GetString(responseBytes);
        logger.LogError("TTS request failed. Status: {Status}, Body: {Body}", res.StatusCode, respText);
        throw new ApplicationException($"TTS error: {respText}");
    }
    logger.LogDebug("Received TTS audio bytes: {Size} bytes", responseBytes.Length);
    return responseBytes;
}

// Specification for speech synthesis request
public record SpeechRequest(string Input, string Voice);