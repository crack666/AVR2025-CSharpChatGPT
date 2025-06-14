# Refactoring-Plan für Voice Activity Detection (VAD) und Progressive TTS

## 1. Zielsetzung

Verbesserung der Zuverlässigkeit und Reduzierung der Latenz bei der Erkennung des Beginns von Sprachsegmenten im C#-Projekt "Voice Activity Detection Refactoring". Optimierung der progressiven Text-to-Speech (TTS) Pipeline für eine schnelle initiale Antwort und eine nachfolgend konsistente, natürlich klingende Sprachausgabe. Inspiration und bewährte Ansätze sollen dabei vom Python-Projekt "Study Material Processor v2.1" übernommen werden, insbesondere dessen effektive Methode zur Erkennung von Sprachbeginn basierend auf Wellenform-Analyse (z.B. "Spikes"). Vereinfachung der Codepfade und Überprüfung der Notwendigkeit von Feature-Flags.

## 2. Analyse des Ist-Zustands (C#-Projekt)

*   **VAD Aktuell:** Das System verwendet 20ms Audio-Frames, EMA-Glättung des RMS-Signals, Hysterese-Schwellenwerte (`StartThreshold`, `EndThreshold`), eine Hang-over-Zeit und einen Pre-Speech-Puffer (`WebSocketAudioService.cs`).
*   **VAD Problem:** Der Anfang eines Sprachsegments wird oft zu spät oder unzuverlässig erkannt.
*   **TTS Aktuell (`WebSocketAudioService.cs` & `ProgressiveTTSSynthesizer.cs`):**
    *   `WebSocketAudioService` (`WSA`) empfängt Audio-Chunks, führt VAD durch, sendet erkannte Sprache an Whisper (`IRecognizer`).
    *   Die transkribierte Benutzereingabe geht an den `IChatService`.
    *   Wenn `StreamingOpenAIChatService` verwendet wird und `!_pipelineOptions.DisableTokenStreaming`, werden LLM-Antworten tokenweise empfangen.
    *   `WSA` nutzt `ShouldFlush` und `FlushSegmentAtSentenceBoundary` um den ankommenden Textstrom vom LLM in Chunks für TTS aufzuteilen.
    *   `ProgressiveTTSSynthesizer` (`PTTS`) implementiert `ISynthesizer` und hat eine `ChunkedSynthesisAsync`-Methode, die Text intern weiter in Sätze aufteilt und jeden Satz einzeln an die OpenAI TTS API sendet. Die Methode `SynthesizeTextChunkAsync` in `PTTS` ruft aktuell `ChunkedSynthesisAsync` auf, was zu einer doppelten/redundanten Chunking-Logik führen kann, wenn `WSA` bereits kleine Chunks liefert.
*   **TTS Problem:** Die Interaktion der Chunking-Logiken zwischen `WSA` und `PTTS` ist potenziell ineffizient. Das Ziel einer schnellen ersten Antwort gefolgt von größeren, natürlicheren Sprachsegmenten ist noch nicht optimal umgesetzt. Codepfade sind durch verschiedene Ansätze komplex geworden.
*   **Herausforderung:** Echtzeit-Analyse von kontinuierlich eintreffenden Audio-Chunks (VAD) und Text-Tokens (TTS) mit minimaler Verzögerung und hoher Audioqualität.

## 3. Erkenntnisse aus "Study Material Processor v2.1" (Python-Projekt)

Das Python-Projekt nutzt Ansätze wie "Precision Waveform Detection" und "Defensive Silence Detection", die auf direkter Analyse der Audio-Wellenform basieren, um Sprachaktivität zu erkennen. Schlüsselkonzepte:

*   **Energie-/RMS-basierte Spike-Erkennung:** Identifizierung signifikanter Anstiege im Audiosignal.
*   **Perzentil-basierte/Adaptive Schwellenwerte.**

## 4. Geplanter Refactoring-Prozess

### Teil A: VAD-Verbesserungen (Fokus: `WebSocketAudioService.cs`)

