using System.Text;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using VoiceAssistant.Core.Services;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Plugins.OpenAI;
using Microsoft.Extensions.Logging;

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
// Register core services and plugin implementations
builder.Services.AddSingleton<VoiceAssistant.Core.Services.ChatLogManager>();
builder.Services.AddSingleton<VoiceAssistant.Core.Interfaces.IChatService, VoiceAssistant.Plugins.OpenAI.OpenAIChatService>();
builder.Services.AddSingleton<VoiceAssistant.Core.Interfaces.IRecognizer, VoiceAssistant.Plugins.OpenAI.OpenAIApiRecognizer>();
builder.Services.AddSingleton<VoiceAssistant.Core.Interfaces.ISynthesizer, VoiceAssistant.Plugins.OpenAI.OpenAIApiSynthesizer>();
var app = builder.Build();
var logger = app.Logger;
// Enable detailed exception page for debugging
app.UseDeveloperExceptionPage();

app.UseDefaultFiles();
app.UseStaticFiles();

// Endpoint: process audio to text, then chat with context via IChatService
app.MapPost("/api/processAudio", async (
    HttpRequest request,
    IRecognizer recognizer,
    IChatService chatService,
    ChatLogManager chatLogManager) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null)
        return Results.BadRequest("No file uploaded");

    // Speech-to-text using configured recognizer
    string prompt;
    await using (var stream = file.OpenReadStream())
        prompt = await recognizer.RecognizeAsync(stream, file.ContentType, file.FileName);

    // Add user message to context
    chatLogManager.AddMessage(ChatRole.User, prompt);

    // Generate bot response with full context
    var reply = await chatService.GenerateResponseAsync(chatLogManager.GetMessages());
    chatLogManager.AddMessage(ChatRole.Bot, reply);

    return Results.Json(new { prompt, response = reply });
});
// Streaming endpoint: process audio, transcribe, then stream chat completion tokens via SSE
// Streaming endpoint: process audio, transcribe, then stream chat completion tokens via SSE
app.MapPost("/api/processAudioStream", async (
    HttpContext context,
    IRecognizer recognizer,
    ChatLogManager chatLogManager,
    IChatService chatService) =>
{
    try
    {
        var request = context.Request;
        var response = context.Response;
        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file");
        if (file == null)
        {
            response.StatusCode = 400;
            await response.WriteAsync("No file uploaded");
            return;
        }
        // Speech-to-text
        string prompt;
        await using (var audioStream = file.OpenReadStream())
            prompt = await recognizer.RecognizeAsync(audioStream, file.ContentType, file.FileName);
        chatLogManager.AddMessage(ChatRole.User, prompt);
        // Configure SSE
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Content-Type", "text/event-stream");
        // Send initial prompt event
        await response.WriteAsync($"event: prompt\ndata: {JsonSerializer.Serialize(new { prompt })}\n\n");
        await response.Body.FlushAsync();
        // Generate and send bot response
        var reply = await chatService.GenerateResponseAsync(chatLogManager.GetMessages());
        chatLogManager.AddMessage(ChatRole.Bot, reply);
        await response.WriteAsync($"data: {JsonSerializer.Serialize(new { message = reply })}\n\n");
        // Signal done
        await response.WriteAsync("event: done\ndata: \n\n");
        await response.Body.FlushAsync();
    }
    catch (Exception ex)
    {
        // Log full exception and return detailed error for debugging
        logger.LogError(ex, "Unhandled error in /api/processAudioStream");
        var response = context.Response;
        response.StatusCode = 500;
        response.Headers["Content-Type"] = "text/plain";
        await response.WriteAsync(ex.ToString());
    }
});
// Endpoint for OpenAI Text-to-Speech using models like Juniper or Alloy
// Endpoint for Text-to-Speech using ISynthesizer
app.MapPost("/api/speech", async (SpeechRequest spec, ISynthesizer synthesizer) =>
{
    try
    {
        var audio = await synthesizer.SynthesizeAsync(spec.Input, spec.Voice);
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

// Endpoint for direct text chat streaming (ASR clients)
// Endpoint for text-based chat with full context via IChatService (streaming fallback)
app.MapPost("/api/chatStream", async (HttpContext context, ChatLogManager chatLogManager, IChatService chatService) =>
{
    var chatReq = await JsonSerializer.DeserializeAsync<ChatRequest>(context.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (chatReq?.Prompt == null)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Invalid request: missing prompt.");
        return;
    }
    var prompt = chatReq.Prompt;
    // Add user message
    chatLogManager.AddMessage(ChatRole.User, prompt);

    context.Response.Headers.Add("Cache-Control", "no-cache");
    context.Response.Headers.Add("Content-Type", "text/event-stream");
    // Send initial prompt event
    await context.Response.WriteAsync($"event: prompt\ndata: {JsonSerializer.Serialize(new { prompt })}\n\n");
    await context.Response.Body.FlushAsync();

    // Generate response (non-streaming) and send as single event
    var reply = await chatService.GenerateResponseAsync(chatLogManager.GetMessages());
    chatLogManager.AddMessage(ChatRole.Bot, reply);
    await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { message = reply })}\n\n");
    // Signal done
    await context.Response.WriteAsync("event: done\ndata: \n\n");
    await context.Response.Body.FlushAsync();
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
// Specification for chat request (text-based streaming)
public record ChatRequest(string Model, string Prompt);