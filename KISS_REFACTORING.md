<!-- KISS_REFACTORING.md: Refactoring-Plan für modulare Pipeline mit Feature-Flags -->
# Keep It Simple Stupid (KISS) Refactoring Plan

Dieser Plan beschreibt die notwendigen Schritte, um unsere Audio-→Transkript-→Chat-→TTS-Pipeline modular zu gestalten und per SettingsController steuerbare Feature-Flags einzuführen. Jeder Schritt ist als Checklisteneintrag markierbar. Bereits abgeschlossene Tasks sind mit "[x]" markiert.

## 1. PipelineOptions definieren
- [x] Neue Klasse `PipelineOptions` in `VoiceAssistant.Core.Models` mit folgenden Properties:
  - `bool UseLegacyHttp`
  - `bool DisableVad`
  - `bool DisableTokenStreaming`
  - `bool DisableProgressiveTts`

## 2. Optionen per IConfiguration und IOptions binden
- [x] In `Program.cs` mittels `builder.Services.Configure<PipelineOptions>(Configuration.GetSection("PipelineOptions"))` die Section aus `appsettings.json` binden.

## 3. SettingsController erweitern
- [x] Existierenden `SettingsController` so anpassen, dass er `PipelineOptions` lesen und aktualisieren kann (z. B. GET/PUT auf `/api/settings/pipeline`).

## 4. Legacy-HTTP-Endpunkte bewusst nutzen
- [x] In `Program.cs` WebSocket-Mapping (`/ws/audio`) `PipelineOptions.UseLegacyHttp` abfragen und bei `true` den WS-Endpoint deaktivieren (400 BadRequest), sodass der Client per HTTP-Post (Legacy-Pipeline) arbeiten muss.

## 5. WebSocketAudioService parametrisieren
- [x] In `WebSocketAudioService` Konstruktor und `HandleAsync` via DI auf `PipelineOptions` zugreifen und das Verhalten steuern:
  - Wenn `UseLegacyHttp`, komplette Audio-Frames sammeln und `ProcessSegmentAsync` beim Close einmal aufrufen.
  - Wenn `DisableVad`, direkt segmentiert ohne VAD-Logik.
  - Wenn `DisableTokenStreaming`, im Chat-Service-Block das Token-Streaming überspringen und stattdessen Komplett-Antwort senden.
  - Wenn `DisableProgressiveTts`, statt chunked TTS den Ein-Shoot-TTS verwenden.

## 6. Frontend-Optimierungs-Panel reaktivieren
- [x] Optimierungs-Panel im Frontend (Web UI) wieder lauffähig machen:
  - Checkboxen für Pipeline-Flags eingebunden und initialisiert via GET `/api/settings/pipeline` oder `localStorage`.
  - Änderungen werden via PUT `/api/settings/pipeline` in SettingsController gespeichert.
  - Pipeline-Flags (`UseLegacyHttp`, `DisableVad`, `DisableTokenStreaming`, `DisableProgressiveTts`) werden automatisch angewendet beim Verbindungsaufbau.

## 7. Latenzmessung im Frontend implementieren
- [x] End-to-End-Latenz messen:
  * WebSocket-Streaming:
    - `transcriptionReceived`: Zeitpunkt, wenn Transkript (Prompt) empfangen wird (Ende der Audio-Übertragung).
    - `llmResponseStart`: Zeitpunkt, wenn erste Tokens empfangen werden (Text-Latenz).
    - `ttsEnd`: Zeitpunkt, wenn erstes Audio-Chunk ankommt und abspielbereit ist (Audio-Latenz).
  * HTTP-Post-Modus:
    - `recordingStop`: Zeitpunkt, wenn manuelle Aufnahme endet.
    - `transcriptionReceived`: Zeitpunkt, wenn `/api/processAudio`-Antwort empfangen wird.
    - `llmResponseStart`: Zeitpunkt, wenn Chat-Antwort empfangen wird.
    - `ttsEnd`: Zeitpunkt, wenn Audio-Blob-Download abgeschlossen.
- [x] HTTP-Post-Modus ebenfalls lückenlos instrumentiert und individuelle Latenzen an jeder Bot-Message angezeigt.
- [x] HTTP-Post-Modus ebenfalls lückenlos instrumentiert.

## 8. Dokumentation und Konfiguration
+ [ ] Beispiel-Section in `appsettings.json` unter `PipelineOptions` ergänzen.
+ [ ] README aktualisieren mit Beschreibung der neuen Feature-Flags und Frontend-Panel.

## 9. Testing und Validierung
+ [ ] Manuelle Testläufe mit verschiedenen Combinations:
  - Legacy vs. Streaming
  - VAD an/aus
  - Token-Streaming an/aus
  - Progressive-TTS an/aus
  → Latenzwerte vergleichen und optimalen Modus ermitteln.
- [ ] (optional) Integrationstests über WebSocket und HTTP für alle Modi.

---
*Stand: Feature-Modularisierung abgeschlossen. Nächste Schritte: Testing und Dokumentation.*