#### Schritt 4.A.1: Implementierung einer vorgelagerten "Spike-Detection" - *(Erledigt)*
*   **Berechnung der Energie/RMS pro Chunk:** Für jeden eintreffenden Audio-Chunk (z.B. 20ms) ist dessen Energie oder RMS-Wert in Echtzeit zu berechnen (bereits teilweise in `CalculateRms` vorhanden).
*   **Definition eines "Spike"-Schwellenwerts:**
    *   Einführung eines neuen Parameters, z.B. `VadSpikeThreshold` oder `VadEnergyRiseFactor`.
    *   Untersuchung, ob dieser Schwellenwert adaptiv gestaltet werden kann (basierend auf dem kurzfristigen Rauschpegel oder einem gleitenden Perzentil der Energie der letzten N Chunks, ähnlich der bestehenden `_noiseFloor`-Logik, aber aggressiver für den Start).
*   **Logik:** Wenn die Energie/RMS eines Chunks (oder eine schnelle Änderung über wenige Chunks) den `VadSpikeThreshold` überschreitet, wird dies als starker Indikator für einen potenziellen Sprachbeginn gewertet. Dieser Mechanismus soll *vor* oder parallel zur bestehenden `_vad.HasSpeech()` und `dynamicThreshold`-Prüfung agieren, um eine schnellere initiale Reaktion zu ermöglichen.

#### Schritt 4.A.2: Integration der Spike-Detection mit der bestehenden VAD-Logik - *(Erledigt)*
*   **Primärer Trigger:** Die "Spike-Detection" dient als primärer, schneller Auslöser für den Zustand "Sprache beginnt".
*   **Bestätigung und Stabilisierung:** Die bestehenden Mechanismen (`_vad.HasSpeech()`, EMA-Glättung falls vorhanden, Hysterese-Schwellenwerte) können nachgelagert verwendet werden, um den Sprachzustand zu bestätigen, zu stabilisieren und das Ende der Sprache präziser zu erkennen.
*   **Pre-Speech-Puffer:** Der `preBuffer` ist entscheidend. Sobald ein "Spike" erkannt wird, wird der Inhalt des `preBuffer` zusammen mit den nachfolgenden Chunks als Teil des beginnenden Sprachsegments betrachtet.

#### Schritt 4.A.3: Optimierung der Latenz bei VAD - *(Erledigt)*
*   Minimierung der Verzögerung zwischen tatsächlichem Sprechbeginn und dem Füllen des `segmentBuffer` für `ProcessSegmentAsync`. (Substanziell adressiert durch die verbesserte Spike-Detection und Initiierungslogik in 4.A.2. Weitere Optimierungen wären primär Parameter-Tuning unter 4.C.1).

### Teil B: Progressive TTS-Optimierung (Fokus: `WebSocketAudioService.cs` & `ProgressiveTTSSynthesizer.cs`)

#### Schritt 4.B.1: Klärung der Chunking-Verantwortlichkeiten und Implementierung einer Hybrid-Strategie - *(Erledigt)*
*   **Ziel:** Schnelle erste Audioausgabe, danach längere, natürlichere Segmente.
*   **Anpassung `ProgressiveTTSSynthesizer.SynthesizeTextChunkAsync`:** Diese Methode soll so geändert werden, dass sie den übergebenen `textChunk` *direkt* und ohne weitere interne Aufteilung synthetisiert (d.h., sie sollte intern die Logik von `SynthesizeAsync` für diesen spezifischen Chunk verwenden). - *(Erledigt)*
*   **Anpassung `WebSocketAudioService.ProcessSegmentAsync` (Streaming-Pfad):**
    1.  **Erster Chunk (für minimale Latenz):**
        *   `WSA` sammelt die ersten Tokens vom LLM (z.B. bis zu einer bestimmten kurzen Länge, ca. 50-100 Zeichen, oder bis zum ersten natürlichen Satzende, falls sehr kurz).
        *   Dieser erste, kleine Text-Chunk wird an `_synthesizer.SynthesizeTextChunkAsync` (die modifizierte Version) gesendet.
    2.  **Folgende Chunks (für natürliche Sprache):**
        *   `WSA` sammelt danach größere Textmengen vom LLM (z.B. mehrere Sätze oder bis zu einer Obergrenze von z.B. 200-300 Zeichen).
        *   Diese größeren Textblöcke werden dann an `_synthesizer.ChunkedSynthesisAsync` übergeben. `ProgressiveTTSSynthesizer` übernimmt dann mit seiner `SplitTextIntoSentenceChunks`-Logik die Aufteilung in natürlich klingende Segmente für die TTS-API.
    *   Die Logik in `ShouldFlush` und `FlushSegmentAtSentenceBoundary` in `WSA` muss entsprechend angepasst werden, um dieses zweistufige Verhalten (kleiner erster Chunk, größere Folgechunks) zu unterstützen. - *(Erledigt)*

