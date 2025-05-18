using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;
using VoiceAssistant.Plugins.OpenAI;

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
// Register chat service with streaming support
builder.Services.AddSingleton<VoiceAssistant.Core.Interfaces.IChatService>(sp =>
{
    var httpClient = sp.GetRequiredService<HttpClient>();
    // Use the streaming version of the chat service
    return new VoiceAssistant.Plugins.OpenAI.StreamingOpenAIChatService(httpClient);
});
builder.Services.AddSingleton<VoiceAssistant.Core.Interfaces.IRecognizer, VoiceAssistant.Plugins.OpenAI.OpenAIApiRecognizer>();
// Register the new progressive TTS synthesizer for enhanced latency performance
builder.Services.AddSingleton<VoiceAssistant.Core.Interfaces.ISynthesizer, VoiceAssistant.Plugins.OpenAI.ProgressiveTTSSynthesizer>();
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
// Enhanced with token-by-token streaming for low latency 
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
        logger.LogDebug("Starting speech recognition");
        string prompt;
        await using (var audioStream = file.OpenReadStream())
            prompt = await recognizer.RecognizeAsync(audioStream, file.ContentType, file.FileName);

        logger.LogDebug("Speech recognized: {Prompt}", prompt);

        // Debug: Log prompt length and content
        logger.LogInformation("**** Recognized prompt length: {Length}, Content: '{Content}'",
            prompt?.Length ?? 0,
            prompt?.Substring(0, Math.Min(prompt?.Length ?? 0, 50)));

        chatLogManager.AddMessage(ChatRole.User, prompt);

        // Configure SSE
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Content-Type", "text/event-stream");

        // Send initial prompt event
        await response.WriteAsync($"event: prompt\ndata: {JsonSerializer.Serialize(new { prompt })}\n\n");
        await response.Body.FlushAsync();

        // Generate and send bot response
        if (chatService is StreamingOpenAIChatService streamingService)
        {
            logger.LogDebug("Using true token-streaming for chat response");
            logger.LogInformation("**** Sending chat messages to LLM, count: {Count}", chatLogManager.GetMessages().Count);

            // Use real token-by-token streaming
            string reply = await streamingService.GenerateStreamingResponseAsync(
                chatLogManager.GetMessages(),
                async token =>
                {
                    // Send each token as it arrives for immediate UI feedback
                    await response.WriteAsync($"event: token\ndata: {JsonSerializer.Serialize(new { token })}\n\n");
                    await response.Body.FlushAsync();
                });

            logger.LogInformation("**** Received LLM response, length: {Length}, Content: '{Content}'",
                reply?.Length ?? 0,
                reply?.Substring(0, Math.Min(reply?.Length ?? 0, 50)));

            // Add the complete response to chat history
            chatLogManager.AddMessage(ChatRole.Bot, reply);
        }
        else
        {
            // Fallback for non-streaming implementation
            logger.LogWarning("Falling back to non-streaming chat implementation");

            var reply = await chatService.GenerateResponseAsync(chatLogManager.GetMessages());
            chatLogManager.AddMessage(ChatRole.Bot, reply);

            // Send the full message at once
            await response.WriteAsync($"data: {JsonSerializer.Serialize(new { message = reply })}\n\n");
        }

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

        // Return error as SSE event if possible
        if (!response.HasStarted)
        {
            response.Headers["Content-Type"] = "text/event-stream";
            await response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(new { error = ex.Message })}\n\n");
        }
        else
        {
            response.Headers["Content-Type"] = "text/plain";
            await response.WriteAsync(ex.ToString());
        }
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

