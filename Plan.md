\
# Refactoring und Optimierungsplan

Dieses Dokument basiert auf den in der `README.md` (Abschnitt 4) identifizierten potenziellen Verbesserungsbereichen und dient als Plan für schrittweise Optimierungen und Refactorings der Voice Assistant Anwendung.

## 1. Klärung und Implementierung der Query-Parameter-Logik (`Program.cs`)

*   **Problem:** Die Logik zur Übernahme von `model` und `voice` Query-Parametern beim WebSocket-Aufbau in `Program.cs` ist aktuell auskommentiert.
*   **Aktion:**
    1.  **Entscheidung treffen:** Soll diese Funktionalität beibehalten und vollständig implementiert oder entfernt werden?
    2.  **Bei Implementierung:**
        *   Den Code in `Program.cs` wieder aktivieren und sicherstellen, dass die Query-Parameter korrekt ausgelesen werden.
        *   Die ausgelesenen Werte müssen an den `WebSocketAudioService` übergeben werden. Dies könnte geschehen, indem die initialen `PipelineOptions` (oder eine ähnliche Konfigurationsinstanz, die an den `WebSocketAudioService` übergeben wird) vor der Instanziierung des Services modifiziert werden.
        *   Sicherstellen, dass der `WebSocketAudioService` diese initialen Werte korrekt verwendet (z.B. für das erste Setup des Chat-Modells oder der TTS-Stimme).
        *   Dokumentation dieser Query-Parameter aktualisieren/erstellen.
    3.  **Bei Entfernung:** Den auskommentierten Codeblock entfernen, um die Codebasis zu bereinigen.
*   **Ziel:** Eindeutiges und funktionierendes Verhalten bezüglich der initialen Konfiguration über URL-Parameter.

## 2. Konsistenz der Konfigurationsübernahme zur Laufzeit

*   **Problem:** Änderungen an `PipelineOptions` (z.B. `Language`, `ChatModel`, `TtsVoice`) und `VadSettings`, die zur Laufzeit über WebSocket-Nachrichten (`updatePipelineOptions`, `updateVadSettings`) empfangen werden, müssen sich konsistent und korrekt auf alle relevanten Dienste und laufenden Prozesse im `WebSocketAudioService` auswirken.
*   **Aktion:**
    1.  **Analyse:** Überprüfen, wie `WebSocketAudioService` die `_pipelineOptions` und `_settings` (VadSettings) intern verwendet, nachdem sie durch `HandleUpdatePipelineOptionsAsync` und `HandleUpdateVadSettingsAsync` aktualisiert wurden.
    2.  **Chat-Modell und TTS-Stimme:** Besondere Aufmerksamkeit gilt der dynamischen Änderung des Chat-Modells und der TTS-Stimme. Werden neue Instanzen von `IChatService` oder `ISynthesizer` benötigt oder können die bestehenden Instanzen rekonfiguriert werden? Aktuell werden diese als Singletons in `Program.cs` registriert. Eine dynamische Änderung zur Laufzeit pro WebSocket-Sitzung erfordert eine andere Handhabung (z.B. Factory-Pattern oder Scoped Services, die pro Verbindung erstellt werden, oder Weitergabe der Optionen an die Methoden der Dienste).
    3.  **Spracheinstellung:** Sicherstellen, dass die `Language`-Einstellung in `PipelineOptions` korrekt an den `_recognizer` und ggf. andere sprachabhängige Komponenten weitergegeben wird, auch bei Änderungen zur Laufzeit.
    4.  **VAD-Parameter:** Die `ConfigureVad()`-Methode wird bereits nach Änderungen an `VadSettings` aufgerufen. Überprüfen, ob alle Parameter korrekt angewendet werden und keine laufenden VAD-Prozesse inkonsistent werden.
*   **Ziel:** Robuste und vorhersehbare Reaktion der Anwendung auf dynamische Konfigurationsänderungen während einer aktiven WebSocket-Verbindung.

## 3. Feingranulare Fehlerbehandlung und -kommunikation

*   **Problem:** Die aktuelle Fehlerbehandlung könnte detaillierter sein, um dem Client spezifischere Informationen über aufgetretene Probleme zu geben.
*   **Aktion:**
    1.  **Identifiziere kritische Punkte:** Durchgehen des `WebSocketAudioService` und anderer Kernkomponenten (`OpenAI`-Plugins) auf Stellen, an denen Fehler auftreten können (API-Aufrufe, Datenverarbeitung, WebSocket-Kommunikation).
    2.  **Spezifische Fehler-Events:** Definieren und implementieren von spezifischeren `error`-Events, die an den Client gesendet werden (z.B. `error_openai_api`, `error_tts_failed`, `error_stt_failed`, `error_invalid_audio_format`).
    3.  **Fehler-Payloads:** Anreichern der Fehler-Events mit nützlichen Informationen (Fehlercode, detailliertere Meldung).
    4.  **Serverseitiges Logging:** Sicherstellen, dass alle relevanten Fehler auch serverseitig detailliert geloggt werden.
    5.  **Client-Handling:** (Optional, für das Web-Frontend) Verbesserung der Darstellung dieser spezifischen Fehler im UI.
