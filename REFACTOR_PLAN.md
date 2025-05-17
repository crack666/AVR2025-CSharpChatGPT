<!--
  Refactoring Plan für Voice Chat Assistant
  Jede Phase enthält eine Statusmarkierung.
-->
# Refactoring Plan

Dieser Plan dokumentiert die geplanten Phasen zur Optimierung der Latenz und Streaming-Funktionalität.

| Phase | Beschreibung | Status |
|-------|--------------|:-----:|
| 1. Low-Latency Voice-Activity Detection    | FFT-Size von 2048 → 1024 und Poll-Intervall von 100 ms → 50 ms | ✅ Abgeschlossen |
| 2. Streaming-Chat-Antworten                | `stream: true` bei /chat/completions, Token-Streaming im Client      | ✅ Abgeschlossen |
| 3. Persistenter HTTP/2-Client (.NET)       | Gemeinsamen HttpClient mit HTTP/2, Keep-Alive global wiederverwenden | ✅ Abgeschlossen |
| 4. Streaming-ASR                           | Nutzung Web Speech API oder WebSocket-basierte Whisper-Streaming    | ✅ Abgeschlossen |
| 5. Streaming-TTS                           | Chunked-Audio via MediaSource Extensions bzw. Streaming-TTS         | ⬜ Nicht gestartet |

---

*Fortschritt nach Phase 1:*
Phase 1 wurde implementiert: die Voice-Activity-Erkennung wurde beschleunigt.

*Fortschritt nach Phase 2:*
Phase 2 wurde implementiert: Streaming-Chat-Antworten via SSE wurden hinzugefügt und der Client verarbeitet Streaming-Events.