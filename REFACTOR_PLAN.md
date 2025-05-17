<!--
  Refactoring Plan für Voice Chat Assistant
  Jede Phase enthält eine Statusmarkierung.
-->
# Refactoring Plan

Dieser Plan dokumentiert die geplanten Phasen zur Optimierung der Latenz und Streaming-Funktionalität.

**Hinweis:** Plattformunabhängigkeit hat höchste Priorität. Rein browser-spezifische Streaming-Techniken (z.B. Streaming-ASR/-TTS) sind optional und nur als Plugin-Lösung vorgesehen. Bevorzuge batch-basierte UnityWebRequest-Implementierungen.

**Kompatibilität:**
- Alle .NET-Bibliotheken zielen auf **.NET Standard 2.0** mit **C# 8.0** ab, um maximale Kompatibilität mit Unity (aktuell unterstützt bis C# 8.0) zu gewährleisten.
- **Keine** Nutzung von C# 9+ Features (z.B. `record`, Ziel-typisierte `new()`), da Unity zum Zeitpunkt der Entwicklung noch nicht alle neuen Sprachebenen unterstützt.
- Keine Voraussetzung auf .NET 5/6/7/8/9: Ziel bleibt .NET Standard 2.0.

| Phase | Beschreibung | Status |
|-------|--------------|:-----:|
| 1. Low-Latency Voice-Activity Detection    | FFT-Size von 2048 → 1024 und Poll-Intervall von 100 ms → 50 ms | ✅ Abgeschlossen |
| 2. Streaming-Chat-Antworten                | `stream: true` bei /chat/completions, Token-Streaming im Client      | ✅ Abgeschlossen |
| 3. Persistenter HTTP/2-Client (.NET)       | Gemeinsamen HttpClient mit HTTP/2, Keep-Alive global wiederverwenden | ✅ Abgeschlossen |
| 4. Streaming-ASR (optional, browser-basiert) | Nutzung Web Speech API oder WebSocket-basierte Whisper-Streaming    | ✅ Abgeschlossen |
| 5. Streaming-TTS (optional, browser-basiert) | Chunked-Audio via MediaSource Extensions bzw. Streaming-TTS         | ⬜ Nicht gestartet |

---

*Fortschritt nach Phase 1:*
Phase 1 wurde implementiert: die Voice-Activity-Erkennung wurde beschleunigt.

*Fortschritt nach Phase 2:*
Phase 2 wurde implementiert: Streaming-Chat-Antworten via SSE wurden hinzugefügt und der Client verarbeitet Streaming-Events.
---
## Erweiterter Refactoring Plan für Unity/VR Chat-Anwendung

Dieser Refactoring-Plan enthält genaue Beschreibungen und Anweisungen, um die Arbeit jederzeit leicht wieder aufzunehmen. Jeder Schritt ist mit einer Fortschritts-Markierung versehen.

### Schritt 1: Architektur-Grundlage (1/9)
Fortschritt: [x] (Projektstruktur erstellt: Core, Plugins.OpenAI, UnityIntegration)
Beschreibung:
- Trennt klare Domänen-Module:
  - ChatManager (Steuerung des Dialog-Flows & Kontext)
  - ASR-Abstraction (Speech-to-Text)
  - TTS-Abstraction (Text-to-Speech & Audio-Playback)
  - UI/VR-Layer (Buttons, Anzeige, Cancel-Controls)
- Definiere Interfaces (IRecognizer, ISynthesizer), um browser- oder streaming-spezifische Implementierungen optional zu halten.
- Definiere modulare Projektstruktur und Assemblies, damit der Code standalone testbar und Unity-integrierbar ist:
  - Core: .NET Standard 2.0 Class Library für Kern-Logik (unity-unabhängig)
  - Plugins: Separate Class Libraries für ASR- und TTS-Implementierungen
  - UnityIntegration: Unity Assembly (mit .asmdef), die Core und Plugins referenziert
  - Tests: xUnit/NUnit Testprojekt für Unit- und Integrationstests
  - Optional: ConsoleApp oder .NET Core CLI Tool für standalone Konsolen-Tests