// New endpoint for streaming/chunked Text-to-Speech synthesis
// Support both POST (for initial request) and GET (for EventSource)
app.MapMethods("/api/streamingSpeech", new[] { "POST", "GET" }, async (HttpContext context, ISynthesizer synthesizer) =>
{
    try
    {
        var request = context.Request;
        var response = context.Response;

        // Handle both GET (EventSource) and POST (initial request)
        SpeechRequest reqBody;
        
        if (request.Method == "GET")
        {
            // For GET requests (EventSource), look for data in QueryString
            string requestId = context.Request.Query["_"].ToString();
            string inputText = context.Request.Query["text"].ToString();
            string voice = context.Request.Query["voice"].ToString();
            
            // Read from session if available (using request ID as key)
            if (string.IsNullOrEmpty(inputText) && !string.IsNullOrEmpty(requestId))
            {
                logger.LogDebug("Looking for cached request with ID: {RequestId}", requestId);
                // Normally would use IDistributedCache here but we'll keep it simple
                // The request data should have been stored when the POST was made
            }
            
            if (string.IsNullOrEmpty(inputText))
            {
                response.StatusCode = 400;
                await response.WriteAsync("Missing text parameter in query string");
                return;
            }
            
            reqBody = new SpeechRequest(inputText, string.IsNullOrEmpty(voice) ? "nova" : voice);
        }
        else // POST
        {
            // Parse speech request from request body
            reqBody = await request.ReadFromJsonAsync<SpeechRequest>();
            if (reqBody == null)
            {
                response.StatusCode = 400;
                await response.WriteAsync("Invalid request body");
                return;
            }
            
            // Store request data for potential GET requests
            // Would normally use IDistributedCache here
        }

        // Validate input
        if (string.IsNullOrWhiteSpace(reqBody.Input))
        {
            response.StatusCode = 400;
            await response.WriteAsync("Text input cannot be empty");
            return;
        }

        // Check if synthesizer supports chunked synthesis
        if (synthesizer is VoiceAssistant.Plugins.OpenAI.ProgressiveTTSSynthesizer progressiveSynthesizer)
        {
            logger.LogDebug("Using progressive TTS synthesis for text: {Length} chars", reqBody.Input.Length);

            // Configure SSE for streaming chunks
            response.Headers.Add("Cache-Control", "no-cache");
            response.Headers.Add("Content-Type", "text/event-stream");

            // Send initial info event
            var chunkCount = 0;
            await response.WriteAsync($"event: info\ndata: {JsonSerializer.Serialize(new { message = "Starting progressive synthesis" })}\n\n");
            await response.Body.FlushAsync();

            // Process synthesis in chunks with callback
            try
            {
                await progressiveSynthesizer.ChunkedSynthesisAsync(
                    reqBody.Input,
                    reqBody.Voice,
                    async audioBytes =>
                    {
                        chunkCount++;
                        var chunkId = Guid.NewGuid().ToString("N");

                        // Convert audio bytes to Base64 to send via SSE
                        var base64Audio = Convert.ToBase64String(audioBytes);

                        // Send chunk event
                        await response.WriteAsync($"event: chunk\ndata: {JsonSerializer.Serialize(new { chunkId, index = chunkCount, audio = base64Audio })}\n\n");
                        await response.Body.FlushAsync();

                        logger.LogDebug("Sent audio chunk {Index}: {Bytes} bytes", chunkCount, audioBytes.Length);
                    });

                // Signal completion
                await response.WriteAsync($"event: done\ndata: {JsonSerializer.Serialize(new { totalChunks = chunkCount })}\n\n");
                await response.Body.FlushAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during progressive TTS synthesis");
                if (!response.HasStarted)
                {
                    response.StatusCode = 500;
                    await response.WriteAsync($"Error during synthesis: {ex.Message}");
                }
                else
                {
                    // Send error event if streaming has already started
                    await response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(new { error = ex.Message })}\n\n");
                    await response.Body.FlushAsync();
                }
            }
        }
        else
        {
            // Fallback for non-progressive synthesis
            logger.LogDebug("Using standard synthesis (progressive not available)");

            try
            {
                var audio = await synthesizer.SynthesizeAsync(reqBody.Input, reqBody.Voice);
                response.ContentType = "audio/mpeg";
                await response.Body.WriteAsync(audio);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during standard TTS synthesis");
                response.StatusCode = 500;
                await response.WriteAsync($"Error during synthesis: {ex.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled error in /api/streamingSpeech");
        var response = context.Response;
        response.StatusCode = 500;

        if (!response.HasStarted)
        {
            await response.WriteAsync($"Unhandled error: {ex.Message}");
        }
        else
        {
            // Try to send error event if possible
            try
            {
                await response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(new { error = ex.Message })}\n\n");
                await response.Body.FlushAsync();
            }
            catch
            {
                // Last resort fallback
                response.ContentType = "text/plain";
                await response.WriteAsync(ex.ToString());
            }
        }
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

// Endpoint to clear chat history
app.MapPost("/api/clearChat", (ChatLogManager chatLogManager) =>
{
    chatLogManager.ClearMessages();
    logger.LogInformation("Chat history cleared");
    return Results.Ok(new { success = true, message = "Chat history cleared" });
});

// Endpoint for direct text chat streaming (ASR clients)
// Endpoint for text-based chat with full context via IChatService with token-by-token streaming
app.MapPost("/api/chatStream", async (HttpContext context, ChatLogManager chatLogManager, IChatService chatService) =>
{
    try
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

        // Configure SSE
        context.Response.Headers.Add("Cache-Control", "no-cache");
        context.Response.Headers.Add("Content-Type", "text/event-stream");

        // Send initial prompt event
        await context.Response.WriteAsync($"event: prompt\ndata: {JsonSerializer.Serialize(new { prompt })}\n\n");
        await context.Response.Body.FlushAsync();

        // Check if we can use true streaming mode
        if (chatService is StreamingOpenAIChatService streamingService)
        {
            logger.LogDebug("Using true token-streaming for chat response");

            // Use true token-by-token streaming with callbacks
            string reply = await streamingService.GenerateStreamingResponseAsync(
                chatLogManager.GetMessages(),
                async token =>
                {
                    // Send each token as it arrives
                    await context.Response.WriteAsync($"event: token\ndata: {JsonSerializer.Serialize(new { token })}\n\n");
                    await context.Response.Body.FlushAsync();
                });

            // Add the complete response to chat history
            chatLogManager.AddMessage(ChatRole.Bot, reply);

            // Signal completion
            await context.Response.WriteAsync("event: done\ndata: \n\n");
            await context.Response.Body.FlushAsync();
        }
        else
        {
            // Fallback for non-streaming implementations
            logger.LogWarning("Falling back to non-streaming chat implementation");

            // Generate response (non-streaming) and send as single event
            var reply = await chatService.GenerateResponseAsync(chatLogManager.GetMessages());
            chatLogManager.AddMessage(ChatRole.Bot, reply);

            // Send the complete message at once
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { message = reply })}\n\n");

            // Signal done
            await context.Response.WriteAsync("event: done\ndata: \n\n");
            await context.Response.Body.FlushAsync();
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in /api/chatStream endpoint");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"event: error\ndata: {JsonSerializer.Serialize(new { error = ex.Message })}\n\n");
        await context.Response.Body.FlushAsync();
    }
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