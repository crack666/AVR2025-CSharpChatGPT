# OpenAI Realtime API Integration

## Übersicht

Das Voice Assistant System unterstützt jetzt drei verschiedene Modi für Speech-to-Text (STT) Verarbeitung:

1. **Legacy HTTP Mode** - Traditionelle Whisper API mit HTTP-Requests
2. **Streaming Mode** - Chunked HTTP-Verarbeitung für schnellere Antworten
3. **OpenAI Realtime API Mode** - Echtes Echtzeit-Streaming mit eingebautem VAD

## OpenAI Realtime API

Die neue OpenAI Realtime API bietet folgende Vorteile:

### Eingebaute VAD (Voice Activity Detection)
- Eliminiert die Notwendigkeit für lokales WebRTC VAD
- Bessere Genauigkeit bei der Spracherkennung
- Automatische Segmentierung basierend auf natürlichen Sprachpausen

### Echtes Streaming
- WebSocket-basierte Verbindung zu `wss://api.openai.com/v1/realtime`
- Kontinuierliche Audio-Übertragung ohne Zwischenpufferung
- Sofortige Teilergebnisse während des Sprechens

### Niedrige Latenz
- Keine Wartezeit bis zum Ende der Spracheingabe
- Streaming-Ergebnisse beginnen sofort nach Sprachbeginn
- Optimiert für Echtzeit-Konversationen

## Konfiguration

### Realtime API aktivieren

In `appsettings.json`:
```json
{
  "PipelineOptions": {
    "Language": "de",
    "UseOpenAIRealtimeVad": true,
    "DisableVad": false,
    "UseLegacyHttp": false
  }
}
```

### Wichtige Einstellungen

- `UseOpenAIRealtimeVad: true` - Aktiviert die OpenAI Realtime API
- `DisableVad: false` - Lokales VAD wird automatisch bypassed wenn Realtime API aktiv ist
- `UseLegacyHttp: false` - Verwendet WebSocket-Streaming statt HTTP

## Implementierung

### Neue Klassen

#### `OpenAIRealtimeRecognizer`
```csharp
public class OpenAIRealtimeRecognizer : IRecognizer, IDisposable
{
    // WebSocket-basierte Realtime API Implementierung
    public async Task<string> RecognizeRealtimeAsync(byte[] audioChunk, string language, string sessionId)
    public async Task ConnectAsync(string sessionId, string language = "en")
    public async Task DisconnectAsync()
}
```

### Events

Das System verwendet Events für asynchrone Kommunikation:

#### Von AudioFrameProcessor
- `SpeechStreamStarted` - Wenn OpenAI VAD Sprache erkennt
- `SpeechFrameReady` - Kontinuierliche Audio-Frames während Sprache
- `SpeechStreamEnded` - Wenn OpenAI VAD Sprachende erkennt

#### Von OpenAIRealtimeRecognizer
- `OnTranscriptionReady` - Erkannte Textfragmente
- `OnSpeechStarted` - Sprachbeginn von OpenAI VAD
- `OnSpeechEnded` - Sprachende von OpenAI VAD
- `OnError` - Fehler in der Realtime API

## Flow-Diagramm

### Traditioneller Flow (mit lokalem VAD)
```
Audio → AudioFrameProcessor → WebRTC VAD → Segment Buffer → HTTP Whisper API → Text
```

### Realtime API Flow
```
Audio → AudioFrameProcessor → OpenAI Realtime API → Streaming Text
                              ↓
                         Eingebautes VAD
```

## Migration

### Von Legacy zu Realtime

1. **Konfiguration anpassen**:
   ```json
   "UseOpenAIRealtimeVad": true
   ```

2. **Dependency Injection erweitern**:
   ```csharp
   services.AddSingleton<IRecognizer, OpenAIRealtimeRecognizer>();
   ```

3. **API Key bereitstellen**:
   - Umgebungsvariable: `OPENAI_API_KEY`
   - Oder in appsettings.json

### Kompatibilität

Das System ist rückwärtskompatibel:
- Legacy HTTP Mode bleibt funktionsfähig
- Bestehende Konfigurationen funktionieren weiterhin
- Schrittweise Migration möglich

## Performance-Verbesserungen

### Latenz-Reduktion
- **Legacy**: 2-4 Sekunden (VAD + HTTP + Verarbeitung)
- **Streaming**: 1-2 Sekunden (Chunked HTTP)
- **Realtime**: 200-500ms (WebSocket + eingebautes VAD)

### Speicherverbrauch
- Reduzierte Pufferung durch Streaming
- Eliminierung von großen Audio-Segmenten
- Kontinuierliche Verarbeitung statt Batch-Processing

## Debugging

### Logs

```csharp
_logger.LogInformation("Session {SessionId}: Connected to OpenAI Realtime API for transcription", sessionId);
_logger.LogInformation("Session {SessionId}: Speech started (detected by OpenAI VAD)", sessionId);
_logger.LogInformation("Session {SessionId}: Transcription completed: '{Transcript}'", sessionId, transcript);
```

### WebSocket-Status überwachen

Die Realtime API verwendet WebSocket-Verbindungen:
- Verbindungsstatus in Logs verfolgen
- Reconnection-Logic implementiert
- Graceful degradation zu HTTP-Fallback

## Bekannte Limitierungen

1. **Beta-Status**: OpenAI Realtime API ist noch in Beta
2. **Kosten**: Möglicherweise höhere Kosten als HTTP API
3. **Rate Limits**: Andere Limits als HTTP API
4. **Netzwerk**: Benötigt stabile WebSocket-Verbindung

## Zukunftsausblick

### Geplante Features
- Vollständige Speech-to-Speech Pipeline über Realtime API
- Integration von TTS über Realtime API
- Multi-Language Support in einer Session
- Conversation Context Management

### Roadmap
1. **Phase 1**: STT-Integration (✅ Abgeschlossen)
2. **Phase 2**: TTS-Integration
3. **Phase 3**: Vollständige Realtime-Pipeline
4. **Phase 4**: Advanced Features (Function Calling, etc.)
