<!-- TRUE_E2E_STREAMING.md: Plan für echte End-to-End-Streaming-Pipeline (Chat + TTS) -->
# True E2E Streaming Plan

Ziel: Chat-Token-Streaming und TTS-Streaming vollständig parallelisieren, so dass bereits während der fortlaufenden Text-Generierung Audio-Chunks abgespielt werden.

## 1. Neue TTS-Schnittstelle für Teiltexte
- [ ] In `VoiceAssistant.Core.Interfaces.ISynthesizer` Methode hinzufügen:
  ```csharp
  Task<byte[]> SynthesizeTextChunkAsync(string textChunk, string voice);
  ```
  Diese erzeugt Audio-MP3-Bytes für den jeweiligen Text-Ausschnitt.

## 2. Implementierung in `ProgressiveTTSSynthesizer`
- [ ] `ProgressiveTTSSynthesizer` umsetzen:
  ```csharp
  public Task<byte[]> SynthesizeTextChunkAsync(string textChunk, string voice)
      => SynthesizeAsync(textChunk, voice);
  ```
  (Einfaches Single-Shot-TTS für den Teiltext)

## 3. Prozess-Logik in `WebSocketAudioService` umbauen
- [ ] In `ProcessSegmentAsync`:
  1. Starte Chat-Streaming mit Callback:
     ```csharp
     await streaming.GenerateStreamingResponseAsync(messages, async token => {
         // 1.1 Token → Frontend
         await SendEventAsync(ws, "token", new { token });
         // 1.2 Akkumuliere in StringBuilder
         sb.Append(token);
         // 1.3 Bei Schwelle erreicht (z.B. 50 Zeichen oder Satzende):
         if (ShouldFlush(sb)) {
             var chunkText = FlushSegment(sb);
             var audioBytes = await _synthesizer.SynthesizeTextChunkAsync(chunkText, TtsVoice);
             await SendEventAsync(ws, "audio-chunk", new { chunk = Convert.ToBase64String(audioBytes) });
         }
     });
     ```
  2. Nach Ende (`[DONE]`), restliche Texte flushen und letzte Audio-Chunks senden.
  3. Abschließend `await SendEventAsync(ws, "audio-done", null)` und `"done"`.

## 4. Entfernen alter TTS-Schritte
- [ ] Alte sequentielle TTS-Logik (nach vollem Reply) aus `ProcessSegmentAsync` entfernen.

## 5. Synchronisation und Restriangulierung
- [ ] Sicherstellen, dass alle `await`-Aufrufe sequentiell ablaufen, um Überlast zu vermeiden:
  - `GenerateStreamingResponseAsync` mit Callback unterstützt.
  - Im Callback `SynthesizeTextChunkAsync` erst auf vorhergehenden Call warten.

## 6. Client-Integration
- [ ] CS-Client: Verbleibende `audio-chunk`-Events korrekt handhaben.

## 7. Tests und Metriken
- [ ] Manuelles Testen mit WebSocket-Streaming: volle Pipeline-Latenz messen.
- [ ] Vergleichen mit Legacy HTTP-Modus.

---
*Dieser Plan ist experimentell und dient der Evaluierung echter Streaming-Performance.*