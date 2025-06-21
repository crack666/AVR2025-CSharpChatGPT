# Voice Assistant mit OpenAI Realtime API Integration

Modernisierter, latenzoptimierter Voice Assistant mit Unterstützung für:
- **OpenAI Realtime API** für echtes Echtzeit-Streaming (WebSocket-basiert)
- **Traditionelle HTTP-API** für Kompatibilität  
- **Hybride Streaming-Pipeline** mit optimierter Satzgrenze-Erkennung
- **Flexible VAD-Modi**: Lokales WebRTC VAD oder eingebautes OpenAI VAD

## 🚀 Neue Features (2025)

### OpenAI Realtime API Integration
- **Echtes Streaming**: WebSocket-Verbindung zu `wss://api.openai.com/v1/realtime`
- **Eingebaute VAD**: Eliminiert lokales VAD, bessere Spracherkennung
- **Ultra-niedrige Latenz**: 200-500ms statt 2-4 Sekunden
- **Sofortige Teilergebnisse**: Beginnt während des Sprechens

### Verbesserte Pipeline
- **Robuste Satzgrenze-Erkennung**: Verhindert Aufspaltung bei Abkürzungen und Zahlen
- **Streaming Session Management**: Kontinuierliche Verarbeitung mit Zustandsverfolgung  
- **Ereignis-basierte Architektur**: Asynchrone Kommunikation zwischen Komponenten
- **Rückwärtskompatibilität**: Alle bestehenden Modi funktionieren weiterhin

## 📊 Performance-Verbesserungen

| Modus | Latenz | VAD | API-Typ |
|-------|--------|-----|---------|
| Legacy | 2-4s | WebRTC | HTTP |
| Streaming | 1-2s | WebRTC | HTTP (Chunked) |
| **Realtime** | **200-500ms** | **OpenAI** | **WebSocket** |

## 🔧 Schnellstart

### Realtime API aktivieren
```json
{
  "PipelineOptions": {
    "UseOpenAIRealtimeVad": true,
    "DisableVad": false,
    "UseLegacyHttp": false
  }
}
```

### Umgebungsvariable setzen
```bash
OPENAI_API_KEY=your_api_key_here
```

### Starten
```bash
dotnet run
```

Siehe [OPENAI_REALTIME_INTEGRATION.md](OPENAI_REALTIME_INTEGRATION.md) für detaillierte Informationen.

---

# Detaillierte Analyse der Voice Assistant Anwendung

Dieses Dokument beschreibt detailliert den Aufbau und den Funktionsablauf der Voice Assistant Anwendung. Es dient als umfassende Anleitung für Entwickler und als Wissensbasis für LLMs, um die Codebasis schnell zu verstehen und spezifische Komponenten zu lokalisieren.

## 1. Übersicht

Die Anwendung ist ein sprachgesteuerter Assistent, der Audioeingaben in Echtzeit verarbeitet, diese mittels Voice Activity Detection (VAD) in Sprachsegmente unterteilt, transkribiert, an einen Chat-Service sendet und die Antwort als synthetisierte Sprache zurückgibt. Die Hauptinteraktion erfolgt über eine WebSocket-Verbindung, die bidirektionales Streaming von Audio- und Steuerungsdaten ermöglicht.

## 2. Kernfunktionalität und Code-Pfad

Der Kern der Anwendung ist in C# implementiert und nutzt ASP.NET Core für die Web-Funktionalitäten.

### 2.1. Anwendungseinstieg und Service-Konfiguration (`Program.cs`)

Der Einstiegspunkt der Anwendung ist die `Program.cs`-Datei. Hier werden wesentliche Dienste konfiguriert und die Web-Anwendung initialisiert:

*   **API-Schlüssel:** Der `OPENAI_API_KEY` wird aus den Umgebungsvariablen geladen. Ohne diesen Schlüssel startet die Anwendung nicht.
*   **Logging:** Serilog wird als Logging-Framework verwendet und über `appsettings.json` konfiguriert. Standard-Logging-Provider werden entfernt.
*   **PipelineOptions:** Die `PipelineOptions` (aus `VoiceAssistant.Core/Models/PipelineOptions.cs`) werden aus der `appsettings.json` geladen und als Singleton-Dienst registriert. Diese Klasse enthält Flags zur Steuerung verschiedener Pipeline-Funktionen (z.B. `UseLegacyHttp`, `DisableVad`, `DisableTts`).
*   **HttpClient:** Ein globaler `HttpClient` wird als Singleton konfiguriert. Dieser ist für HTTP/2 optimiert, verwendet persistente Verbindungen und inkludiert den OpenAI API-Schlüssel im Authorization-Header. Die Timeout-Dauer wurde auf 3 Minuten erhöht.
*   **Kerndienste und Plugins:**
    *   `ChatLogManager` (`VoiceAssistant.Core/Services/ChatLogManager.cs`): Zuständig für die Verwaltung von Chat-Protokollen.
    *   `IChatService` (`VoiceAssistant.Core/Interfaces/IChatService.cs`): Implementiert durch `StreamingOpenAIChatService` (`VoiceAssistant.Plugins.OpenAI/StreamingOpenAIChatService.cs`). Diese Implementierung unterstützt Streaming-Antworten vom Chat-Modell.
    *   `IRecognizer` (`VoiceAssistant.Core/Interfaces/IRecognizer.cs`): Implementiert durch `OpenAIApiRecognizer` (`VoiceAssistant.Plugins.OpenAI/OpenAIApiRecognizer.cs`) für die Spracherkennung.
    *   `ISynthesizer` (`VoiceAssistant.Core/Interfaces/ISynthesizer.cs`): Implementiert durch `ProgressiveTTSSynthesizer` (`VoiceAssistant.Plugins.OpenAI/ProgressiveTTSSynthesizer.cs`) für eine latenzoptimierte Text-zu-Sprache-Synthese.
    *   `VadSettings` (`VoiceAssistant.Core/Models/VadSettings.cs`): Enthält Einstellungen für die Voice Activity Detection und wird als Singleton registriert.
    *   `WebSocketAudioService` (`WebSocketAudioService.cs`): Der zentrale Dienst für die Handhabung der WebSocket-Kommunikation und Audioverarbeitung.
