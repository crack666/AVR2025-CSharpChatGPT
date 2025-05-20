<!-- optimization-refactoring.md: Plan zur Bereinigung und Verriegelung der Optimierungsoptionen -->
# Pipeline-Optimierungen Refactoring Plan

Dieser Plan stellt sicher, dass jede im Frontend angebotene Optimierungsoption:
1. Eine echte Code-Stelle hat, die sie an- oder abschaltet.
2. Sowohl im WebSocket- als auch im HTTP-Post-Pfad wirksam ist.
3. Nicht deaktivierbare Optionen entweder zusammengeführt oder ausgegraut werden.

## 1. Audit aller UI-Toggles
- [ ] Liste aller Checkboxen/Optionen im Optimierungs-Panel erfassen:
  * Progressive TTS
  * Token-Streaming
  * Chunk-basierte Audioverarbeitung
  * VAD deaktivieren
  * Legacy HTTP-Modus
  * (ggf. weitere Optionen: Smart Chunk Splitting, Early Audio Processing, Cached AudioContext)
  * TTS Chunk-Größe Slider

## 2. Mapping zu Backend-Flags
- [ ] Für jede UI-Option prüfen:
  - Welche PipelineOption (in `PipelineOptions`) existiert oder benötigt wird.
  - Wo im Code (Program.cs, WebSocketAudioService, AudioController/SpeechController) diese Option gelesen wird.
  - Ob der HTTP-Post-Pfad die Option ebenfalls respektiert (ggf. implementieren).

## 3. Entfernen nicht wirksamer Optionen
- [ ] UI-Optionen, die keine echte Implementierung haben oder den Fluss nicht ändern, ausgrauen oder entfernen.

## 4. Einheitliche Pipeline-Steuerung
- [ ] Füge in `PipelineOptions` alle relevanten Flags ein (bereits: UseLegacyHttp, DisableVad, DisableTokenStreaming, DisableProgressiveTts).
- [ ] Komplettiere `Program.cs` + Controller, um diese Flags für beide Pfade (WS & HTTP) zu lesen.

## 5. WebSocket-Pfad anpassen
- [ ] Ersetze bedingte Logik in `WebSocketAudioService.HandleAsync`:
  * `DisableVad` → keine VAD-Logik, sofort am Close `ProcessSegmentAsync` über gesamten Stream.
  * `DisableTokenStreaming` → keine per-Token-Events.
  * `DisableProgressiveTts` → keine Chunked TTS.

## 6. HTTP-Post-Pfad anpassen
- [ ] Stelle sicher, dass derselbe `PipelineOptions`-Flagsatz im HTTP-Controller/
       im Legacy-Client-Pfad ausgewertet wird:
  * LegacyHTTP vs. WS für Aufnahme-Ende.
  * DisableVad (nur relevante, ggf. remove for HTTP).
  * DisableTokenStreaming (HTTP ist non-streaming by design).
  * DisableProgressiveTts → fw. zu HTTP-Synthesizer (ggf. implementieren).

## 7. UI-Update
- [ ] Halte `optimization-manager.js` so minimal wie möglich:
  * Nur Checkboxen für Flags, die tatsächlich existieren.
  * Kein `useEarlyAudioProcessing`, `useCachedAudioContext` oder `useSmartChunkSplitting`, wenn nicht wirksam abschaltbar.

## 8. E2E-Test und Validierung
- [ ] Manuelles und automatisiertes Testen aller Modi (kombiniert):
  * WS+alle Flags an/aus
  * HTTP+alle Flags an/aus
  * Latenz, Vollständigkeit der Transkription, UX-Feedback

---
*Stand: Audit und Plan erstellt. Nächste Schritte: Mapping und Implementierung.*