### Schritt 2: Plattform-unabhängiges Audio-Playback (2/9)
Fortschritt: [x] (VoicePlaybackManager und UnityIntegration-Assembly erstellt)
Beschreibung:
- Erstelle VoicePlaybackManager mit einer zentralen Unity AudioSource.
- Play(Clip): stoppt laufende Wiedergabe, setzt neuen Clip, startet Wiedergabe.
- Stop(): exposed für Cancel-Button.
- UI: Jeder Antwort-Blase einen Stop-Button zuordnen.

### Schritt 3: Chat-Log und Reihenfolgesicherung (3/9)
Fortschritt: [x] (ChatLogManager, ChatMessage/ChatRole, Unity ChatManager & UI-Subscription implementiert)
Beschreibung:
- Implementiere ChatLogManager mit FIFO-Liste und Timestamps.
- Enqueue neuer Messages, Event/Callback an UI.
- Verwende thread-sichere Queue und Unity Main-Thread Dispatcher.

### Schritt 4: Kontext-Persistenz für das LLM (4/9)
Fortschritt: [x] (IChatService und OpenAIChatService implementiert, ChatLogManager stellt komplette Historie zur Verfügung)
Beschreibung:
- Halte ChatContext (Liste von Message-Objekten) im ChatManager (Unity) bzw. ChatLogManager (Core).
- OpenAI-Chat-API ist stateless: es gibt keine serverseitige Session-ID. Jeder Request muss die Historie explizit in `messages[]` enthalten (vollständig oder per Sliding-Window). ggf. Zusammenfassungen alter Nachrichten verwenden.
- In der WebAPI (Program.cs) werden ChatLogManager, IChatService und IRecognizer über DI eingebunden und in den Endpoints verwendet, um:
  1. Speech-to-Text via IRecognizer
  2. Hinzufügen von User-Input zur ChatLogManager-Historie
  3. Generierung der Bot-Antwort via IChatService mit vollständigem Verlauf
  4. Speicherung der Bot-Antwort in ChatLogManager

### Schritt 5: ASR / TTS: Streaming vs. Batch (5/9)
Fortschritt: [x] (IRecognizer-/ISynthesizer-Plugins erstellt und in WebAPI integriert)
Beschreibung:
- Implementiere beide Modi als Plugins (Batch bevorzugt; browser-spezifische Streaming-Implementierungen optional):
  - Batch-API (UnityWebRequest) als Standardmodus
  - Optional: Streaming via Websocket oder Web Speech API (nur wenn VR-Target zuverlässig unterstützt)

### Schritt 6: Threading & Synchronisation (6/9)
Fortschritt: [ ]
Beschreibung:
- Unity-API-Aufrufe nur im Main-Thread.
- Netzwerk/ASR/TTS-Download async/await oder Job System.
- Ergebnisse via MainThreadDispatcher oder Coroutine zurückmelden.

### Schritt 7: UI/UX in VR (7/9)
Fortschritt: [ ]
Beschreibung:
- VR-Buttons für Stop/Cancel pro Antwort-Blase.
- Optional globaler Stop-Button und Fortschrittsbalken für aktuelle Wiedergabe.

### Schritt 8: Migrations- und Roll-out-Plan (8/9)
Fortschritt: [ ]
Beschreibung:
1. Interfaces & Dummy-Implementierungen (Echo-TTS, Local-ASR)
2. VoicePlaybackManager extrahieren und alte Logik aufrufen.
3. Kontextsicherung im ChatManager – History-Injection testen.
4. UI-Cancel-Buttons hinzufügen.
5. Produktive TTS/ASR-Plugins integrieren (Batch zuerst).
6. End-to-End Tests im Unity-Editor + VR-Device.