*   **Ziel:** Verbesserte Diagnosemöglichkeiten für Entwickler und transparentere Fehlermeldungen für den Client.

## 4. Ressourcenmanagement (`IDisposable`)

*   **Problem:** Sicherstellen, dass alle `IDisposable`-Ressourcen korrekt freigegeben werden, um Speicherlecks oder andere Probleme bei langlebigen Verbindungen zu vermeiden.
*   **Aktion:**
    1.  **Code-Review:** Überprüfen aller Klassen, die `IDisposable` implementieren oder `IDisposable`-Objekte verwenden (z.B. Streams, `HttpClient`, `WebSocket`, `WebRtcVad`).
    2.  **`using`-Statements:** Wo immer möglich, `using`-Statements oder `try-finally`-Blöcke verwenden, um die Freigabe sicherzustellen.
    3.  **Lebenszyklen:** Besondere Aufmerksamkeit auf Objekte, die an den Lebenszyklus einer WebSocket-Verbindung gebunden sind (z.B. im `WebSocketAudioService`). Sicherstellen, dass deren `Dispose`-Methoden aufgerufen werden, wenn die Verbindung schließt oder der Service selbst disposed wird (falls relevant).
    4.  **`WebRtcVad`:** Überprüfen, ob `_vad.Dispose()` korrekt aufgerufen wird, wenn `WebSocketAudioService` nicht mehr benötigt wird oder die VAD-Instanz neu konfiguriert/ersetzt wird.
*   **Ziel:** Stabile und ressourcenschonende Anwendung, auch bei vielen gleichzeitigen oder langlebigen Verbindungen.

## 5. Erweiterung der Testabdeckung (`VoiceAssistant.Tests/`)

*   **Problem:** Die bestehenden Tests decken möglicherweise nicht alle Szenarien ab, insbesondere dynamische Konfigurationsänderungen und komplexe VAD-Interaktionen.
*   **Aktion:**
    1.  **Analyse der Testabdeckung:** Identifizieren von Bereichen mit geringer Testabdeckung.
    2.  **Neue Tests schreiben für:**
        *   Dynamische Änderungen der `PipelineOptions` via WebSocket und deren Auswirkungen.
        *   Dynamische Änderungen der `VadSettings` via WebSocket und deren Auswirkungen auf die Segmentierung.
        *   Verschiedene VAD-Szenarien mit unterschiedlichen Rauschpegeln, Sprechmustern und Einstellungen (Hysterese, Hangover etc.).
        *   Grenzfälle und Fehlerbedingungen (z.B. ungültige Eingaben, API-Fehler).
        *   Korrekte Funktionsweise der progressiven TTS und des Token-Streamings.
    3.  **Integrationstests:** Erweiterung der Integrationstests, die den gesamten Flow von Audioeingang bis Sprachausgabe über WebSockets testen.
*   **Ziel:** Höhere Codequalität und frühzeitiges Erkennen von Regressionen.

## 6. Dokumentation der WebSocket-API

*   **Problem:** Eine explizite Dokumentation der WebSocket-Nachrichtenformate fehlt.
*   **Aktion:**
    1.  **Erstellen eines Dokuments** (z.B. `WebSocketAPI.md` oder als Teil der `README.md`), das die JSON-Struktur aller vom Client zum Server und vom Server zum Client gesendeten Nachrichten detailliert beschreibt.
    2.  **Beispiele:** Für jede Nachrichtenart ein Beispiel-Payload angeben.
    3.  **Typen und Parameter:** Bedeutung aller Felder und möglicher Werte erläutern.
    4.  **Sequenzdiagramme:** (Optional) Visualisierung typischer Interaktionsflüsse.
*   **Ziel:** Erleichterung der Entwicklung von alternativen Clients und besseres Verständnis der Server-Client-Kommunikation.

## 7. VAD Noise Floor Kalibrierung und Anpassung

