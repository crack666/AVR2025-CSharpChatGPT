# VR-Latenzoptimierung: Implementierungsleitfaden

Dieses Dokument enthält konkrete Implementierungsbeispiele für die zusätzlichen Latenzoptimierungen des Refactoring-Plans, speziell für die Meta Quest 3 VR-Integration.

## 1. StreamingOpenAIChatService

Diese erweiterte Version des OpenAIChatService implementiert echtes Token-Streaming und bietet Callbacks für sofortige UI-Updates.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant.Plugins.OpenAI
{
    public class StreamingOpenAIChatService : IChatService
    {
        private readonly HttpClient _httpClient;
        private readonly Action<string> _onTokenReceived;
        
        public StreamingOpenAIChatService(HttpClient httpClient, Action<string> onTokenReceived = null)
        {
            _httpClient = httpClient;
            _onTokenReceived = onTokenReceived;
        }
        
        public async Task<string> GenerateResponseAsync(IEnumerable<ChatMessage> chatHistory)
        {
            // Wenn kein Token-Callback registriert ist, verwende nicht-streamende Implementierung
            if (_onTokenReceived == null)
            {
                return await GenerateNonStreamingResponseAsync(chatHistory);
            }
            
            // Sonst verwende Streaming-Implementierung
            return await GenerateStreamingResponseAsync(chatHistory);
        }
        
        private async Task<string> GenerateNonStreamingResponseAsync(IEnumerable<ChatMessage> chatHistory)
        {
            // Bestehendes Verhalten aus OpenAIChatService beibehalten
            var messages = chatHistory.Select(msg => new {
                role = msg.Role == ChatRole.User ? "user" : "assistant",
                content = msg.Content
            });
            
            var payload = new {
                model = "gpt-3.5-turbo",
                messages = messages.ToArray(),
                stream = false
            };
            
            var jsonPayload = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
                
            return content ?? string.Empty;
        }
        
        private async Task<string> GenerateStreamingResponseAsync(IEnumerable<ChatMessage> chatHistory)
        {
            var messages = chatHistory.Select(msg => new {
                role = msg.Role == ChatRole.User ? "user" : "assistant",
                content = msg.Content
            });
            
            var payload = new {
                model = "gpt-3.5-turbo",
                messages = messages.ToArray(),
                stream = true // Aktiviere Streaming
            };
            
            var jsonPayload = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            
            // ResponseHeadersRead für frühe Verarbeitung verwenden
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            
            var fullResponse = new StringBuilder();
            string line;
            
            while ((line = await reader.ReadLineAsync()) != null)
            {
                // SSE Format: "data: {...}" oder "data: [DONE]"
                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6);
                    if (data == "[DONE]")
                        break;
                    
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                            choices.GetArrayLength() > 0 &&
                            choices[0].TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrEmpty(token))
                            {
                                fullResponse.Append(token);
                                // Callback für UI-Update
                                _onTokenReceived(token);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignoriere ungültiges JSON (z.B. Heartbeats)
                        continue;
                    }
                }
            }
            
            return fullResponse.ToString();
        }
    }
}
```

## 2. UnityMainThreadDispatcher

Diese Komponente stellt sicher, dass Callbacks von Hintergrund-Threads sicher im Unity-Hauptthread ausgeführt werden:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace VoiceAssistant.UnityIntegration
{
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly Queue<Action> _executionQueue = new Queue<Action>();
        private static SynchronizationContext _unityContext;
        
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
            DontDestroyOnLoad(gameObject);
            _unityContext = SynchronizationContext.Current;
        }
        
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            
            lock (_executionQueue)
            {
                _executionQueue.Enqueue(action);
            }
        }
        
        private void Update()
        {
            lock (_executionQueue)
            {
                while (_executionQueue.Count > 0)
                {
                    var action = _executionQueue.Dequeue();
                    action.Invoke();
                }
            }
        }
        
        public static void ExecuteOnMainThread(Action action)
        {
            if (Thread.CurrentThread == _unityContext.CreateCopy().TargetThread)
            {
                // Wenn bereits im Main Thread, direkt ausführen
                action();
            }
            else
            {
                // Sonst zur Queue hinzufügen
                Enqueue(action);
            }
        }
    }
}
```

## 3. OptimizedVoicePlaybackManager