#### Schritt 4.B.2: Vereinfachung der Codepfade und Überprüfung der Feature-Flags
*   **Hauptpfad stärken:** Der Code-Pfad für `!_pipelineOptions.DisableTokenStreaming` (also das echte End-to-End-Streaming) ist der primäre Fokus und soll maximal robust und effizient gestaltet werden.
*   **`_pipelineOptions.DisableTokenStreaming`:** Beibehalten als Fallback oder für Debugging-Zwecke. Der `else`-Block sollte eine einfache, aber funktionierende Standard-TTS-Implementierung darstellen (vermutlich Aufruf von `_synthesizer.SynthesizeAsync` mit dem gesamten Text).
*   **`_pipelineOptions.DisableVad`:** Beibehalten für Tests. Ermöglicht das komplette Deaktivieren der VAD.
*   **VAD-Komponenten-Flags (NEU):** Überprüfung und Nutzung der neuen Flags `EnableSpikeDetection` und `EnableThirdPartyVad` aus `VadSettings`, um die einzelnen Detektionsmechanismen der VAD steuern zu können.
*   **Andere Optionen:** Überprüfen, ob weitere `_pipelineOptions` die Komplexität unnötig erhöhen oder für den Hauptanwendungsfall relevant sind. Ziel ist es, Konfigurationsmöglichkeiten zu bieten, ohne den Code unübersichtlich zu machen.

#### Schritt 4.B.3: Sicherstellung der geordneten Audioausgabe - *(Überprüft und angepasst)*
*   Die bestehende Logik in `WebSocketAudioService.HandleStreamingChatResponseAsync` (umbenannt von `ProcessSegmentAsync` für den Streaming-Teil) zur Verwaltung von TTS-Tasks und der geordneten Audioausgabe (`audioChunks`, `audioChunkReady`, `audioProcessingTask` etc.) wurde überprüft. Die Variablen `ttsTaskQueue` und `audioSendSemaphore` waren nicht mehr in Gebrauch und wurden entfernt. Die Logik zur Behandlung von leeren/kurzen Chunks in `StartTtsTaskAsync` (Platzieren von `Array.Empty<byte>()` und Freigeben von `audioChunkReady`) stellt sicher, dass der `audioProcessingTask` nicht blockiert. Die Verwendung von `localChunkCounter` als Index für `audioChunks` und `nextChunkIndex` im `audioProcessingTask` gewährleistet die korrekte Reihenfolge. Die Anpassungen für `DisableProgressiveTts` und `DisableTts` sind integriert. Die Fehlerbehandlung in `StartTtsTaskAsync` (ebenfalls Freigabe von `audioChunkReady`) trägt zur Robustheit bei.

#### Schritt 4.B.4: Refactoring von `ProcessSegmentAsync` - *(Erledigt)*
*   **Ziel:** Aufteilung der `ProcessSegmentAsync`-Methode in kleinere, logisch getrennte und besser wartbare private Helper-Methoden.
*   **Vorgehen:**
    *   Identifizierung von eigenständigen Codeblöcken innerhalb von `ProcessSegmentAsync`, wie z.B.:
        *   Vorbereitung des Audio-Streams für die Transkription.
        *   Abruf der Transkription.
        *   Initialisierung des Streaming-Chat-Antwort-Prozesses (inkl. Setup der Hilfsfunktionen `ShouldFlush`, `FlushSegmentAtSentenceBoundary`, `StartTtsTaskAsync` und der Audio-Verarbeitungs-Tasks).
        *   Die Token-Verarbeitungsschleife selbst (Callback-Logik von `GenerateStreamingResponseAsync`).
        *   Verarbeitung des restlichen Textes nach der Token-Schleife.
        *   Warten auf den Abschluss aller Tasks und Senden der finalen Events.
        *   Fallback-Logik für nicht-streaming Modus.
    *   Extraktion dieser Blöcke in separate, gut benannte private Methoden innerhalb der `WebSocketAudioService`-Klasse.
    *   Sicherstellung, dass die Aufrufe dieser neuen Methoden in `ProcessSegmentAsync` den ursprünglichen Kontrollfluss korrekt abbilden und die Funktionalität erhalten bleibt.
    *   Dies soll die Lesbarkeit und Testbarkeit der einzelnen Komponenten verbessern.