*   **Controller:** Standard ASP.NET Core MVC Controller werden registriert.
*   **Middleware:**
    *   `UseDeveloperExceptionPage()`: Aktiviert eine detaillierte Fehlerseite im Entwicklungsmodus.
    *   `UseWebSockets()`: Aktiviert die Unterstützung für WebSockets.
    *   `Map("/ws/audio", ...)`: Definiert den Endpunkt für WebSocket-Verbindungen. Hier wird geprüft, ob `PipelineOptions.UseLegacyHttp` aktiv ist. Wenn nicht, wird die WebSocket-Verbindung akzeptiert und an den `WebSocketAudioService` übergeben. Die (aktuell auskommentierte) Logik zur Übernahme von `model` und `voice` Query-Parametern deutet auf eine frühere oder geplante Funktionalität zur dynamischen Modellauswahl hin.
    *   `UseDefaultFiles()` und `UseStaticFiles()`: Ermöglichen das Ausliefern von statischen Dateien aus dem `wwwroot`-Verzeichnis (z.B. die Frontend-Anwendung).
    *   `MapControllers()`: Mappt die Routen für die API-Controller.

### 2.2. WebSocket-Audioverarbeitung (moderne Architektur ab 2025)

Die WebSocket-basierte Audioverarbeitung wurde im Zuge des Refactorings von einer monolithischen Klasse (`WebSocketAudioService`) in mehrere spezialisierte Services und Komponenten aufgeteilt. Dies erhöht die Wartbarkeit, Testbarkeit und Erweiterbarkeit.

**Wichtige Komponenten:**

- **WebSocketHandler** (`VoiceAssistant.Core/Services/WebSocketHandler.cs`):
  - Orchestriert die gesamte WebSocket-Kommunikation pro Session.
  - Verwaltet den Lebenszyklus der Verbindung, nimmt Audio-Frames entgegen und steuert die Verarbeitungskette.
  - Reagiert auf Steuerbefehle (z.B. VAD/Pipeline-Updates) und verteilt sie an die zuständigen Services.
  - Sorgt für Logging und Fehlerbehandlung auf Session-Ebene.

- **AudioFrameProcessor** (`VoiceAssistant.Core/Services/AudioFrameProcessor.cs`):
  - Übernimmt die Vorverarbeitung und Segmentierung der eingehenden Audio-Frames (VAD, RMS, Spike Detection etc.).
  - Erkennt Sprachsegmente und triggert Events für die weitere Verarbeitung.
  - Kann zur Laufzeit mit neuen VAD- und Pipeline-Settings aktualisiert werden.

- **AudioSegmentProcessor** (`VoiceAssistant.Core/Services/AudioSegmentProcessor.cs`):
  - Übernimmt die Verarbeitung kompletter Sprachsegmente (Transkription, Chat, TTS, Event-Senden).
  - Kapselt die gesamte Logik für STT, LLM-Interaktion und TTS-Streaming.
  - Sendet alle relevanten Events/Audioblocks über den WebSocketHandler an den Client.

- **WebSocketSettingsManager** (`VoiceAssistant.Core/Services/WebSocketSettingsManager.cs`):
  - Kapselt die Logik zur Laufzeit-Aktualisierung von VAD- und Pipeline-Settings via WebSocket.
  - Validiert und übernimmt neue Einstellungen, sendet Bestätigungen/Fehler an den Client.

- **Weitere Services:**
  - `ChatLogManager`, `IChatService`, `ISynthesizer`, `IRecognizer` etc. werden als Abhängigkeiten injiziert und von den Prozessoren genutzt.

### 2.2.1. Architektonische Schlüsselerkenntnis: Entkopplung der WebSocket-Kommunikation

Während des Refactorings im Juni 2025 wurde ein kritisches Problem identifiziert, das die Anwendung blockierte: **zirkuläre Abhängigkeiten** im Dependency-Injection-Container.

**Problem:** Ursprünglich hatten Dienste wie `AudioSegmentProcessor` eine direkte Abhängigkeit vom `IWebSocketHandler`, um Nachrichten (z.B. Transkriptionen, TTS-Audio) an den Client zurückzusenden. Gleichzeitig benötigte der `IWebSocketHandler` diese Dienste, um die Audioverarbeitung zu steuern. Bei der Verwendung von Scoped Services führte dies zu einer Blockade, da jeder Dienst auf die Instanziierung des anderen wartete.

**Lösung und goldene Regel:** Die Lösung ist eine strikt **ereignisgesteuerte Architektur**.

1.  **Keine WebSocket-Abhängigkeit:** Dienste innerhalb der Verarbeitungskette (`AudioFrameProcessor`, `AudioSegmentProcessor`, `WebSocketSettingsManager` etc.) dürfen **niemals** eine direkte Abhängigkeit vom `WebSocket`-Objekt oder dem `IWebSocketHandler` haben. Sie wissen nichts über die Kommunikationsschicht.
2.  **Kommunikation über Events:** Wenn ein Dienst ein Ergebnis hat (z.B. eine fertige Transkription), löst er ein Event aus (z.B. `OnTranscriptionReady`).
3.  **Der Orchestrator:** Der `WebSocketHandler` ist die **einzige** Komponente, die die `WebSocket`-Instanz verwaltet. Er abonniert die Events der Verarbeitungsdienste. Wenn ein Event ausgelöst wird, formatiert der `WebSocketHandler` die Nachricht und sendet sie über das WebSocket an den Client.

Diese Entkopplung ist entscheidend für die Stabilität und Wartbarkeit der Anwendung. Zukünftige Entwicklungen müssen dieses Prinzip strikt befolgen, um zu verhindern, dass die Anwendung erneut durch Abhängigkeitsprobleme blockiert wird.

**Ablauf:**
- Jede WebSocket-Session erhält eigene Instanzen der Handler/Prozessoren (Dependency Injection, Scoped).
- Audio-Frames werden im Handler empfangen, an den FrameProcessor weitergereicht und bei Segment-Events an den SegmentProcessor übergeben.
- Steuerbefehle (Settings-Updates) werden über den SettingsManager verarbeitet und propagiert.
- Logging ist auf Session- und Event-Ebene sehr granular, inkl. CloseStatus, Fehlern und Performance.

**Vorteile der neuen Architektur:**
- Klare Trennung von Zuständigkeiten (Single Responsibility Principle).
- Bessere Testbarkeit und Erweiterbarkeit (z.B. für alternative VAD- oder TTS-Engines).
- Verbesserte Fehlerdiagnose durch detailliertes Logging pro Session und Event.
- Settings-Änderungen wirken sofort und thread-sicher auf die jeweilige Session.

### 2.3. Controller (`Controllers/`)

Die Anwendung enthält auch Standard-API-Controller:

*   `AudioController.cs`: Könnte Endpunkte für Audioverarbeitung außerhalb von WebSockets bereitstellen (z.B. `/api/processAudio` aus dem alten README).
*   `ChatController.cs`: Könnte HTTP-basierte Chat-Endpunkte anbieten.
*   `ModelsController.cs`: Dient vermutlich dazu, verfügbare Modelle (z.B. für STT oder Chat) aufzulisten.
*   `SettingsController.cs`: Ermöglicht das Abrufen und ggf. Aktualisieren von `VadSettings` und `PipelineOptions` über HTTP-Endpunkte. Dies bietet eine Alternative zur WebSocket-basierten Konfiguration.
*   `SpeechController.cs`: Könnte Endpunkte für direkte STT- oder TTS-Anfragen via HTTP bereitstellen.

### 2.4. Kernkomponenten (`VoiceAssistant.Core/`)

Dieser Ordner enthält die grundlegenden Abstraktionen und Datenmodelle:

*   **Interfaces:** `IChatService.cs`, `IRecognizer.cs`, `ISynthesizer.cs` definieren die Verträge für die jeweiligen Funktionalitäten und ermöglichen austauschbare Implementierungen.
*   **Models:**
    *   `ChatMessage.cs`, `ChatRole.cs`: Standardmodelle für Chat- nachrichten.
    *   `PipelineOptions.cs`: Enthält Flags zur Steuerung des Verhaltens der Verarbeitungspipeline (z.B. Deaktivieren von VAD, TTS, Progressive TTS, Streaming Chat, Auswahl der Sprache).
    *   `VadSettings.cs`: Enthält detaillierte Parameter für die Voice Activity Detection (z.B. `OperatingMode`, Schwellenwerte, Timing-Parameter wie `PreSpeechPaddingMs`, `HangOverMs`, `MinSegmentDurationSec`, Parameter für Spike Detection und Rauschanpassung).
*   **Services:**
    *   `ChatLogManager.cs`: Verwaltet das Logging von Chat-Interaktionen.

### 2.5. OpenAI-Plugins (`VoiceAssistant.Plugins.OpenAI/`)

Dieses Projekt enthält die konkreten Implementierungen der Kerninterfaces unter Verwendung der OpenAI-API:

*   `OpenAIApiRecognizer.cs`: Implementiert `IRecognizer` für Speech-to-Text mit OpenAI.
*   `OpenAIApiSynthesizer.cs`: Eine Basisimplementierung für `ISynthesizer` (Text-to-Speech).
*   `OpenAIChatService.cs`: Eine Basisimplementierung für `IChatService`.
*   `ProgressiveTTSSynthesizer.cs`: Implementiert `ISynthesizer` und unterstützt progressives/inkrementelles Synthetisieren von Sprache, um die wahrgenommene Latenz zu reduzieren. Sendet Audio-Daten, sobald erste Teile der Antwort verfügbar sind.
*   `StreamingOpenAIChatService.cs`: Implementiert `IChatService` und unterstützt das Streamen von Antworten vom OpenAI Chat-Modell Token für Token.

### 2.6. Frontend (`wwwroot/`)

Das Frontend ist eine Single-Page-Application (SPA), die mit modernem, modularem JavaScript (ES6), HTML5 und CSS implementiert ist. Sie dient als Referenzimplementierung und Testumgebung für die Echtzeit-Audioverarbeitung des Backends.

# Frontend-Architektur & Refactoring (Stand Juni 2025)

Das Frontend wurde grundlegend überarbeitet, um Stabilitätsprobleme zu beheben und die Codebasis auf moderne Web-Standards zu heben. Die ursprünglichen Probleme – kein sichtbarer Audio-Pegel und kein automatischer Aufnahmestart – wurden durch zwei zentrale Änderungen gelöst.

### 1. Kernerkenntnis: Automatischer Start und Browser-Richtlinien

**Problem:** Browser verhindern die automatische Wiedergabe (und Verarbeitung) von Audio, bis eine Benutzerinteraktion stattgefunden hat.

**Lösung:** Der Schlüssel liegt in der korrekten Initialisierungsreihenfolge. Anstatt auf einen expliziten "Start"-Button zu warten, nutzt die Anwendung nun die **Anfrage zur Mikrofonberechtigung** als die erforderliche Benutzerinteraktion.

1.  **Seite lädt:** `main.js` ruft `initAndStartAudioSystem()` auf.
2.  **Berechtigung zuerst:** Die App fordert sofort den Zugriff auf das Mikrofon an (`navigator.mediaDevices.getUserMedia`).
3.  **Geste des Benutzers:** Das Klicken auf "Zulassen" im Berechtigungsdialog wird vom Browser als die notwendige Geste gewertet.
4.  **AudioContext starten:** *Nachdem* die Berechtigung erteilt wurde, wird der `AudioContext` erstellt und/oder fortgesetzt. Er ist nun sofort im Zustand `running`.
5.  **Automatischer Start:** Da der `AudioContext` aktiv ist, kann die Audioverarbeitung (Pegelanzeige) und die WebSocket-Verbindung sofort und automatisch gestartet werden.

### 2. Kernerkenntnis: Performante Audioverarbeitung mit `AudioWorklet`

**Problem:** Die veraltete `ScriptProcessorNode`-API läuft auf dem Haupt-Thread der Benutzeroberfläche. Dies führte dazu, dass die UI (insbesondere die Audio-Pegelanzeige) bei hoher Last einfror und keine Audiodaten zuverlässig verarbeitet wurden.

**Lösung:** Die Implementierung wurde vollständig auf die moderne **`AudioWorklet`**-API umgestellt.

*   **Performance:** Ein `AudioWorklet` läuft in einem eigenen, vom UI-Thread getrennten Thread. Dies garantiert eine ruckelfreie, performante Audioverarbeitung ohne Blockieren der Benutzeroberfläche.
*   **Stabilität:** Die Verarbeitung ist robuster gegenüber Lastspitzen im Haupt-Thread.

### 3. Modulare Architektur

Die Frontend-Logik ist in spezialisierte, wiederverwendbare ES6-Module unterteilt:

*   **`main.js`**: Der Einstiegspunkt der Anwendung. Initialisiert die UI und startet den gesamten Audioprozess durch den Aufruf von `initAndStartAudioSystem`.

*   **`audio-system.js`**: Der **Orchestrator**. Diese zentrale Komponente steuert den gesamten Lebenszyklus der Audio-Pipeline:
    *   Implementiert die `initAndStartAudioSystem`-Logik (Berechtigung anfordern, Module initialisieren, Verarbeitung starten).
    *   Verwaltet den globalen Aufnahmestatus (`isRecordingActive`).
    *   Empfängt verarbeitete Audio-Chunks vom `microphone.js` und leitet sie an den `websocket-handler.js` weiter.
    *   Empfängt WebSocket-Events und delegiert sie an `tts-playback.js` oder `ui-manager.js`.