Eine erweiterte Version des VoicePlaybackManager mit Unterstützung für Queue-basierte Wiedergabe und Audio-Optimierungen:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace VoiceAssistant.UnityIntegration
{
    [RequireComponent(typeof(AudioSource))]
    public class OptimizedVoicePlaybackManager : MonoBehaviour
    {
        private AudioSource _audioSource;
        private Queue<AudioClip> _clipQueue = new Queue<AudioClip>();
        
        // Optimale Unity-Audio-Einstellungen für Quest 3
        [SerializeField] private int _dspBufferSize = 256;
        [SerializeField] private int _dspBufferCount = 2;
        
        // Optional: Optimierungen für Voice-Clips
        [SerializeField] private bool _optimizeVoiceClips = true;
        [SerializeField] private int _targetSampleRate = 22050; // 22.05kHz für Sprache
        
        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            
            // DSP-Buffer-Größe optimieren für niedrigere Latenz
            AudioSettings.SetDSPBufferSize(_dspBufferSize, _dspBufferCount);
        }
        
        private void Update()
        {
            // Automatisch nächsten Clip abspielen, wenn aktueller beendet
            if (!_audioSource.isPlaying && _clipQueue.Count > 0)
            {
                PlayNextInQueue();
            }
        }
        
        // Sofortiges Abspielen (unterbricht aktuelle Wiedergabe)
        public void Play(AudioClip clip)
        {
            if (clip == null) return;
            
            AudioClip optimizedClip = _optimizeVoiceClips ? OptimizeClip(clip) : clip;
            
            _audioSource.Stop();
            _audioSource.clip = optimizedClip;
            _audioSource.Play();
        }
        
        // Hinzufügen zur Abspiel-Queue
        public void EnqueueClip(AudioClip clip)
        {
            if (clip == null) return;
            
            AudioClip optimizedClip = _optimizeVoiceClips ? OptimizeClip(clip) : clip;
            _clipQueue.Enqueue(optimizedClip);
            
            // Wenn nichts abgespielt wird, sofort starten
            if (!_audioSource.isPlaying)
            {
                PlayNextInQueue();
            }
        }
        
        // Wiedergabe stoppen
        public void Stop()
        {
            _audioSource.Stop();
        }
        
        // Queue leeren
        public void ClearQueue()
        {
            _clipQueue.Clear();
        }
        
        private void PlayNextInQueue()
        {
            if (_clipQueue.Count > 0)
            {
                _audioSource.clip = _clipQueue.Dequeue();
                _audioSource.Play();
            }
        }
        
        // Clip-Optimierungen (Sample-Rate reduzieren etc.)
        private AudioClip OptimizeClip(AudioClip originalClip)
        {
            // Wenn bereits optimale Sample-Rate, Original zurückgeben
            if (originalClip.frequency == _targetSampleRate)
                return originalClip;
                
            // Abwärts-Sampling implementieren
            // Hinweis: Vollständiges Resampling würde komplexere DSP-Logik erfordern
            // Für Produktion ein spezialisiertes Audio-Processing-Plugin verwenden
            
            // Im einfachsten Fall: Original zurückgeben mit Warnung
            Debug.LogWarning($"Audio-Optimierung für {originalClip.name} kann nicht durchgeführt werden. " +
                              "Für Produktion ein spezialisiertes Audio-Processing-Plugin implementieren.");
            return originalClip;
        }
    }
}
```

## 4. ProgressiveTTSSynthesizer

Eine erweiterte Version des Text-to-Speech Synthesizers, der Texte in Chunks verarbeitet:

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using VoiceAssistant.Core.Interfaces;

namespace VoiceAssistant.Plugins.OpenAI
{
    public class ProgressiveTTSSynthesizer : ISynthesizer
    {
        private readonly HttpClient _httpClient;
        
        // Konfigurationsparameter
        private readonly int _defaultChunkSize = 100; // Zeichen pro Chunk
        
        public ProgressiveTTSSynthesizer(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }
        
        // Standardimplementierung für IChatService-Kompatibilität
        public async Task<byte[]> SynthesizeAsync(string text, string voice)
        {
            if (string.IsNullOrEmpty(text))
                return new byte[0];
                
            return await SynthesizeTextAsync(text, voice);
        }
        
        // Erweiterte Methode mit progressiven Callback-Updates
        public async Task ChunkedSynthesisAsync(string text, string voice, Action<byte[]> onChunkSynthesized)
        {
            if (string.IsNullOrEmpty(text) || onChunkSynthesized == null)
                return;
                
            // Text in natürliche Chunks aufteilen
            var chunks = SplitTextIntoNaturalChunks(text, _defaultChunkSize);
            
            foreach (var chunk in chunks)
            {
                // Jedes Chunk synthetisieren und Audio sofort zurückgeben
                var audioBytes = await SynthesizeTextAsync(chunk, voice);
                
                if (audioBytes.Length > 0)
                {
                    // Callback mit synthetisiertem Audio-Chunk
                    onChunkSynthesized(audioBytes);
                }
            }
        }
        
        private async Task<byte[]> SynthesizeTextAsync(string text, string voice)
        {
            if (string.IsNullOrEmpty(text))
                return new byte[0];
                
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
            
            var payload = new { model = "tts-1", voice = voice, input = text };
            var body = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                throw new ApplicationException($"TTS-Fehler: {errorText}");
            }
            
            return await response.Content.ReadAsByteArrayAsync();
        }
        
        // Teilt Text an natürlichen Sprechpausen auf (Sätze, Phrasen)
        private string[] SplitTextIntoNaturalChunks(string text, int preferredChunkSize)
        {
            if (string.IsNullOrEmpty(text))
                return new[] { string.Empty };
                
            var result = new List<string>();
            
            // Regex-Muster für verschiedene Satzendungen und Sprechpausen
            var sentenceEndPattern = @"(?<=[.!?])\s+";
            var pausePattern = @"(?<=[:;,])\s+";
            
            // Erst versuchen, bei Satzenden zu trennen
            var sentences = Regex.Split(text, sentenceEndPattern);
            
            var currentChunk = new StringBuilder();
            
            foreach (var sentence in sentences)
            {
                // Wenn Satz in den aktuellen Chunk passt
                if (currentChunk.Length + sentence.Length <= preferredChunkSize)
                {
                    currentChunk.Append(sentence);
                }
                else if (sentence.Length > preferredChunkSize)
                {
                    // Wenn der Satz selbst zu lang ist, versuche ihn an Pausen aufzuteilen
                    if (currentChunk.Length > 0)
                    {
                        result.Add(currentChunk.ToString());
                        currentChunk.Clear();
                    }
                    
                    var phrases = Regex.Split(sentence, pausePattern);
                    var phraseChunk = new StringBuilder();
                    
                    foreach (var phrase in phrases)
                    {
                        if (phraseChunk.Length + phrase.Length <= preferredChunkSize)
                        {
                            phraseChunk.Append(phrase);
                        }
                        else if (phrase.Length > preferredChunkSize)
                        {
                            // Wenn Phrase immer noch zu lang ist, teile am Wortende auf
                            if (phraseChunk.Length > 0)
                            {
                                result.Add(phraseChunk.ToString());
                                phraseChunk.Clear();
                            }
                            
                            // Teile am Wortende auf
                            var words = phrase.Split(' ');
                            var wordChunk = new StringBuilder();
                            
                            foreach (var word in words)
                            {
                                if (wordChunk.Length + word.Length + 1 <= preferredChunkSize)
                                {
                                    if (wordChunk.Length > 0)
                                        wordChunk.Append(' ');
                                    wordChunk.Append(word);
                                }
                                else
                                {
                                    if (wordChunk.Length > 0)
                                    {
                                        result.Add(wordChunk.ToString());
                                        wordChunk.Clear();
                                        wordChunk.Append(word);
                                    }
                                    else
                                    {
                                        // Einzelnes Wort ist länger als Chunk-Größe
                                        result.Add(word);
                                    }
                                }
                            }
                            
                            if (wordChunk.Length > 0)
                                result.Add(wordChunk.ToString());
                        }
                        else
                        {
                            // Phrase passt nicht mehr in den aktuellen Chunk
                            if (phraseChunk.Length > 0)
                            {
                                result.Add(phraseChunk.ToString());
                                phraseChunk.Clear();
                            }
                            phraseChunk.Append(phrase);
                        }
                    }
                    
                    if (phraseChunk.Length > 0)
                        result.Add(phraseChunk.ToString());
                }
                else
                {
                    // Satz passt nicht mehr in den aktuellen Chunk
                    if (currentChunk.Length > 0)
                    {
                        result.Add(currentChunk.ToString());
                        currentChunk.Clear();
                    }
                    currentChunk.Append(sentence);
                }
            }
            
            if (currentChunk.Length > 0)
                result.Add(currentChunk.ToString());
                
            return result.ToArray();
        }
    }
}
```

