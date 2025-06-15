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

### 2.2. WebSocket-Audioverarbeitung (`WebSocketAudioService.cs`)

Der `WebSocketAudioService` ist die komplexeste und zentralste Komponente der Anwendung. Er ist verantwortlich für den Empfang von Audio-Daten über WebSockets, die Durchführung der Voice Activity Detection (VAD), die Koordination von Spracherkennung (STT), Chat-Interaktion und Text-zu-Sprache-Synthese (TTS) sowie das Senden von Ereignissen und Audio-Daten zurück an den Client.

**Initialisierung und Konfiguration:**

*   Der Konstruktor nimmt verschiedene Dienste entgegen, darunter `IRecognizer`, `IChatService`, `ChatLogManager`, `ISynthesizer`, `ILogger`, sowie die initialen `VadSettings` und `PipelineOptions`.
*   **Wichtig:** `VadSettings` und `PipelineOptions` sind im `WebSocketAudioService` als Instanzfelder gespeichert und können zur Laufzeit durch spezielle WebSocket-Nachrichten vom Client modifiziert werden (siehe Methoden `HandleUpdateVadSettingsAsync` und `HandleUpdatePipelineOptionsAsync`). Dies ermöglicht eine dynamische Anpassung des Verhaltens ohne Neustart.
*   Ein `WebRtcVad`-Objekt (`WebRtcVadSharp`-Bibliothek) wird initialisiert und über `ConfigureVad()` konfiguriert. Die Konfiguration (z.B. `OperatingMode`) basiert auf den aktuellen `_settings` (also `VadSettings`).
*   Der initiale **Rauschpegel (`_noiseFloor`)** wird durch `MeasureInitialNoiseFloor()` für eine kurze Kalibrierungsphase gemessen. Dieser Wert dient als Basis für die VAD-Logik.

**Hauptverarbeitungsschleife (`HandleAsync`):**

Die `HandleAsync`-Methode läuft, solange die WebSocket-Verbindung offen ist und verarbeitet eingehende Nachrichten:

*   **Nachrichtentypen:**
    *   `WebSocketMessageType.Close`: Behandelt das Schließen der Verbindung. Wenn noch Audiodaten im Puffer sind (`rawAudio` bei deaktivierter VAD oder `segmentBuffer` bei aktivierter VAD), wird versucht, diese final zu verarbeiten.
    *   `WebSocketMessageType.Text`: Eingehende Textnachrichten werden als JSON interpretiert. Diese können Steuerbefehle enthalten, z.B. zum Aktualisieren der `VadSettings` (`updateVadSettings`) oder `PipelineOptions` (`updatePipelineOptions`). Die entsprechenden Handler (`HandleUpdateVadSettingsAsync`, `HandleUpdatePipelineOptionsAsync`) parsen das JSON-Payload und aktualisieren die internen Konfigurationsobjekte.
    *   `WebSocketMessageType.Binary`: Dies sind die rohen Audio-Daten vom Client, erwartet im Format 16kHz, 1 Kanal, 16 Bit PCM, in Frames von `FrameDurationMs` (typischerweise 20ms).

*   **Audio-Frame-Verarbeitung (wenn VAD aktiviert):**
    1.  **Vorverstärkung:** Optional wird eine Vorverstärkung (`_settings.PreAmplification`) auf den Frame angewendet (`ApplyPreAmplification`).
    2.  **RMS-Berechnung:** Der RMS-Wert des Frames wird berechnet (`CalculateRms`) als Maß für die Energie.
    3.  **VAD-Logik:**
        *   **Spike Detection (`_settings.EnableSpikeDetection`):** Wenn aktiv und der Frame-RMS einen Schwellenwert (`_settings.VadSpikeThreshold`) überschreitet (und signifikant über dem Rauschpegel liegt), kann dies einen potenziellen Sprachbeginn signalisieren (`potentialSpikeDetected`).
        *   **Third-Party VAD (`_settings.EnableThirdPartyVad`):** Wenn aktiv, wird die `_vad.HasSpeech(frame)` Methode der `WebRtcVadSharp`-Bibliothek genutzt, um zu entscheiden, ob der Frame Sprache enthält.
        *   **Kombinierte Logik:** Die Ergebnisse aus Spike Detection und/oder Third-Party VAD werden kombiniert, um `activeSpeechSignal` zu bestimmen.
        *   **Rauschpegelanpassung:** Wenn für eine bestimmte Zeit (`_settings.SilenceAdaptationTimeSec`) Stille erkannt wird, wird der Rauschpegel (`_noiseFloor`) neu berechnet und angepasst.
        *   **Hysterese und Pufferung:**
            *   `preFrames`: Anzahl der Frames, die vor einem erkannten Sprachbeginn gepuffert werden (`_settings.PreSpeechPaddingMs`).
            *   `startFrames`: Anzahl der aufeinanderfolgenden Sprachframes, die benötigt werden, um den Zustand `inSpeech` zu aktivieren (`_settings.SpeechStartThresholdMs`).
            *   `endFrames`: Anzahl der aufeinanderfolgenden Stille-Frames, die benötigt werden, um den Zustand `inSpeech` zu beenden (`_settings.HangOverMs`).
            *   Wenn `inSpeech` beginnt, werden die gepufferten `preBuffer`-Frames und der aktuelle Frame dem `segmentBuffer` hinzugefügt.
            *   Solange `inSpeech` aktiv ist, werden alle Frames dem `segmentBuffer` hinzugefügt.
            *   Wenn `inSpeech` endet (nach `endFrames` Stille-Frames), wird der Inhalt des `segmentBuffer` an `ProcessSegmentAsync` zur weiteren Verarbeitung gesendet und der Buffer geleert.
    4.  **Audio-Frame-Verarbeitung (wenn VAD deaktiviert - `_pipelineOptions.DisableVad`):**
        *   Die ankommenden Audio-Frames werden direkt in `rawAudio` gesammelt.
        *   Die Verarbeitung (`ProcessSegmentAsync`) erfolgt erst, wenn die WebSocket-Verbindung geschlossen wird oder eine spezielle Textnachricht (nicht explizit im Code ersichtlich, aber denkbar) den Abschluss signalisiert.

