using Serilog;
using System.Net;
using System.Net.Http.Headers;
using VoiceAssistant;
using VoiceAssistant.Core.Models;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Please set the OPENAI_API_KEY environment variable.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
// Remove default logging providers and configure Serilog from appsettings.json
builder.Logging.ClearProviders();
builder.Host.UseSerilog((hostingContext, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(hostingContext.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console();
});
// Bind and register pipeline feature flags as mutable singleton
var pipelineOptions = new PipelineOptions();
builder.Configuration.GetSection("PipelineOptions").Bind(pipelineOptions);
builder.Services.AddSingleton(pipelineOptions);

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
// VAD settings for voice-activity detection (threshold, silence, min speech duration)
builder.Services.AddSingleton<VadSettings>();
// WebSocket-based audio streaming and VAD service
builder.Services.AddSingleton<WebSocketAudioService>();
builder.Services.AddControllers();
var app = builder.Build();
var logger = app.Logger;
// Enable detailed exception page for debugging
app.UseDeveloperExceptionPage();
// Enable WebSocket support for backend VAD and audio streaming
app.UseWebSockets();
// Map WebSocket endpoint for real-time audio streaming
app.Map("/ws/audio", async context =>
{
    var pipelineOptions = context.RequestServices.GetRequiredService<PipelineOptions>();
    if (pipelineOptions.UseLegacyHttp)
    {
        // Legacy HTTP mode: do not accept WebSocket
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    if (context.WebSockets.IsWebSocketRequest)
    {
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var service = context.RequestServices.GetRequiredService<WebSocketAudioService>();
        // Read desired chat model from query, default gpt-3.5-turbo
        /*
        var model = context.Request.Query["model"].ToString();
        service.ChatModel = string.IsNullOrEmpty(model) ? "gpt-3.5-turbo" : model;
        // Read desired TTS voice from query, default nova
        var voice = context.Request.Query["voice"].ToString();
        service.TtsVoice = string.IsNullOrEmpty(voice) ? "nova" : voice;
        */
        await service.HandleAsync(webSocket);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});
// Serve static files and SPA
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.Run();