*   **`audio-context.js`**: Verwaltet den globalen, singleton `AudioContext`. Stellt sicher, dass dieser erst *nach* der Benutzergeste (Mikrofonberechtigung) erstellt wird.

*   **`microphone.js`**: Verantwortlich für die Interaktion mit dem Mikrofon und dem `AudioWorklet`.
    *   Ruft den `MediaStream` vom Mikrofon ab.
    *   Lädt den `audio-processor.js` Worklet und erstellt einen `AudioWorkletNode`.
    *   Verbindet die Audioquelle (`MediaStreamSource`) mit dem Worklet-Knoten.
    *   Empfängt Nachrichten (verarbeitete Audio-Chunks und RMS-Werte) vom Worklet und leitet sie über Callbacks an den `audio-system.js` (für WebSocket) und `ui-manager.js` (für Visualisierung) weiter.

*   **`audio-processor.js` (AudioWorkletProcessor)**: Das Herzstück der Audioverarbeitung. Dieser Code läuft in einem **separaten Thread**.
    *   Empfängt rohe Audio-Samples vom Mikrofon (typischerweise in 128-Sample-Blöcken).
    *   Puffert diese Samples und erstellt daraus exakte 20ms-Chunks (320 Samples bei 16kHz), die der Server erwartet.
    *   Berechnet den RMS-Wert (Root Mean Square) der Audiodaten für die Pegelanzeige.
    *   Sendet die fertigen Chunks und die RMS-Werte über eine `MessagePort` zurück an `microphone.js` im Haupt-Thread.

*   **`websocket-handler.js`**: Kapselt die gesamte WebSocket-Logik (Verbindung, Senden, Empfangen, Status-Handling).

*   **`tts-playback.js`**: Verwaltet die Wiedergabe der vom Server empfangenen TTS-Audio-Chunks.

*   **`ui-manager.js` & `optimization-manager.js`**: Verwalten die DOM-Manipulation, UI-Events und die Einstellungs-Panels.

### 3.3. Audio-Verarbeitung im Frontend

Die Audio-Verarbeitung im Frontend ist vollständig in `audio-system.js` und dem `AudioWorklet` (`audio-processor.js`) gekapselt.

**Ablauf der Audio-Aufnahme:**

1.  **Initialisierung:** `audio-system.js` initialisiert den `AudioContext` und lädt das `audio-processor.js`-Modul in den `AudioWorklet`.
2.  **Start:** Bei `startRecording()` (kann automatisch oder durch Benutzerinteraktion ausgelöst werden) wird der Zugriff auf das Mikrofon angefordert (`getUserMedia`).
3.  **Verarbeitungskette:** Der `MediaStream` vom Mikrofon wird an einen `AudioWorkletNode` weitergeleitet, der den `audio-processor` ausführt.
4.  **Chunking & RMS:** Der `audio-processor` empfängt die Audiodaten in Blöcken von 128 Samples. Er puffert diese Daten und erstellt daraus präzise Chunks (standardmäßig 320 Samples für 20ms bei 16kHz). Für jeden dieser Chunks berechnet er den RMS-Wert.
5.  **Datenübertragung:**
    - Der Audio-Chunk wird per `postMessage` an `audio-system.js` gesendet.
    - Der RMS-Wert wird ebenfalls per `postMessage` an `audio-system.js` gesendet.
6.  **Weiterleitung:**
    - `audio-system.js` leitet den Audio-Chunk an `websocket-handler.js` weiter, der ihn an den Server sendet.
    - `audio-system.js` leitet den RMS-Wert an `ui-manager.js` weiter, um die Pegelanzeige zu aktualisieren.

**Architektur-Diagramm (Frontend):**

`Mikrofon` -> `MediaStreamSource` -> `AudioWorkletNode` (`audio-processor.js`) -> `(verarbeiteter 20ms Chunk)` -> `port.postMessage` -> `audio-system.js` -> `websocket-handler.js` -> **Server**

`AudioWorkletNode` (`audio-processor.js`) -> `(berechneter RMS-Wert)` -> `port.postMessage` -> `audio-system.js` -> `ui-manager.js` -> **UI-Pegelanzeige**

### 3.4. Module und ihre Verantwortlichkeiten

- **main.js**: Einstiegspunkt der Anwendung, initialisiert alle Manager-Module.
- **audio-system.js**: Herzstück der Frontend-Audioverarbeitung. Orchestriert Mikrofon, `AudioWorklet`, `WebSocketHandler` und `TTS-Playback`.
- **audio-processor.js**: `AudioWorkletProcessor` für das Chunking und die RMS-Berechnung.
- **audio-context.js**: Verwaltet den globalen `AudioContext`.
- **websocket-handler.js**: Kapselt die WebSocket-Kommunikation.

### 4. Datenfluss (Audio & UI)

**Audio zum Server:**
`Mikrofon` -> `MediaStreamSource` -> `AudioWorkletNode` (`audio-processor.js`) -> `(verarbeiteter 20ms Chunk)` -> `port.postMessage` -> `audio-system.js` -> `websocket-handler.js` -> **Server**

**Audio-Pegel zur UI:**
`AudioWorkletNode` (`audio-processor.js`) -> `(berechneter RMS-Wert)` -> `port.postMessage` -> `audio-system.js` -> `ui-manager.js` -> **UI-Pegelanzeige**

# Modularisierung des Frontends (Stand Juni 2025)

## Neue Struktur der Audio-Frontend-Logik

Im Rahmen eines umfangreichen Refactorings wurde das große Frontend-Modul `audio-system.js` in mehrere funktionsorientierte ES6-Module aufgeteilt. Ziel war eine bessere Wartbarkeit, Übersichtlichkeit und Testbarkeit. Die Orchestrationslogik verbleibt in `audio-system.js`.

### Neue Modulstruktur (`wwwroot/js/`):
- **audio-system.js**: Orchestrator, zentrale Steuerung, UI-Callbacks, Initialisierung aller Subsysteme.
- **tts-playback.js**: Verwaltung der TTS-Playback-Logik (Chunk-Management, Wiedergabeschleife, State-Reset).
- **audio-context.js**: Singleton-Management des AudioContext (get/resume/close).
- **microphone.js**: Mikrofon- und Audioverarbeitungslogik (MediaStream, Buffering, RMS, Ressourcenfreigabe).
- **websocket-handler.js**: WebSocket-Management (Verbindungsaufbau, Senden/Empfangen, State, Event-Handler-Registrierung).
- **audio-utils.js**: Hilfsfunktionen (debugLog, IS_DEBUG_MODE, PCM-Konvertierung, Chunk-Indexing).