## 5. VRChatManager 

Eine verbesserte Version des ChatManager speziell für VR-Anwendungen:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;

namespace VoiceAssistant.UnityIntegration
{
    public class VRChatManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ChatUIManager uiManager;
        [SerializeField] private OptimizedVoicePlaybackManager playbackManager;
        
        [Header("Feedback")]
        [SerializeField] private AudioClip processingStartSound;
        [SerializeField] private AudioClip processingEndSound;
        [SerializeField] private GameObject thinkingIndicator;
        
        // Referenzen zu Core-Services (via Dependency Injection)
        private ChatLogManager _chatLogManager;
        private IChatService _chatService;
        private IRecognizer _recognizer;
        private ISynthesizer _synthesizer;
        
        // State Machine für Audio-Verarbeitung
        private enum ProcessingState { Idle, Recognizing, GeneratingResponse, Synthesizing }
        private ProcessingState _currentState = ProcessingState.Idle;
        
        // Für Streaming-Modus
        private bool _isStreamingMode = true;
        private bool _isAutoPlayback = true;
        private StringBuilder _currentResponseBuilder;
        
        // Für abbbrechbare Operationen
        private CancellationTokenSource _cts;
        
        private void Awake()
        {
            _chatLogManager = new ChatLogManager();
            _chatLogManager.MessageAdded += OnMessageAdded;
            
            // Initialisiere _currentResponseBuilder für Streaming
            _currentResponseBuilder = new StringBuilder();
            
            // Prüfe, ob MainThreadDispatcher vorhanden ist
            if (FindObjectOfType<MainThreadDispatcher>() == null)
            {
                Debug.LogWarning("MainThreadDispatcher nicht gefunden. Erstelle einen in der Szene.");
                var go = new GameObject("MainThreadDispatcher");
                go.AddComponent<MainThreadDispatcher>();
            }
        }
        