**Segmentverarbeitung (`ProcessSegmentAsync`):**

Diese Methode wird aufgerufen, sobald ein Sprachsegment durch die VAD (oder durch Schließen der Verbindung bei deaktivierter VAD) finalisiert wurde.

1.  **Minimale Segmentdauer:** Prüft, ob das Segment die in `_settings.MinSegmentDurationSec` definierte Mindestdauer erreicht. Kürzere Segmente werden verworfen, um Rauschen oder kurze Störgeräusche nicht fälschlicherweise zu verarbeiten.
2.  **Performance-Messung:** Ein `Stopwatch` misst die Gesamtdauer der Segmentverarbeitung.
3.  **Audio-Vorbereitung:** Die rohen Audio-Bytes werden in einen `MemoryStream` geschrieben und mit einem WAV-Header versehen (`PrepareAudioStreamForTranscription`, `CreateWavHeader`). Dies ist notwendig, da die Spracherkennungs-API typischerweise ein vollständiges Audioformat erwartet.
4.  **Spracherkennung (STT):**
    *   `GetTranscriptionAsync` ruft `_recognizer.RecognizeAsync(audioMemoryStream, _pipelineOptions.Language)` auf.
    *   Der erkannte Text (`prompt`) und die Dauer der Transkription werden erfasst.
    *   Ein `prompt`-Event mit dem erkannten Text wird an den Client gesendet (`SendEventAsync`).
5.  **Chat-Verarbeitung:**
    *   Abhängig von `_pipelineOptions.DisableStreamingChat`:
        *   **Streaming Chat (`HandleStreamingChatResponseAsync`):**
            *   Wird verwendet, wenn `_chatService` eine Instanz von `StreamingOpenAIChatService` ist.
            *   Ruft `streamingChatService.StreamChatAsync` auf.
            *   Während Tokens vom Chat-Service eintreffen:
                *   Werden sie aggregiert (`accumulatedTextForTts`, `fullReply`).
                *   Ein `token`-Event mit dem neuen Token wird an den Client gesendet.
                *   Wenn Progressive TTS (`!_pipelineOptions.DisableTts && !_pipelineOptions.DisableProgressiveTts`) aktiviert ist und genügend Text für ein TTS-Segment vorhanden ist, wird `_synthesizer.SynthesizeAsync` aufgerufen und das resultierende Audio-Chunk sofort an den Client gesendet (`SendAudioChunkAsync`).
        *   **Non-Streaming Chat (`HandleNonStreamingChatResponseAsync`):**
            *   Ruft `_chatService.SendChatAsync` auf, um die vollständige Antwort zu erhalten.
            *   Wenn TTS aktiviert ist (`!_pipelineOptions.DisableTts`), wird die gesamte Antwort mit `_synthesizer.SynthesizeAsync` synthetisiert und als einzelne Audio-Antwort an den Client gesendet.
6.  **Logging und finale Events:**
    *   `LogAndSendFinalEventsAsync` protokolliert die Zeiten für Transkription, LLM-Antwort und Gesamtverarbeitung.
    *   Ein `done`-Event wird an den Client gesendet, das die finale Antwort und die Performance-Metriken enthält.
    *   Wenn TTS nicht deaktiviert ist, wird ein `audioFinished`-Event gesendet, nachdem das letzte Audio-Chunk übertragen wurde.

**Hilfsmethoden:**

*   `ApplyPreAmplification`: Wendet einen Verstärkungsfaktor auf die Audio-Samples an.
*   `CalculateRms`: Berechnet den Root Mean Square eines Audio-Frames.
*   `CreateWavHeader`: Erstellt einen gültigen WAV-Header für die gegebenen Audiodaten.
*   `HandleUpdateVadSettingsAsync`, `HandleUpdatePipelineOptionsAsync`: Verarbeiten eingehende WebSocket-Textnachrichten, um `VadSettings` bzw. `PipelineOptions` zur Laufzeit zu aktualisieren. Sie parsen das JSON-Payload und wenden die neuen Werte an. `ConfigureVad()` wird nach einer `VadSettings`-Aktualisierung aufgerufen.
*   `PrepareAudioStreamForTranscription`: Konvertiert rohe Audio-Bytes in einen `MemoryStream` mit WAV-Header.
*   `GetTranscriptionAsync`: Führt die eigentliche Spracherkennung durch.
*   `SendAudioChunkAsync`: Sendet ein Audio-Chunk (typischerweise MP3) als Base64-kodierten String in einer JSON-Nachricht an den Client.
*   `SendEventAsync`: Sendet eine generische JSON-Nachricht (Event) an den Client.

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
    *   `ChatMessage.cs`, `ChatRole.cs`: Standardmodelle für Chat-Nachrichten.
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

Enthält statische Dateien für die Web-Benutzeroberfläche (`index.html`, CSS, JavaScript). Die UI ermöglicht die Interaktion mit dem Backend, sendet Audio und empfängt Ereignisse sowie synthetisierte Sprache. Sie bietet vermutlich auch Steuerelemente zur dynamischen Anpassung der `VadSettings` und `PipelineOptions`.

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