### Schritt 9: Tests & Qualitätssicherung (9/9)
Fortschritt: [ ]
Beschreibung:
- Unit-Tests für ChatLogManager (Reihenfolge, Enqueue/Dequeue).
- Integrationstests: Play/Stop-Sequenzen im VoicePlaybackManager, Kontext-Injection im LLM-Service (Mock).
- Manuelle VR-Durchläufe auf Ziel-Device.

---

## Zusätzliche Latenzoptimierungen für VR/Meta Quest 3

Diese erweiterten Optimierungsmaßnahmen zielen speziell auf die Reduzierung der Latenz in VR-Umgebungen, insbesondere für die Meta Quest 3. Diese Optimierungen sollen in die bestehenden Schritte integriert werden.

### Schritt 10: Unity-Audio-Optimierungen (1/5)
Fortschritt: [ ]
Beschreibung:
- Optimiere DSP-Buffer-Größe (auf 128-256 Samples reduzieren)
- Reduziere Sampling-Rate für Sprachaudio auf 22.05kHz
- Implementiere "Decompress On Load" für kurze Voice-Clips
- Erweitere VoicePlaybackManager für Queue-basierte Audio-Verarbeitung:
  ```csharp
  public void EnqueueClip(AudioClip clip)
  {
      _clipQueue.Enqueue(clip);
      if (!_audioSource.isPlaying)
          PlayNextInQueue();
  }
  ```

### Schritt 11: Echtes Token-Streaming im Chat Service (2/5)
Fortschritt: [x] (StreamingOpenAIChatService implementiert und API-Endpoints angepasst)
Beschreibung:
- Erweitere OpenAIChatService für echtes Token-Streaming mit Callback-Funktion:
  ```csharp
  public async Task<string> GenerateStreamingResponseAsync(
      IEnumerable<ChatMessage> chatHistory, 
      Action<string> onTokenReceived)
  {
      // Stream: true aktivieren und SSE-Response verarbeiten
      // Token-weise Callbacks für UI-Updates
  }
  ```
- Implementiere Streaming-to-UI-Updates via SSE Events (event: token)
- Aktiviere inkrementelles Rendering der Chat-Antworten mit requestAnimationFrame()

### Schritt 12: Progressive TTS-Synthese (3/5)
Fortschritt: [ ]
Beschreibung:
- Implementiere Chunk-basierte TTS-Verarbeitung für früheres Feedback:
  ```csharp
  public async Task ChunkedSynthesisAsync(
      string text, 
      string voice,
      Action<AudioClip> onChunkReady)
  {
      // Text in natürliche Chunks aufteilen (Sätze)
      // Chunk für Chunk synthetisieren und sofort abspielen
  }
  ```
- Intelligente Satz-/Phrase-Trennung für natürliche Chunks
- Audio-Crossfading zwischen Chunks für nahtlose Übergänge

### Schritt 13: Multithreading und Parallelverarbeitung (4/5)
Fortschritt: [ ]
Beschreibung:
- State-Machine für ASR/LLM/TTS-Pipeline mit Zwischenfeedback
- Erweiterte Thread-Synchronisation zwischen Worker-Tasks und Unity-Main-Thread:
  ```csharp
  // Beispiel für MainThreadDispatcher (Unity-kompatibel)
  public static void Enqueue(Action action)
  {
      // Aktion für Ausführung im nächsten Update-Zyklus einreihen
  }
  ```
- Optimierte CancellationToken-Unterstützung für abbrechbare Operationen

### Schritt 14: Wahrnehmungsoptimierung (5/5)
Fortschritt: [ ]
Beschreibung:
- Multimodale Feedback-Mechanismen für gefühlte Latenzreduktion:
  ```csharp
  public void ShowProcessingIndicator(bool isProcessing)
  {
      // Visuelles Feedback
      thinkingIndicator.SetActive(isProcessing);
      // Audio-Feedback
      if (isProcessing)
          playbackManager.Play(processingSound);
  }
  ```
- Fülllaute und non-verbale Turn-Taking-Signale
- Optimierte Animation von Chat-Bubbles
- Visuelles Token-Streaming mit Typewriter-Effekt