### Vorteile der Modularisierung
- Keine Datei >400 Zeilen, klar getrennte Verantwortlichkeiten.
- Bessere Testbarkeit und Lesbarkeit.
- Einfachere Erweiterbarkeit (z.B. für alternative Audioquellen oder neue Protokolle).

### Hinweise zur Migration/Verwendung
- Alle Importe/Exporte erfolgen als ES6-Module (z.B. `import * as ttsPlayback from './tts-playback.js'`).
- Die Haupt-UI-Logik und alle Interaktionen mit `window.uiManager` und `window.optimizationManager` laufen weiterhin über `audio-system.js`.
- Die Submodule sind weitgehend unabhängig und können einzeln getestet werden.

### Beispiel für die Initialisierung (aus `audio-system.js`):
```js
import * as ttsPlayback from './tts-playback.js';
import * as audioContextManager from './audio-context.js';
import * as microphoneManager from './microphone.js';
import * as webSocketHandler from './websocket-handler.js';
import * as audioUtils from './audio-utils.js';

// ...

export async function initAudioSystem() {
    audioContextManager.initAudioContextModule(...);
    ttsPlayback.initTtsPlayback(...);
    microphoneManager.initMicrophone(...);
    webSocketHandler.initWebSocketHandler(...);
    // ...
}
```

### Funktionalität bleibt erhalten
Alle bisherigen Features (TTS, VAD, Streaming, UI-Callbacks, Fehlerbehandlung) sind weiterhin verfügbar, aber klarer getrennt und leichter wartbar.

---

Die restliche README bleibt unverändert und beschreibt weiterhin Backend, API, Konfiguration und Teststruktur.

## 3. Konfigurationsoptionen

Die Anwendung bietet mehrere Ebenen der Konfiguration:

*   **`appsettings.json`:**
    *   Konfiguration für Serilog (Logging-Level, Ausgabeziele).
    *   Standardwerte für `PipelineOptions`.
*   **Umgebungsvariablen:**
    *   `OPENAI_API_KEY`: Zwingend erforderlich für die Kommunikation mit der OpenAI-API.
*   **WebSocket-Nachrichten (dynamisch zur Laufzeit):**
    *   Clients können JSON-Nachrichten mit dem Typ `updateVadSettings` oder `updatePipelineOptions` senden, um die entsprechenden Einstellungen im laufenden `WebSocketAudioService` zu ändern. Dies ermöglicht eine feingranulare Echtzeit-Anpassung der VAD- und Pipeline-Parameter.
*   **HTTP Endpunkte (über `SettingsController`):**
    *   Bieten eine alternative Möglichkeit, `VadSettings` und `PipelineOptions` abzurufen und zu setzen.
*   **Query-Parameter bei WebSocket-Verbindung (teilweise implementiert/auskommentiert):**
    *   In `Program.cs` ist Code vorhanden (aktuell auskommentiert), um `model` (Chat-Modell) und `voice` (TTS-Stimme) als Query-Parameter beim Aufbau der WebSocket-Verbindung zu akzeptieren. Diese würden dann vermutlich die `PipelineOptions` oder `VadSettings` initial überschreiben.

## 4. Mögliches Optimierungs- und Refactoring-Potenzial

*   **Klärung der Query-Parameter-Logik:** Die auskommentierte Logik für `model` und `voice` Query-Parameter in `Program.cs` sollte entweder entfernt oder vollständig implementiert und dokumentiert werden. Wenn sie genutzt werden soll, muss sichergestellt werden, dass diese Werte korrekt an den `WebSocketAudioService` übergeben und dort berücksichtigt werden (z.B. durch Modifikation der initialen `PipelineOptions` oder `VadSettings` Instanz, die an den Service übergeben wird).
*   **Konsistenz der Konfigurationsübernahme:** Sicherstellen, dass Änderungen an `PipelineOptions` (z.B. `Language`, `ChatModel`, `TtsVoice`) sich konsistent auf alle relevanten Dienste auswirken, insbesondere wenn diese zur Laufzeit geändert werden.
*   **Feingranulare Fehlerbehandlung:** Eine noch detailliertere Fehlerbehandlung und -kommunikation an den Client könnte die Robustheit verbessern (z.B. spezifische Fehlermeldungen bei Problemen mit der API, ungültigen Audioformaten etc.).
*   **Ressourcenmanagement:** Überprüfen, ob alle `IDisposable`-Objekte korrekt freigegeben werden, insbesondere im Kontext langlebiger WebSocket-Verbindungen und der dynamischen Erzeugung von Streams.
*   **Testabdeckung:** Erweiterung der Unit- und Integrationstests (`VoiceAssistant.Tests/`) zur Abdeckung weiterer Szenarien, insbesondere der dynamischen Konfigurationsänderungen und verschiedener VAD-Einstellungen.
*   **Dokumentation der WebSocket-API:** Eine klare Dokumentation der WebSocket-Nachrichtenformate (sowohl Client-zu-Server als auch Server-zu-Client) wäre hilfreich für Entwickler, die Clients für den Service erstellen.
*   **Initial Noise Floor Measurement:** Das `MeasureInitialNoiseFloor` in `WebSocketAudioService` ist ein guter Start. Man könnte überlegen, ob diese Kalibrierung periodisch wiederholt oder durch den Benutzer manuell ausgelöst werden kann, um sich an verändernde Umgebungsgeräusche anzupassen, falls die automatische Anpassung nicht ausreicht.
*   **Modularität der VAD-Logik:** Die VAD-Logik in `HandleAsync` ist recht umfangreich. Teile davon könnten in separate Methoden oder sogar eigene Klassen ausgelagert werden, um die Lesbarkeit und Wartbarkeit zu verbessern.

## 5. Logging

*   Die Anwendung verwendet Serilog für strukturiertes Logging.
*   Die Konfiguration erfolgt über `appsettings.json`.
*   Wie im alten README erwähnt, wurden detaillierte Pro-Token-Debug-Logs auf den Trace-Level verlegt, um Log-Spam zu reduzieren. Die Frontend-Timeline wurde ebenfalls bereinigt.

## 6. Testing (`VoiceAssistant.Tests/`)