*   **Problem:** Die initiale Rauschpegelmessung (`MeasureInitialNoiseFloor`) ist statisch. Eine dynamischere Anpassung könnte die VAD-Robustheit verbessern.
*   **Aktion:**
    1.  **Optionen evaluieren:**
        *   **Periodische Neukalibrierung:** Automatische Neumessung des Rauschpegels in längeren Stillephasen.
        *   **Manuelle Auslösung:** Implementierung eines Mechanismus (z.B. WebSocket-Nachricht vom Client), um die Rauschpegelkalibrierung neu zu starten. Das Web-Frontend hat bereits einen Button (`calibrateVadBtn`) und ein File-Input (`vadSampleInput`), deren Backend-Logik implementiert werden könnte.
        *   **Kontinuierliche Anpassung:** Fortlaufende, langsame Anpassung des Rauschpegels basierend auf dem niedrigsten erkannten Pegel über einen längeren Zeitraum (ähnlich der bestehenden `SilenceAdaptationTimeSec`-Logik, aber ggf. verfeinert).
    2.  **Implementierung:** Auswahl und Implementierung der bevorzugten Methode.
    3.  **Testen:** Überprüfen der Auswirkungen auf die VAD-Performance in unterschiedlichen Umgebungen.
*   **Ziel:** Robustere VAD, die sich besser an variable Umgebungsgeräusche anpassen kann.

## 8. Modularität der VAD-Logik in `WebSocketAudioService.HandleAsync`

*   **Problem:** Die VAD-bezogene Logik innerhalb der `HandleAsync`-Methode ist umfangreich und könnte die Lesbarkeit und Wartbarkeit beeinträchtigen.
*   **Aktion:**
    1.  **Identifiziere Sub-Logiken:** Aufteilen der VAD-Verarbeitung in kleinere, logisch zusammengehörige Blöcke (z.B. Frame-Vorverarbeitung, RMS-Berechnung, Spike Detection Logik, Third-Party VAD Logik, Zustandsübergänge für `inSpeech`, Puffer-Management).
    2.  **Auslagern in private Methoden:** Refaktorieren dieser Blöcke in gut benannte private Methoden innerhalb des `WebSocketAudioService`.
    3.  **Eigene VAD-Klasse (Optional):** Bei sehr hoher Komplexität könnte die gesamte VAD-Zustandslogik und -verarbeitung in eine separate Klasse ausgelagert werden, die von `WebSocketAudioService` verwendet wird. Dies würde die Trennung der Verantwortlichkeiten weiter verbessern.
*   **Ziel:** Verbesserte Lesbarkeit, Wartbarkeit und Testbarkeit der VAD-Komponente.

## 9. Überprüfung und ggf. Refactoring der Singleton-Dienste für dynamische Konfiguration

*   **Problem:** Dienste wie `IChatService` und `ISynthesizer` werden als Singletons registriert, was eine einfache sitzungsspezifische Konfiguration (z.B. unterschiedliche Modelle/Stimmen pro WebSocket-Verbindung basierend auf Query-Parametern oder Laufzeit-Updates) erschwert.
*   **Aktion:**
    1.  **Analyse:** Wie wirken sich die aktuellen Singleton-Registrierungen auf die gewünschte dynamische Konfigurierbarkeit pro Sitzung aus?
    2.  **Lösungsansätze:**
        *   **Scoped Services:** Wenn ASP.NET Core Identity oder ein ähnlicher Mechanismus zur Sitzungsverwaltung verwendet würde, könnten Dienste pro Request/Verbindung (`AddScoped`) erstellt werden. Für WebSockets ist dies nicht direkt anwendbar, aber das Prinzip ist relevant.
        *   **Factory Pattern:** Eine Factory-Klasse könnte dafür verantwortlich sein, Instanzen von `IChatService` oder `ISynthesizer` mit spezifischen Konfigurationen (Modell, Stimme) zu erstellen, wenn sie benötigt werden (z.B. zu Beginn einer WebSocket-Verbindung oder wenn sich die Konfiguration ändert). Der `WebSocketAudioService` würde dann die Factory verwenden.
        *   **Parameterübergabe an Methoden:** Statt die Konfiguration im Dienst selbst zu halten, könnten relevante Parameter (wie Modellname, Stimmenname) direkt an die Methoden (`SendChatAsync`, `SynthesizeAsync`) übergeben werden. Die Singleton-Dienste wären dann zustandsloser und würden die Konfiguration pro Aufruf erhalten. Dies ist oft der sauberste Ansatz für Singletons.
    3.  **Implementierung:** Auswahl und Umsetzung des passendsten Ansatzes.
*   **Ziel:** Flexible und korrekte Konfiguration der Dienste pro Client-Sitzung, insbesondere bei dynamischen Änderungen.

Dieser Plan sollte als lebendiges Dokument betrachtet und bei Bedarf angepasst werden.
