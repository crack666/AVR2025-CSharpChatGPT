In der aktuellen Implementierung von ProcessSegmentAsync laufen Chat-Streaming und TTS-Streaming nacheinander ab:

    1. Wir rufen
       reply = await streaming.GenerateStreamingResponseAsync(…)
       auf und warten damit, bis der gesamte Chat-Text am Client angekommen ist (auch wenn die Tokens schon live reinflattern).
    2. Erst danach starten wir ProgressiveTTSSynthesizer.ChunkedSynthesisAsync(reply,…) und schicken Audio-Chunks erst, wenn das komplette reply verfügbar ist.

Das heißt: Wir haben zwar Token-Streaming für den Text, aber unser TTS-Chunking erfolgt de facto immer erst nach dem vollen Chat-Reply, und nicht parallel oder „on‐the‐fly“.
Damit verpufft der eigentliche Vorteil von ProgressiveTTS, weil wir das gesamte Reply erst fertigbauen, bevor wir auch nur ein einziges Audio-Chunk abholen.

Um echten End‐to‐End-Streaming-Durchsatz zu erreichen, müssten wir:

• Token-Callback mit TTS koppeln
  In GenerateStreamingResponseAsync einen weiteren Callback übergeben, der bei jedem Token (oder sinnvoller Text-Segment-Schwelle) nicht nur

      await SendEventAsync(webSocket, "token", new { token });

  sondern auch direkt

      await _synthesizer.ChunkedSynthesisAsync(/*den bisher gestreamten Text*/,…)

  aufruft, um parallel zum Anzeigetext auch schon erste Audio-Chunks zu generieren und abzuschicken.

• Text-Segmentierung on-the-fly
  Wir müssten den Reply-String inkrementell im Token-Callback zusammensetzen und nach jeder, sagen wir, 50 Zeichen, ein Teil-TTS anstoßen. Das würde allerdings mehrere
parallele HTTP-Requests ins TTS auslösen und erfordert eine deutlich robustere Chunk-Steuerung, damit wir nicht die Latenz gleich wieder verschlechtern.

• Asynchrone Steuerung vereinfachen
  Weil wir jetzt zwei Streams (Token & Audio) gleichzeitig abarbeiten, müssen wir aufpassen, dass wir nicht in Deadlocks geraten oder Puffer übersprudeln. Eine Alternative
wäre, die Streaming-TTS-API direkt im Token-SSE-Loop zu unterstützen (statt separate ChunkedSynthesisAsync-Methode).

Fazit: Aktuell sind wir nur halb im Streaming-Modus (Text live, Audio nachträglich). Für echtes Parallel-Streaming müssten wir die Chat-Streaming-API um eine
Audio-Callback-Integration erweitern und ProgressiveTTS so umbauen, dass es inkrementelle Eingaben akzeptiert. Das ist ein nicht-trivialer Refactor, und wir sollten das als
separaten Task (z.B. „True E2E Streaming“) umsetzen und sorgfältig testen.