Das Projekt enthält Tests, darunter:
*   `EndToEndFlowTests.cs`
*   `ProcessAudioTests.cs`
*   `StreamingSpeechLatencyTests.cs`
*   `TokenStreamingTests.cs`
*   `VADCalibrationTests.cs`
*   `WebSocketAudioStreamingTests.cs`
*   Ein spezifischer Test `ElefantenAudio_ReturnsExpectedPrompt` (erwähnt im alten README) verwendet eine Testdatei, um den `/api/processAudio`-Endpunkt zu validieren. Es ist wichtig, solche Tests aktuell zu halten und zu erweitern.

Diese detaillierte Beschreibung sollte einen umfassenden Einblick in die Funktionsweise der Anwendung geben und als Grundlage für zukünftige Entwicklungen und Einarbeitungen dienen.

## 7. Web-Frontend (`wwwroot/`)

Das mitgelieferte Web-Frontend dient als Referenzimplementierung und als Testumgebung für die Backend-Funktionalitäten. Es ist eine Single-Page-Application (SPA), die mit HTML, CSS und reinem JavaScript implementiert ist.

### 7.1. Struktur und Komponenten

*   **`index.html`**: Definiert die Hauptstruktur der Benutzeroberfläche. Enthält Steuerelemente für die Modellauswahl (Chat, Sprache, TTS-Stimme), Buttons zur Interaktionssteuerung (Aufnahme starten/stoppen, Chat leeren) und Bereiche zur Anzeige des Chatverlaufs sowie detaillierte Panels für Debugging und Performance-Optimierung.
*   **`js/main.js`**: Der Einstiegspunkt der Frontend-Anwendung. Initialisiert die verschiedenen JavaScript-Module und lädt anfängliche Daten wie Chat-Modelle, TTS-Stimmen und die bestehende Chat-Historie vom Backend.
*   **`js/audio-system.js`**: Diese Komponente ist zentral für die gesamte Audiointeraktion:
    *   **Audioaufnahme**: Greift über `navigator.mediaDevices.getUserMedia` auf das Mikrofon zu.
    *   **WebSocket-Kommunikation**: Stellt eine Verbindung zum `/ws/audio` Endpunkt des Backends her. Über diese Verbindung werden kontinuierlich Audio-Frames (PCM, 16kHz, 1-Kanal, 20ms) an den Server gesendet. Es werden ebenfalls JSON-Nachrichten für Steuerungszwecke (z.B. `updateVadSettings`, `updatePipelineOptions`) gesendet und Server-Events (`prompt`, `token`, `audioChunk`, `done`, `error`) empfangen.
    *   **Audio-Wiedergabe**: Verarbeitet die vom Server als `audioChunk` gesendeten TTS-Daten. Nutzt die Web Audio API, um diese Chunks (typischerweise Base64-kodierte MP3-Segmente) zu dekodieren und sequenziell abzuspielen, was Progressive TTS ermöglicht.
    *   **Legacy HTTP Pipeline**: Beinhaltet eine alternative Methode zur Audioverarbeitung, bei der die gesamte Aufnahme an `/api/processAudio` gesendet und die Sprachausgabe von `/api/speech` abgerufen wird. Dies dient als Fallback oder für Tests.
*   **`js/ui-manager.js`**: Verantwortlich für die dynamische Manipulation des Document Object Model (DOM). Dies umfasst das Anzeigen von Benutzer- und Bot-Nachrichten im Chat-Protokoll, das Aktualisieren von Statusanzeigen und Latenzinformationen sowie das Management der Sichtbarkeit der Debug- und Optimierungs-Panels.
*   **`js/optimization-manager.js`**: Verwaltet die Logik hinter den Einstellungs-Panels:
    *   **Pipeline-Optionen**: Ermöglicht das Umschalten von Funktionen wie Progressive TTS, Token Streaming, VAD-Nutzung und TTS-Nutzung.
    *   **VAD-Einstellungen**: Bietet detaillierte Kontrolle über serverseitige VAD-Parameter (Spike Detection, Schwellenwerte etc.).
    *   **Persistenz**: Speichert die vom Benutzer vorgenommenen Einstellungen im `localStorage` des Browsers.
    *   **Backend-Synchronisation**: Sendet die geänderten Einstellungen über WebSocket-Nachrichten (`updateVadSettings`, `updatePipelineOptions`) an das Backend, um dessen Verhalten zur Laufzeit anzupassen.

### 7.2. Kernfunktionalitäten und Überlegungen für andere Clients (z.B. Unity)

Das Web-Frontend demonstriert die Kerninteraktion mit dem Backend. Für einen alternativen Client, wie eine VR-Anwendung in Unity, sind folgende Aspekte zentral:

*   **Audio-Ein-/Ausgabe**: Der Client muss Audio vom Mikrofon aufnehmen und im vom Server erwarteten Format (16kHz PCM, 20ms Frames) bereitstellen können. Ebenso muss er die empfangenen Audio-Chunks (TTS-Antworten) dekodieren und abspielen können.
*   **WebSocket-Verbindung**: Eine stabile WebSocket-Implementierung ist notwendig, um:
    *   Audio-Daten kontinuierlich an den `/ws/audio` Endpunkt zu streamen.
    *   JSON-basierte Nachrichten vom Server zu empfangen und zu parsen (für Transkripte, LLM-Tokens, Audio-Daten, Status-Updates).
    *   Optional: JSON-basierte Nachrichten an den Server zu senden, um `PipelineOptions` oder `VadSettings` dynamisch anzupassen. Dies ist nützlich, um das Verhalten an unterschiedliche Nutzer oder Umgebungen anzupassen, ohne das Backend neu starten zu müssen.
*   **Minimale Client-Logik**: Die Kernlogik (VAD, STT, LLM, TTS) verbleibt auf dem Server. Der Client ist primär für die Audio-Interaktion und die Darstellung der Ergebnisse zuständig. Die umfangreichen Debug- und Konfigurations-UI-Elemente des Web-Frontends sind für einen Endanwender-Client (wie in VR) nicht zwingend erforderlich und können für eine schlankere Implementierung weggelassen werden.

Das Ziel ist es, den Client so einfach wie möglich zu halten, während die volle Funktionalität durch die serverseitige Verarbeitung gewährleistet wird. Die im Web-Frontend vorhandenen Debug-Panels sind wertvoll für Testzwecke, zeigen aber auch die Flexibilität der Backend-Konfiguration über die WebSocket-Schnittstelle.

## 9. Bekannte Issues und Roadmap

