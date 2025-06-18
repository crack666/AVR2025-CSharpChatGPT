using Serilog;
using System.Net;
using System.Net.Http.Headers;
using VoiceAssistant; // This should cover the new classes if they are in this namespace
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;

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
        .Enrich.FromLogContext();
    // Note: Don't add WriteTo.Console() here - already configured in appsettings.json
});
// Bind and register pipeline feature flags as mutable singleton (GLOBAL DEFAULT)
var globalPipelineOptions = new PipelineOptions();
builder.Configuration.GetSection("PipelineOptions").Bind(globalPipelineOptions);
builder.Services.AddSingleton(globalPipelineOptions); // Global default

// Register global default VadSettings as singleton
var globalVadSettings = new VadSettings();
builder.Configuration.GetSection("VadSettings").Bind(globalVadSettings);
builder.Services.AddSingleton(globalVadSettings); // Global default

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
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        Timeout = TimeSpan.FromSeconds(180) // Increased timeout to 3 minutes
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
builder.Services.AddSingleton<VoiceAssistant.Core.Interfaces.ISynthesizer, VoiceAssistant.Plugins.OpenAI.ProgressiveTTSSynthesizer>();

// Register new services
// These are scoped per WebSocket connection because they hold session-specific state or dependencies.
builder.Services.AddScoped<IWebSocketSettingsManager, WebSocketSettingsManager>();

// Register session-specific options as scoped services.
// These will be configured per-request in the WebSocket endpoint mapping.
builder.Services.AddScoped<PipelineOptions>();
builder.Services.AddScoped<VadSettings>();

// AudioFrameProcessor needs initial VadSettings and PipelineOptions for its construction.
// These will be the session-specific ones.
builder.Services.AddScoped<IAudioFrameProcessor, AudioFrameProcessor>(
sp => new AudioFrameProcessor(
    sp.GetRequiredService<ILogger<AudioFrameProcessor>>(),
    sp.GetRequiredService<VadSettings>(), // Resolves the SCOPED VadSettings
    sp.GetRequiredService<PipelineOptions>() // Resolves the SCOPED PipelineOptions
));

builder.Services.AddScoped<IAudioSegmentProcessor, AudioSegmentProcessor>();

// WebSocketHandler is also scoped as it orchestrates other scoped services and handles a single WebSocket session.
// It will receive session-specific PipelineOptions and VadSettings upon creation.
builder.Services.AddScoped<IWebSocketHandler, WebSocketHandler>(
sp => new WebSocketHandler(
    sp.GetRequiredService<ILogger<WebSocketHandler>>(),
    sp.GetRequiredService<IAudioFrameProcessor>(),
    sp.GetRequiredService<IAudioSegmentProcessor>(),
    sp.GetRequiredService<IWebSocketSettingsManager>(),
    sp.GetRequiredService<PipelineOptions>(), // Resolves the SCOPED PipelineOptions
    sp.GetRequiredService<VadSettings>()      // Resolves the SCOPED VadSettings
));

// WebSocketAudioService might still be a singleton if it only acts as a lightweight factory or entry point.
// However, if it directly uses scoped services like IWebSocketHandler, it should also be scoped.
// For this refactoring, WebSocketAudioService will be simplified to mostly delegate to IWebSocketHandler.
// Let's make WebSocketAudioService scoped as well if it resolves IWebSocketHandler.
//builder.Services.AddScoped<WebSocketAudioService>();

// PipelineOptions are already registered as a singleton for global defaults.

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
    if (context.WebSockets.IsWebSocketRequest)
    {
        using (var scope = app.Services.CreateScope())
        {
            var serviceProvider = scope.ServiceProvider;

            // Get the scoped PipelineOptions and VadSettings instances for this session
            var sessionPipelineOptions = serviceProvider.GetRequiredService<PipelineOptions>();
            var sessionVadSettings = serviceProvider.GetRequiredService<VadSettings>();

            // Initialize sessionPipelineOptions from the captured global singletons
            sessionPipelineOptions.CopyFrom(globalPipelineOptions); // Start with global defaults

            var modelQuery = context.Request.Query["model"].ToString();
            if (!string.IsNullOrEmpty(modelQuery)) sessionPipelineOptions.ChatModel = modelQuery;
            var voiceQuery = context.Request.Query["voice"].ToString();
            if (!string.IsNullOrEmpty(voiceQuery)) sessionPipelineOptions.TtsVoice = voiceQuery;
            var languageQuery = context.Request.Query["language"].ToString();
            if (!string.IsNullOrEmpty(languageQuery)) sessionPipelineOptions.Language = languageQuery;

            // Initialize sessionVadSettings from the captured global singleton
            sessionVadSettings.CopyFrom(globalVadSettings); // Use CopyFrom method

            var tempLogger = serviceProvider.GetRequiredService<ILogger<Program>>();
            tempLogger.LogInformation("WebSocket session starting with PipelineOptions: ChatModel={ChatModel}, TtsVoice={TtsVoice}, Language={Language}",
                                    sessionPipelineOptions.ChatModel, sessionPipelineOptions.TtsVoice, sessionPipelineOptions.Language);

            var sessionId = Guid.NewGuid().ToString("N")[..8];
            var webSocketHandler = serviceProvider.GetRequiredService<IWebSocketHandler>();
            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await webSocketHandler.HandleAsync(webSocket, sessionId);
        }
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