### Teil C: Allgemeine Maßnahmen

#### Schritt 4.C.1: Parameter-Tuning und UI-Anpassung - *(Erledigt)*
*   Neue Parameter für VAD (`VadSpikeThreshold`) und TTS-Chunking (`TtsMinFirstChunkLength`, `TtsMaxFirstChunkLength`, `TtsSubsequentChunkLength`) wurden in `VadSettings.cs` bzw. `PipelineOptions.cs` implementiert und Standardwerte festgelegt.
*   Feature-Flags für VAD-Komponenten (`EnableSpikeDetection`, `EnableThirdPartyVad`) wurden in `VadSettings.cs` implementiert.
*   Feature-Flags für TTS-Verhalten (`DisableProgressiveTts`, `DisableTts`) wurden in `PipelineOptions.cs` implementiert.
*   Die Debug-UI (`index.html`) wurde angepasst und um neue Steuerelemente für die oben genannten VAD- und TTS/Pipeline-Parameter erweitert.
*   Die JavaScript-Logik (`optimization-manager.js`, `audio-system.js`) wurde aktualisiert, um diese neuen Einstellungen zu verwalten, an das Backend zu senden (via WebSocket-URL-Parameter bei Verbindungsaufbau und via `updateVadSettings`/`updatePipelineOptions` Nachrichten) und auf Einstellungsänderungen vom Server zu reagieren.
*   Die Einstellungen werden nun im `localStorage` persistiert.

#### Schritt 4.C.2: Erweiterung des Loggings und Debuggings - *(Erledigt)*
*   Detailliertes Logging für VAD (Energie/RMS-Werte, Spike-Erkennung, Zustandswechsel, Noise Floor Updates) und TTS (Chunk-Größen, welche Methode aufgerufen wird, Latenzen, Synthesizer-Aufrufe) wurde in `WebSocketAudioService.cs` implementiert.

#### Schritt 4.C.3: Backend WebSocket Message Handling - *(Erledigt)*
*   **Ziel:** Implementierung der Logik in `WebSocketAudioService.cs` zur korrekten Verarbeitung von Pipeline-Optionen, die über den WebSocket-URL-Query-String bei neuen Verbindungen empfangen werden.
*   **Ziel:** Implementierung von Nachrichten-Handlern in `WebSocketAudioService.cs` für `updateVadSettings` und `updatePipelineOptions`-Events, die vom Client gesendet werden, um `VadSettings` und `PipelineOptions` für die aktuelle Sitzung dynamisch zu aktualisieren.
    *   Die Felder `_settings` und `_pipelineOptions` in `WebSocketAudioService.cs` wurden nicht-readonly gemacht.
    *   Der Konstruktor wurde angepasst, um initiale Einstellungen zu übernehmen (wobei davon ausgegangen wird, dass `PipelineOptions` bereits mit Query-Parameter-Werten durch den Aufrufer vorbefüllt sind).
    *   Neue Methoden `ConfigureVad()` und `LogCurrentSettings()` wurden hinzugefügt.
    *   Die `HandleAsync`-Schleife wurde umstrukturiert, um Text- (JSON-Befehle) und Binärnachrichten (Audio) zu unterscheiden.
    *   `HandleUpdateVadSettingsAsync` und `HandleUpdatePipelineOptionsAsync` wurden implementiert, um eingehende JSON-Nachrichten zu parsen, Einstellungen zu aktualisieren, ggf. VAD neu zu konfigurieren, Änderungen zu protokollieren und Bestätigungsereignisse (`vad_settings_updated`, `pipeline_options_updated`) an den Client zu senden.
    *   Sicherstellung, dass VAD-Parameter wie `preFrames`, `startFrames`, `endFrames` in der Schleife neu bewertet werden, falls sich die Einstellungen ändern.
    *   Die Methode `SendEventAsync` wurde standardisiert, um eine Payload-Struktur `{ type, payload }` zu verwenden und das Logging verbessert.
    *   Kompilierungsfehler im Zusammenhang mit Logging-Vorlagen, Methodensignaturen (`ChatLogManager.GetMessages`, `StreamingOpenAIChatService.GenerateStreamingResponseAsync`, `IChatService.GenerateResponseAsync`, `ChatRole.Bot`, `ISynthesizer`-Methodenrückgabetypen und Stream-Konvertierung) und Syntaxfehlern wurden adressiert.
    *   Die `FlushSegmentAtSentenceBoundary`-Logik wurde überprüft und für Klarheit und Korrektheit basierend auf den neuen TTS-Chunking-Parametern angepasst.
    *   `SendAudioChunkAsync` sendet nun binäres Audio und ein separates `audio-chunk-info`-Text-Event.
    *   Die korrekte Verarbeitung von Pipeline-Optionen aus dem WebSocket-URL-Query-String wurde durch die Annahme sichergestellt, dass der Service-Konsument (z.B. der `ChatController`) die `PipelineOptions` vor der Übergabe an den `WebSocketAudioService`-Konstruktor entsprechend vorbefüllt. Die dynamische Aktualisierung über WebSocket-Nachrichten wurde implementiert und verifiziert.