### 9.1. Aktuelle Limitationen (Stand 2025)

#### 9.1.1. Frontend Issues
- **Browser-Kompatibilität:** AudioWorklet wird nicht von allen älteren Browsern unterstützt
- **Mobile Responsiveness:** UI-Panels sind nicht für mobile Geräte optimiert
- **Audio-Latenz:** Minimale Latenz bei TTS-Playback auf manchen Geräten
- **Memory Usage:** Langfristige Sessions können zu Memory-Leaks bei unsachgemäßer Bereinigung führen

#### 9.1.2. Backend Issues
- **Concurrent Sessions:** Noch keine explizite Begrenzung der gleichzeitigen WebSocket-Verbindungen
- **Resource Monitoring:** Fehlende Metriken für Memory/CPU-Usage pro Session
- **Error Recovery:** Partielle Failures in der TTS-Pipeline können die gesamte Session blockieren

#### 9.1.3. Integration Issues
- **Unity Integration:** VoiceAssistant.UnityIntegration noch nicht vollständig in die neue Architektur integriert
- **Testing Coverage:** E2E-Tests für neue WebSocket-Events noch unvollständig

### 9.2. Geplante Verbesserungen

#### 9.2.1. Kurzfristig (Q1 2025)
- [ ] **Mobile UI:** Responsive Design für Tablet/Phone-Nutzung
- [ ] **Error Boundaries:** Bessere Isolation von Fehlern zwischen Sessions
- [ ] **Metrics Dashboard:** Echtzeit-Monitoring für aktive Sessions
- [ ] **Unity Integration:** Vollständige Portierung auf neue WebSocket-Architektur

#### 9.2.2. Mittelfristig (Q2 2025)
- [ ] **Multi-Language TTS:** Dynamische Sprachwahl basierend auf Chat-Content
- [ ] **Voice Cloning:** Integration von Custom-Voice-Training
- [ ] **Advanced VAD:** ML-basierte Voice Activity Detection als Alternative
- [ ] **Load Balancer:** Horizontal Skalierung für hohe Session-Counts

#### 9.2.3. Langfristig (Q3+ 2025)
- [ ] **Real-time Translation:** Live-Übersetzung während der Konversation
- [ ] **Emotion Detection:** Sentiment-Analysis für emotionale TTS-Anpassung
- [ ] **Multi-Modal Input:** Integration von Visual/Gesture Input zusätzlich zu Audio
- [ ] **Edge Computing:** Lokale STT/TTS-Processing für Latenz-kritische Anwendungen

### 9.3. Regression-Prävention

#### 9.3.1. Automatisierte Tests
**Erforderlich für jede Änderung:**
- [ ] WebSocket-Message-Format-Validierung
- [ ] Token-Streaming-Vollständigkeit
- [ ] TTS-Chunk-Ordering
- [ ] Latenz-Metriken-Genauigkeit
- [ ] Frontend-Module-Initialisierung

#### 9.3.2. Code Review Checklist
**Vor jedem Merge prüfen:**
- [ ] Payload-Struktur konsistent (keine Doppel-Serialisierung)
- [ ] Event-Handler-Signaturen korrekt
- [ ] Resource-Cleanup implementiert
- [ ] Debug-Logging strukturiert
- [ ] Dependency-Injection-Zyklen vermieden

#### 9.3.3. Performance Benchmarks
**Kontinuierliche Überwachung:**
- [ ] End-to-End Latenz < 2 Sekunden
- [ ] Memory-Usage < 100MB pro Session
- [ ] CPU-Usage < 50% bei 10 gleichzeitigen Sessions
- [ ] WebSocket-Connection-Stabilität > 99.5%

### 9.4. Migrationshilfen

#### 9.4.1. Legacy-Code-Migration
Für Entwickler, die von der alten monolithischen Architektur migrieren:

```csharp
// ALT: Monolithischer WebSocketAudioService
public class WebSocketAudioService {
    // Alles in einer Klasse
}

// NEU: Modulare Architektur
public class WebSocketHandler {
    // Nur WebSocket-Management
}
public class AudioFrameProcessor {
    // Nur Frame-Processing
}
public class AudioSegmentProcessor {
    // Nur Segment-Processing
}
```

#### 9.4.2. Frontend-Migration
```javascript
// ALT: Monolithisches audio-system.js
// NEU: Modulare Struktur
import * as ttsPlayback from './tts-playback.js';
import * as webSocketHandler from './websocket-handler.js';
// etc.
```

### 9.5. Troubleshooting Guide

#### 9.5.1. Häufige Probleme

**Problem:** Audio-Input funktioniert nicht
```javascript
// Debug-Schritte:
1. Mikrofon-Berechtigung prüfen: navigator.permissions.query({name: 'microphone'})
2. AudioContext-State prüfen: audioContext.state
3. MediaStream-Tracks prüfen: stream.getAudioTracks()[0].enabled
4. AudioWorklet-Status prüfen: workletNode.port.postMessage({type: 'ping'})
```

**Problem:** TTS-Audio fragmentiert
```javascript
// Debug-Schritte:
1. Chunk-Reihenfolge prüfen: console.log('Expected:', nextExpectedIndex, 'Got:', chunkIndex)
2. Queue-Status prüfen: console.log('Queue length:', chunkQueue.length)
3. Audio-Decoder-Fehler prüfen: audioContext.decodeAudioData().catch(console.error)
```

**Problem:** WebSocket-Verbindung instabil
```csharp
// Debug-Schritte:
1. Server-Logs prüfen: Serilog Debug-Level aktivieren
2. Connection-State tracken: WebSocket.State logging
3. Heartbeat implementieren: Ping/Pong-Messages
```

#### 9.5.2. Performance-Profiling

**Frontend:**
```javascript
// Performance-Monitoring aktivieren
performance.mark('audio-processing-start');
// ... Audio-Processing ...
performance.mark('audio-processing-end');
performance.measure('audio-processing', 'audio-processing-start', 'audio-processing-end');
```

**Backend:**
```csharp
// Latenz-Tracking
var stopwatch = Stopwatch.StartNew();
// ... Processing ...
var latency = stopwatch.ElapsedMilliseconds;
_logger.LogInformation("Processing completed in {Latency}ms", latency);
```

### 9.6. Beitrag zur Codebasis

#### 9.6.1. Development Setup
```bash
# Backend
dotnet restore
dotnet build
dotnet test

# Frontend (optional, für erweiterte Tests)
npm install
npm run test
```

