# VAD Refactoring Plan

Dieser Plan beschreibt die schrittweise Verbesserung unserer Voice Activity Detection (VAD),
um die Latenz niedrig zu halten (20 ms‑Frames), aber gleichzeitig robustere Sprachsegmente zu erzeugen.

## Zusammenfassung

- **Frame-Größe:** 20 ms (mono, 16 kHz, 16 Bit)
- **Problem:** Kurze Frames führen bei leiseren Passagen und kleinen Pausen zu fragmentierten Segmenten.
- **Ziel:** RMS‑Glättung (EMA), Hysterese‑Thresholds, Hang‑over und Pre‑Speech‑Puffer kombinieren.

## Schritt-für-Schritt Maßnahmen

- [x] **1. EMA-Glättung** (Moving Average)
- [x] **2. Hysterese-Schwellen** (Start- vs. End-Threshold)
- [x] **3. Hang-over**
- [x] **4. UI-Sliders** für neue Parameter

## Details

### 1) EMA-Glättung

Wir glätten den RMS-Wert über ein kurzes Fenster (z. B. 100 ms) mittels Exponential Moving Average.
- Neue Property `RmsSmoothingWindowSec` in `VadSettings` (Standard: 0.1)
- In `WebSocketAudioService`: Felder `_emaAlpha` und `_emaRms` hinzufügen und statt rohem `rms` glatten Wert verwenden.

### 2) Hysterese-Thresholds

Ein einziger Threshold führt zu Oszillationen. Wir verwenden getrennte Schwellen:
- `StartThreshold` (z. B. 0.008) für Sprachbeginn
- `EndThreshold` (z. B. 0.005)   für Sprachende
- In der VAD-Logik je nach Zustand (`inSpeech`) den passenden Threshold heranziehen.

### 3) Hang-over

Nach dem letzten Sprach-Frame warten wir noch eine kurze Zeit (`HangoverDurationSec`, z. B. 0.2 s),
bevor das Segment endgültig geschlossen wird.

### 4) UI-Sliders

Vier neue Slider in der Debug-UI (HTML & `audio-system.js`):
- StartThreshold
- EndThreshold
- RmsSmoothingWindowSec
- HangoverDurationSec

---

*Die Umsetzung erfolgt schrittweise – hier werden die erledigten Schritte jeweils abgehakt.*