        private void OnDestroy()
        {
            _chatLogManager.MessageAdded -= OnMessageAdded;
            CancelCurrentOperation();
        }
        
        // Services setzen (via Dependency Injection)
        public void SetServices(
            IChatService chatService, 
            IRecognizer recognizer, 
            ISynthesizer synthesizer)
        {
            _chatService = chatService;
            _recognizer = recognizer;
            _synthesizer = synthesizer;
        }
        
        // Audio vom Mikrofon verarbeiten
        public async void ProcessUserAudio(AudioClip recordedAudio)
        {
            if (_currentState != ProcessingState.Idle)
            {
                Debug.LogWarning("Bereits eine Verarbeitung aktiv. Breche ab.");
                return;
            }
            
            CancelCurrentOperation();
            _cts = new CancellationTokenSource();
            
            try
            {
                // UI-Feedback starten
                ShowProcessingFeedback(true);
                
                // 1. Audio zu Text (Recognizer)
                _currentState = ProcessingState.Recognizing;
                string userText = await RecognizeAudioAsync(recordedAudio, _cts.Token);
                
                if (string.IsNullOrEmpty(userText))
                {
                    Debug.LogWarning("Keine Sprache erkannt.");
                    return;
                }
                
                // Dem Chatlog hinzufügen und UI aktualisieren
                _chatLogManager.AddMessage(ChatRole.User, userText);
                
                // 2. Chat-Antwort generieren
                _currentState = ProcessingState.GeneratingResponse;
                
                string botResponse;
                
                if (_isStreamingMode && _chatService is StreamingOpenAIChatService streamingService)
                {
                    // 2a. Streaming-Modus
                    _currentResponseBuilder.Clear();
                    uiManager.StartStreamingResponse();
                    
                    botResponse = await streamingService.GenerateStreamingResponseAsync(
                        _chatLogManager.GetMessages(),
                        token => {
                            _currentResponseBuilder.Append(token);
                            // Auf dem Main Thread aktualisieren
                            MainThreadDispatcher.Enqueue(() => {
                                uiManager.AppendResponseChunk(token);
                            });
                        });
                }
                else
                {
                    // 2b. Nicht-Streaming-Modus
                    botResponse = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages());
                }
                
                // Antwort dem Chatlog hinzufügen
                _chatLogManager.AddMessage(ChatRole.Bot, botResponse);
                
                // 3. Text zu Sprache synthetisieren
                _currentState = ProcessingState.Synthesizing;
                
                if (_synthesizer is ProgressiveTTSSynthesizer progressiveTTS && _isAutoPlayback)
                {
                    // 3a. Progressive Synthese mit sofortiger Wiedergabe
                    await progressiveTTS.ChunkedSynthesisAsync(
                        botResponse, 
                        "alloy", // Default-Voice
                        audioBytes => {
                            // Audio-Bytes in AudioClip konvertieren und abspielen
                            ConvertAndPlayAudioBytes(audioBytes);
                        });
                }
                else
                {
                    // 3b. Vollständiges Audio synthetisieren
                    byte[] audioBytes = await _synthesizer.SynthesizeAsync(botResponse, "alloy");
                    
                    if (audioBytes != null && audioBytes.Length > 0 && _isAutoPlayback)
                    {
                        // Audio-Bytes in AudioClip konvertieren und abspielen
                        ConvertAndPlayAudioBytes(audioBytes);
                    }
                }
                
                // UI aktualisieren
                uiManager.FinalizeResponse(botResponse);
            }
            catch (OperationCanceledException)
            {
                Debug.Log("Verarbeitung wurde abgebrochen.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Fehler bei der Sprachverarbeitung: {ex.Message}");
                uiManager.ShowError("Fehler bei der Sprachverarbeitung");
            }
            finally
            {
                _currentState = ProcessingState.Idle;
                ShowProcessingFeedback(false);
                
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
        }
        
