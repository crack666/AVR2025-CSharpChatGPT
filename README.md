# Voice Activity Detection Refactoring

In diesem Projekt haben wir die Voice Activity Detection (VAD) schrittweise verbessert,
um trotz 20 ms‑Frames robustere und zusammenhängende Sprachsegmente zu erhalten.

## Übersicht

- **EMA-Glättung:** RMS-Signal wird über ein kurzes Fenster (Standard: 100 ms) geglättet, um Jitter zu reduzieren.
- **Hysterese-Thresholds:** Separate Schwellen (`StartThreshold`, `EndThreshold`) verhindern Oszillationen zwischen Sprach‑ und Stille-Erkennung.
- **Hang-over:** Kurze Nachlaufzeit (Standard: 200 ms) nach Sprachende unterdrückt Segmentbruch bei kleinen Pausen.
- **Pre-Speech-Puffer:** Anfangsverluste werden durch Vorpuffer (Standard: 200 ms) ausgeglichen.
- **UI-Sliders:** Alle neuen Parameter sind über die Debug-UI justierbar und werden live ins Backend übertragen.

## Ergebnisse

- Deutlich stabilere Sprachsegmente, weniger fragmentierte Erkennung bei längeren Äußerungen.
- Geringe Zusatzlatenz (~300 ms) als akzeptabler Kompromiss für mehr Robustheit.
- Echtzeit-Anpassung der VAD-Parameter direkt in der WebUI möglich.

## Nächste Schritte

- Weitere Feinjustierung der Default-Werte für unterschiedliche Umgebungsbedingungen.
- (Optional) Automatische Anpassung der Schwellen basierend auf Umgebungsgeräuschen und Nutzungsprofil.

## Logging-Verbesserungen

- Pro-Token-Debug-Logs wurden auf Trace-Level verlegt und Standard-Debug-Logging deaktiviert, um Log-Spam zu vermeiden.
- Frontend-Ereignis-Timeline zeigt nur noch `prompt`, `error` und `done`, Token- und Audio-Events werden nicht mehr geloggt.

## Automatischer Test mit Elefanten.wav

- Neue Unit-Test `ElefantenAudio_ReturnsExpectedPrompt` verwendet `Elefanten.wav` im Ordner `TestAudioSamples`, um sicherzustellen, dass die Frage "Wie groß werden Elefanten?" korrekt erkannt wird.
- Testet die `/api/processAudio`-Endpoint direkt und validiert die erkannte Prompt.