#### Schritt 4.C.4: Frontend `ui-manager.js` Integration - *(Erledigt)*
*   Modifizierung des "Clear Chat"-Button-Event-Listeners in `ui-manager.js`, um `audioSystem.resetAudioPlayback()` aufzurufen.

## 5. Teststrategie

*   **VAD-Tests:**
    *   Audio mit leisem Sprachbeginn.
    *   Sprachbeginn in lauter Umgebung.
    *   Sehr kurze Äußerungen.
    *   Metriken: Time-To-First-Word (TTFW) für VAD-Erkennung, Anfangs-Clipping-Rate, False Positive Rate.
*   **TTS-Tests:**
    *   Latenz bis zum ersten hörbaren Audio-Chunk.
    *   Natürlichkeit und Fluss der gesamten Sprachausgabe.
    *   Korrekte Verarbeitung von kurzen und langen LLM-Antworten.
    *   Verhalten bei Netzwerkproblemen oder langsamen API-Antworten.
*   **End-to-End-Tests:** Nutzung der `Elefanten.wav` und Erstellung neuer Szenarien, die sowohl VAD als auch die progressive TTS-Pipeline beanspruchen.

## 6. Erwartete Ergebnisse

*   Signifikant zuverlässigere und schnellere Erkennung des Beginns von Sprachsegmenten durch die VAD.
*   Reduzierte wahrgenommene Latenz für den Benutzer durch eine optimierte progressive TTS-Pipeline (schnelle erste Audioausgabe).
*   Verbesserte Natürlichkeit der Sprachausgabe für längere Antworten.
*   Ein klarerer, robusterer und besser wartbarer Code-Pfad in `WebSocketAudioService.cs` und `ProgressiveTTSSynthesizer.cs`.

## 7. Nächste Schritte (Implementierung)

1.  **VAD:** Implementierung der Spike-Detection in `WebSocketAudioService.cs` - *(Erledigt)*
2.  **TTS:** Modifikation von `ProgressiveTTSSynthesizer.SynthesizeTextChunkAsync`. - *(Erledigt)*
3.  **TTS:** Anpassung der Chunking-Logik (`ShouldFlush`, `FlushSegmentAtSentenceBoundary` und Aufruflogik für Synthesizer-Methoden) in `WebSocketAudioService.ProcessSegmentAsync` gemäß der Hybrid-Strategie. - *(Erledigt)*
4.  Anpassung und Erweiterung der Konfigurationsparameter und UI. - *(Erledigt)*
5.  Implementierung der Backend-WebSocket-Nachrichtenverarbeitung für dynamische Einstellungsupdates und Query-Parameter. - *(Erledigt)*
6.  Frontend-Integration: "Clear Chat" Button in `ui-manager.js` anpassen. - *(Erledigt)*
7.  Durchführung umfassender Tests und Iterationen zur Parameteroptimierung.
8.  Code-Review und Refinement zur Vereinfachung und Klärung der Logik.