        // Aktuelle Operation abbrechen
        public void CancelCurrentOperation()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
            
            _currentState = ProcessingState.Idle;
            ShowProcessingFeedback(false);
        }
        
        // Text-Nachricht vom User senden
        public void SendUserMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            _chatLogManager.AddMessage(ChatRole.User, text);
            
            // Chat-Antwort asynchron generieren
            ProcessUserText(text);
        }
        
        // Textbasierte Verarbeitung 
        private async void ProcessUserText(string userText)
        {
            if (_currentState != ProcessingState.Idle)
                return;
                
            CancelCurrentOperation();
            _cts = new CancellationTokenSource();
            
            try
            {
                ShowProcessingFeedback(true);
                _currentState = ProcessingState.GeneratingResponse;
                
                string botResponse;
                
                if (_isStreamingMode && _chatService is StreamingOpenAIChatService streamingService)
                {
                    // Streaming-Modus
                    _currentResponseBuilder.Clear();
                    uiManager.StartStreamingResponse();
                    
                    botResponse = await streamingService.GenerateStreamingResponseAsync(
                        _chatLogManager.GetMessages(),
                        token => {
                            _currentResponseBuilder.Append(token);
                            MainThreadDispatcher.Enqueue(() => {
                                uiManager.AppendResponseChunk(token);
                            });
                        });
                }
                else
                {
                    // Nicht-Streaming-Modus
                    botResponse = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages());
                }
                
                _chatLogManager.AddMessage(ChatRole.Bot, botResponse);
                
                // Audio synthetisieren
                _currentState = ProcessingState.Synthesizing;
                
                if (_synthesizer is ProgressiveTTSSynthesizer progressiveTTS && _isAutoPlayback)
                {
                    await progressiveTTS.ChunkedSynthesisAsync(
                        botResponse, 
                        "alloy",
                        audioBytes => {
                            ConvertAndPlayAudioBytes(audioBytes);
                        });
                }
                else
                {
                    byte[] audioBytes = await _synthesizer.SynthesizeAsync(botResponse, "alloy");
                    
                    if (audioBytes != null && audioBytes.Length > 0 && _isAutoPlayback)
                    {
                        ConvertAndPlayAudioBytes(audioBytes);
                    }
                }
                
                uiManager.FinalizeResponse(botResponse);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Fehler bei der Textverarbeitung: {ex.Message}");
                uiManager.ShowError("Fehler bei der Textverarbeitung");
            }
            finally
            {
                _currentState = ProcessingState.Idle;
                ShowProcessingFeedback(false);
                
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
        }
        
        private async Task<string> RecognizeAudioAsync(AudioClip clip, CancellationToken token)
        {
            if (clip == null || _recognizer == null)
                return string.Empty;
                
            try
            {
                // AudioClip in Stream konvertieren
                using (var stream = new MemoryStream())
                {
                    AudioClipToWavStream(clip, stream);
                    stream.Position = 0;
                    
                    // ASR API aufrufen
                    return await _recognizer.RecognizeAsync(stream, "audio/wav", "recording.wav");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Fehler bei der Spracherkennung: {ex.Message}");
                throw;
            }
        }
        
        private void AudioClipToWavStream(AudioClip clip, MemoryStream stream)
        {
            // Konvertiere AudioClip zu WAV
            // Implementierung hängt von Unity-Version und Plattform ab
            // Für vollständige Implementierung auf Unity-Dokumentation verweisen
            
            // Vereinfachte Beispielimplementierung:
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);
            
            // WAV-Header schreiben
            byte[] header = CreateWavHeader(clip.samples, clip.channels, clip.frequency);
            stream.Write(header, 0, header.Length);
            
            // Samples schreiben
            for (int i = 0; i < samples.Length; i++)
            {
                // Konvertiere float-Samples [-1.0, 1.0] zu Int16 [-32768, 32767]
                short sample = (short)(samples[i] * 32767);
                byte[] bytes = BitConverter.GetBytes(sample);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        
        private byte[] CreateWavHeader(int samples, int channels, int frequency)
        {
            // Vereinfachter WAV-Header
            int dataSize = samples * channels * 2; // 16-bit = 2 bytes pro Sample
            int fileSize = 36 + dataSize;
            
            var header = new byte[44];
            
            // "RIFF" Chunk
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
            BitConverter.GetBytes(fileSize).CopyTo(header, 4);
            System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
            
            // "fmt " Chunk
            System.Text.Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
            BitConverter.GetBytes(16).CopyTo(header, 16); // Chunk-Size
            BitConverter.GetBytes((short)1).CopyTo(header, 20); // PCM-Format
            BitConverter.GetBytes((short)channels).CopyTo(header, 22);
            BitConverter.GetBytes(frequency).CopyTo(header, 24);
            BitConverter.GetBytes(frequency * channels * 2).CopyTo(header, 28); // Bytes pro Sekunde
            BitConverter.GetBytes((short)(channels * 2)).CopyTo(header, 32); // Block-Alignment
            BitConverter.GetBytes((short)16).CopyTo(header, 34); // Bits pro Sample
            
            // "data" Chunk
            System.Text.Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
            BitConverter.GetBytes(dataSize).CopyTo(header, 40);
            
            return header;
        }
        
        private void ConvertAndPlayAudioBytes(byte[] audioBytes)
        {
            if (audioBytes == null || audioBytes.Length == 0)
                return;
                
            // MP3/Ogg-Bytes in Unity AudioClip konvertieren
            // Hier ist eine spezialisierte Audio-Library empfehlenswert
            
            // Beispiel für Unity API zur Audio-Dekodierung (pseudo-code)
            // In der Produktionsversion eine spezielle Library einsetzen
            MainThreadDispatcher.Enqueue(async () => {
                try
                {
                    // Pseudo-Code für Audio-Dekodierung
                    var www = new UnityEngine.Networking.UnityWebRequest();
                    www.downloadHandler = new UnityEngine.Networking.DownloadHandlerAudioClip(null, AudioType.MPEG);
                    await www.SendWebRequest();
                    
                    if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        var audioClip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(www);
                        playbackManager.EnqueueClip(audioClip);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Fehler bei der Audio-Konvertierung: {ex.Message}");
                }
            });
        }
        
        private void ShowProcessingFeedback(bool isProcessing)
        {
            // Visuelles Feedback
            if (thinkingIndicator != null)
            {
                thinkingIndicator.SetActive(isProcessing);
            }
            
            // Audio-Feedback
            if (isProcessing && processingStartSound != null)
            {
                playbackManager.Play(processingStartSound);
            }
            else if (!isProcessing && processingEndSound != null)
            {
                playbackManager.Play(processingEndSound);
            }
        }
        
        private void OnMessageAdded(ChatMessage message)
        {
            // Wird automatisch im Main-Thread aufgerufen (Unity Event System)
            if (message.Role == ChatRole.User)
            {
                uiManager.AddUserMessage(message.Content);
            }
        }
    }
}
```

## 6. FeedbackEnhancedChatUIManager

Eine verbesserte UI-Manager-Klasse mit Feedback-Mechanismen:

```csharp
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace VoiceAssistant.UnityIntegration
{
    public class FeedbackEnhancedChatUIManager : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private Transform chatContainer;
        [SerializeField] private GameObject userBubblePrefab;
        [SerializeField] private GameObject botBubblePrefab;
        [SerializeField] private ScrollRect scrollRect;
        
        [Header("Feedback Elements")]
        [SerializeField] private GameObject thinkingIndicator;
        [SerializeField] private GameObject errorPanel;
        [SerializeField] private TextMeshProUGUI errorText;
        [SerializeField] private ParticleSystem bubbleEmitter; // Optionales Partikelsystem
        
        [Header("Animation Settings")]
        [SerializeField] private float typewriterSpeed = 0.03f; // Sekunden pro Zeichen
        [SerializeField] private bool useTypewriterEffect = true;
        
        // Referenzen
        [SerializeField] private OptimizedVoicePlaybackManager playbackManager;
        
        // Aktuelle Bot-Antwort für Streaming-Updates
        private GameObject _currentBotBubble;
        private TextMeshProUGUI _currentBotText;
        private Button _currentStopButton;
        
        private void Start()
        {
            // UI initialisieren
            if (errorPanel != null)
                errorPanel.SetActive(false);
                
            if (thinkingIndicator != null)
                thinkingIndicator.SetActive(false);
        }
        
        // Nutzernachricht hinzufügen
        public void AddUserMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            var bubble = Instantiate(userBubblePrefab, chatContainer);
            var textComponent = bubble.GetComponentInChildren<TextMeshProUGUI>();
            
            if (textComponent != null)
            {
                textComponent.text = text;
            }
            
            // Zum Ende des Chat-Containers scrollen
            ScrollToBottom();
        }
        
        // Eine vollständige Bot-Nachricht hinzufügen (non-streaming)
        public void AddBotMessage(string text, AudioClip voiceClip = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            
            var bubble = Instantiate(botBubblePrefab, chatContainer);
            var textComponent = bubble.GetComponentInChildren<TextMeshProUGUI>();
            
            if (textComponent != null)
            {
                if (useTypewriterEffect)
                {
                    // Mit Typewriter-Effekt anzeigen
                    StartCoroutine(TypewriterEffect(textComponent, text));
                }
                else
                {
                    textComponent.text = text;
                }
            }
            
            // Stop-Button für Audiowiedergabe
            var stopButton = bubble.GetComponentInChildren<Button>();
            if (stopButton != null && playbackManager != null)
            {
                stopButton.onClick.AddListener(() => {
                    playbackManager.Stop();
                });
                
                // Button deaktivieren, wenn kein Audio vorhanden
                stopButton.gameObject.SetActive(voiceClip != null);
            }
            
            // Audio abspielen, wenn vorhanden
            if (voiceClip != null && playbackManager != null)
            {
                playbackManager.Play(voiceClip);
            }
            
            // Zum Ende des Chat-Containers scrollen
            ScrollToBottom();
        }
        
        // Beginn einer Streaming-Antwort
        public void StartStreamingResponse()
        {
            // Aktuelle Streaming-Antwort löschen, falls vorhanden
            if (_currentBotBubble != null)
            {
                _currentBotBubble = null;
                _currentBotText = null;
                _currentStopButton = null;
            }
            
            // Neue Chat-Bubble erstellen
            _currentBotBubble = Instantiate(botBubblePrefab, chatContainer);
            _currentBotText = _currentBotBubble.GetComponentInChildren<TextMeshProUGUI>();
            _currentStopButton = _currentBotBubble.GetComponentInChildren<Button>();
            
            if (_currentBotText != null)
            {
                _currentBotText.text = "..."; // Platzhalter
            }
            
            if (_currentStopButton != null)
            {
                _currentStopButton.gameObject.SetActive(false); // Erst aktivieren, wenn Audio abgespielt wird
                _currentStopButton.onClick.AddListener(() => {
                    if (playbackManager != null)
                        playbackManager.Stop();
                });
            }
            
            // Feedback-Effekte
            if (bubbleEmitter != null)
                bubbleEmitter.Play();
                
            // Zum Ende des Chat-Containers scrollen
            ScrollToBottom();
        }
        
        // Token-weises Update während des Streamings
        public void AppendResponseChunk(string token)
        {
            if (_currentBotText == null || string.IsNullOrEmpty(token))
                return;
                
            if (_currentBotText.text == "...")
                _currentBotText.text = token; // Platzhalter ersetzen
            else
                _currentBotText.text += token; // Token anhängen
                
            // Zum Ende des Chat-Containers scrollen
            ScrollToBottom();
        }
        
        // Abschluss einer Streaming-Antwort
        public void FinalizeResponse(string fullText, AudioClip voiceClip = null)
        {
            if (_currentBotText == null)
                return;
                
            // Sicherstellen, dass der vollständige Text angezeigt wird
            _currentBotText.text = fullText;
            
            // Stop-Button aktivieren, wenn Audio vorhanden
            if (_currentStopButton != null)
            {
                _currentStopButton.gameObject.SetActive(voiceClip != null);
            }
            
            // Audio abspielen, wenn vorhanden
            if (voiceClip != null && playbackManager != null)
            {
                playbackManager.Play(voiceClip);
            }
            
            // Feedback-Effekte stoppen
            if (bubbleEmitter != null)
                bubbleEmitter.Stop();
                
            // Zurücksetzen
            _currentBotBubble = null;
            _currentBotText = null;
            _currentStopButton = null;
            
            // Zum Ende des Chat-Containers scrollen
            ScrollToBottom();
        }
        
        // Zeige Verarbeitungsstatus an
        public void ShowProcessingIndicator(bool isProcessing)
        {
            if (thinkingIndicator != null)
                thinkingIndicator.SetActive(isProcessing);
        }
        
        // Zeige Fehlermeldung
        public void ShowError(string message)
        {
            if (errorPanel != null && errorText != null)
            {
                errorText.text = message;
                errorPanel.SetActive(true);
                
                // Automatisch ausblenden nach 3 Sekunden
                StartCoroutine(AutoHideError(3.0f));
            }
        }
        
        // Zum Ende des Chat-Bereichs scrollen
        private void ScrollToBottom()
        {
            if (scrollRect != null)
            {
                // In der nächsten Frame-Update warten, bis Layout aktualisiert ist
                Canvas.ForceUpdateCanvases();
                scrollRect.normalizedPosition = new Vector2(0, 0);
            }
        }
        
        // Typewriter-Text-Effekt
        private IEnumerator TypewriterEffect(TextMeshProUGUI textComponent, string fullText)
        {
            textComponent.text = "";
            
            foreach (char c in fullText)
            {
                textComponent.text += c;
                yield return new WaitForSeconds(typewriterSpeed);
            }
        }
        
        // Automatisches Ausblenden für Fehlermeldungen
        private IEnumerator AutoHideError(float delay)
        {
            yield return new WaitForSeconds(delay);
            
            if (errorPanel != null)
                errorPanel.SetActive(false);
        }
    }
}
```

Diese Implementierungen decken die im erweiterten Refactoring-Plan genannten Optimierungen ab. Um sie im Projekt einzusetzen:

1. Fügen Sie die neuen Dateien zum Projekt hinzu
2. Passen Sie die API-Endpunkte an, um die Streaming-Funktionalität zu verwenden
3. Ersetzen Sie bestehende Komponenten in Unity oder erweitern Sie sie mit den neuen Funktionen
4. Stellen Sie sicher, dass die entsprechenden Unity-Prefabs und UI-Elemente eingerichtet sind
5. Testen Sie die Implementierung schrittweise, beginnend mit dem StreamingOpenAIChatService

Diese Implementierungen sollten die Latenz spürbar reduzieren und eine deutlich reaktionsschnellere Benutzererfahrung auf der Meta Quest 3 ermöglichen.