#### 9.6.2. Pull Request Guidelines
1. **Tests:** Alle neuen Features müssen getestet werden
2. **Documentation:** README-Updates für API-Änderungen
3. **Performance:** Keine Performance-Regression ohne Begründung
4. **Breaking Changes:** Ausführliche Migration-Dokumentation erforderlich

Diese umfassende Dokumentation soll als lebende Referenz dienen und kontinuierlich mit der Weiterentwicklung der Anwendung aktualisiert werden.

## 8. WebSocket-Protokoll-Spezifikation

### 8.1. Verbindungsaufbau

Der WebSocket-Endpunkt ist unter `/ws/audio` verfügbar. Die Verbindung wird automatisch akzeptiert, wenn `PipelineOptions.UseLegacyHttp` auf `false` gesetzt ist.

### 8.2. Message-Typen (Client → Server)

#### 8.2.1. Audio-Frames (Binärdaten)
- **Format:** PCM, 16-bit, 16kHz, Mono
- **Chunk-Größe:** 20ms (320 Samples)
- **Übertragung:** WebSocket Binary Message
- **Frequenz:** ~50 Nachrichten/Sekunde

#### 8.2.2. Kontroll-Nachrichten (JSON)

**VAD-Einstellungen aktualisieren:**
```json
{
  "type": "updateVadSettings",
  "payload": {
    "operatingMode": "Auto",
    "manualThreshold": 0.02,
    "prerollBufferSizeMs": 300,
    "preSpeechPaddingMs": 300,
    "hangOverMs": 1000,
    "minSegmentDurationSec": 0.5,
    "maxSegmentDurationSec": 30.0,
    "enableSpikeDetection": true,
    "spikeThreshold": 0.15,
    "enableNoiseFloorAdaptation": true,
    "adaptationRate": 0.1
  }
}
```

**Pipeline-Optionen aktualisieren:**
```json
{
  "type": "updatePipelineOptions",
  "payload": {
    "language": "de",
    "chatModel": "gpt-4o-mini",
    "ttsVoice": "nova",
    "disableVad": false,
    "disableTts": false,
    "disableProgressiveTts": false,
    "disableStreamingChat": false,
    "useLegacyHttp": false
  }
}
```

### 8.3. Message-Typen (Server → Client)

#### 8.3.1. Textuelle Nachrichten (JSON)

**Transkription bereit:**
```json
{
  "type": "prompt",
  "payload": "Hallo, wie geht es dir?"
}
```

**Token-Streaming (Chat-Antwort):**
```json
{
  "type": "token",
  "payload": "Hallo"
}
```

**Audio-Chunk-Metadaten:**
```json
{
  "type": "audio-chunk-info",
  "payload": {
    "index": 0
  }
}
```

**Verarbeitung abgeschlossen:**
```json
{
  "type": "done",
  "payload": {
    "textLatency": 1234,
    "audioLatency": 2345,
    "total": 3579
  }
}
```

**Fehlermeldung:**
```json
{
  "type": "error",
  "payload": {
    "message": "API-Fehler: Rate limit exceeded",
    "code": "RATE_LIMIT"
  }
}
```

**Einstellungen bestätigt:**
```json
{
  "type": "vadSettingsUpdated",
  "payload": { /* aktuelle VAD-Einstellungen */ }
}
```

```json
{
  "type": "pipelineOptionsUpdated",
  "payload": { /* aktuelle Pipeline-Optionen */ }
}
```

#### 8.3.2. Audio-Chunks (Binärdaten)

- **Format:** MP3-kodierte Audio-Segmente
- **Übertragung:** WebSocket Binary Message
- **Reihenfolge:** Sequenziell, mit vorangehender `audio-chunk-info`
- **Timing:** Streaming-basiert, sobald TTS-Chunks verfügbar sind

### 8.4. Protokoll-Fluss

#### 8.4.1. Typischer Konversations-Ablauf

```
1. Client sendet Audio-Frame (Binary)
2. Client sendet Audio-Frame (Binary)
...
[VAD erkennt Ende des Sprachsegments]
3. Server sendet "prompt" (JSON)
4. Server sendet "token" (JSON) [mehrfach]
5. Server sendet "audio-chunk-info" (JSON)
6. Server sendet Audio-Chunk (Binary)
7. Server sendet "audio-chunk-info" (JSON)
8. Server sendet Audio-Chunk (Binary)
...
9. Server sendet "done" (JSON)
```

#### 8.4.2. Einstellungen-Update-Fluss

```
1. Client sendet "updateVadSettings" (JSON)
2. Server sendet "vadSettingsUpdated" (JSON)
oder
2. Server sendet "error" (JSON) [bei ungültigen Werten]
```

### 8.5. Fehlerbehandlung

#### 8.5.1. Verbindungsabbruch
- **Client:** Automatische Reconnection mit exponential backoff
- **Server:** Graceful shutdown mit CloseStatus logging

#### 8.5.2. Ungültige Nachrichten
- **Unbekannte JSON-Typen:** Ignoriert, geloggt
- **Malformed JSON:** Connection wird geschlossen
- **Ungültige Einstellungen:** Error-Response gesendet

#### 8.5.3. API-Fehler
- **OpenAI-Errors:** Als "error"-Message weitergeleitet
- **Timeout:** Connection wird geschlossen mit entsprechendem CloseStatus

### 8.6. Performance-Optimierungen

#### 8.6.1. Buffering
- **Audio-Input:** 20ms-Chunks für konsistente Verarbeitung
- **TTS-Output:** Progressive Chunks für niedrige Latenz

#### 8.6.2. Compression
- **JSON-Messages:** Minified, keine Whitespace
- **Audio-Data:** MP3-Kompression für TTS-Output

#### 8.6.3. Connection Management
- **Keep-Alive:** Automatische Ping/Pong-Nachrichten
- **Resource Cleanup:** Explizite Bereinigung bei Verbindungsende

### 8.7. Debugging und Monitoring

#### 8.7.1. Client-Side Logging
```javascript
// Debug-Modus aktivieren
localStorage.setItem('debugMode', 'true');
// oder URL-Parameter: ?debug=true
```

#### 8.7.2. Server-Side Logging
- **Verbindungen:** Session-IDs für Tracking
- **Performance:** Latenz-Metriken für jeden Request
- **Fehler:** Detaillierte Exception-Logs mit Stack Traces

#### 8.7.3. Message-Flow Debugging
- **Sequenz:** Jede Nachricht wird mit Timestamp geloggt
- **Payload:** Strukturierte Logs für JSON-Inhalte
- **Binary Data:** Größe und Typ-Information