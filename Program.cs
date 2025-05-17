using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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

// Configure shared HTTP/2 HttpClient as singleton with persistent connections
builder.Services.AddSingleton(sp =>
{
    var handler = new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };
    var client = new HttpClient(handler)
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
    };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    return client;
});
var app = builder.Build();
var logger = app.Logger;

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/processAudio", async (HttpRequest request, HttpClient http) =>
{
    var form = await request.ReadFormAsync();
    var model = form["model"].ToString();
    var language = form["language"].ToString();
    var file = form.Files.GetFile("file");
    if (file == null)
        return Results.BadRequest("No file uploaded");

    // Transcribe directly from uploaded file (preserving original format/content type)
    var prompt = await TranscribeAudio(file, http);
    var response = await GetCompletion(prompt, model, http);
    return Results.Json(new { prompt, response, model });
});
// Streaming endpoint: process audio, transcribe, then stream chat completion tokens via SSE
app.MapPost("/api/processAudioStream", async (HttpContext context, HttpClient http) =>
{
    var request = context.Request;
    var response = context.Response;
    var form = await request.ReadFormAsync();
    var model = form["model"].ToString();
    var language = form["language"].ToString();
    var file = form.Files.GetFile("file");
    if (file == null)
    {
        response.StatusCode = 400;
        await response.WriteAsync("No file uploaded");
        return;
    }
    // Transcribe audio
    var prompt = await TranscribeAudio(file, http);
    // Configure SSE response
    response.Headers.Add("Cache-Control", "no-cache");
    response.Headers.Add("Content-Type", "text/event-stream");
    // Send initial prompt event
    await response.WriteAsync($"event: prompt\ndata: {JsonSerializer.Serialize(new { prompt, model })}\n\n");
    await response.Body.FlushAsync();
    // Prepare OpenAI streaming request using shared HTTP/2 client
    var chatRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
    var chatPayload = JsonSerializer.Serialize(new
    {
        model = model,
        messages = new object[] { new { role = "user", content = prompt } },
        stream = true
    });
    chatRequest.Content = new StringContent(chatPayload, System.Text.Encoding.UTF8, "application/json");
    chatRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    var openAiRes = await http.SendAsync(chatRequest, HttpCompletionOption.ResponseHeadersRead);
    openAiRes.EnsureSuccessStatusCode();
    using var stream = await openAiRes.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);
    // Read streaming data line by line
    while (!reader.EndOfStream)
    {
        var line = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(line)) continue;
        const string prefix = "data: ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
        var data = line[prefix.Length..];
        if (data.Trim() == "[DONE]")
        {
            // Signal done
            await response.WriteAsync("event: done\ndata: \n\n");
            await response.Body.FlushAsync();
            break;
        }
        try
        {
            using var doc = JsonDocument.Parse(data);
            var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var contentProp))
            {
                var token = contentProp.GetString();
                if (!string.IsNullOrEmpty(token))
                {
                    // Send token event
                    await response.WriteAsync($"data: {JsonSerializer.Serialize(new { token })}\n\n");
                    await response.Body.FlushAsync();
                }
            }
        }
        catch { }
    }
});
// Endpoint for OpenAI Text-to-Speech using models like Juniper or Alloy
app.MapPost("/api/speech", async (SpeechRequest spec, HttpClient http) =>
{
    try
    {
        var audio = await SynthesizeSpeech(spec.Input, spec.Voice, http);
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
app.MapGet("/api/models", async (HttpClient http) =>
{
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

async Task<string> TranscribeAudio(IFormFile file, HttpClient http)
{
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

async Task<string> GetCompletion(string prompt, string model, HttpClient http)
{
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
async Task<byte[]> SynthesizeSpeech(string text, string voice, HttpClient http)
{
    // Prepare request with proper Accept header for audio
    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
    req.Headers.Accept.Clear();
    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

    // Prepare JSON payload for TTS
    var payload = new { model = "tts-1", voice = voice, input = text };
    var body = JsonSerializer.Serialize(payload);
    using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
    logger.LogDebug("Sending TTS request: {Payload}", body);

    req.Content = content;
    var res = await http.SendAsync(req);
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