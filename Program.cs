using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Please set the OPENAI_API_KEY environment variable.");
    return;
}


var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

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
    return Results.Json(new { prompt, response });
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