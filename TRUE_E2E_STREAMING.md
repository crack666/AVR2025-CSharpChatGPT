# True E2E Streaming Implementation

Ziel: Chat-Token-Streaming und TTS-Streaming vollständig parallelisieren, so dass bereits während der fortlaufenden Text-Generierung Audio-Chunks abgespielt werden, ohne dass Wörter in der Mitte getrennt werden.

## Implementierung des Lookahead-Mechanismus für bessere Textsegmentierung

Das Problem mit der bisherigen Text-Segmentierung für die progressive TTS war, dass Wörter manchmal in der Mitte getrennt wurden, was zu unnatürlicher Sprachausgabe führte. Wir haben einen Lookahead-Mechanismus implementiert, der das Problem löst, indem er:

1. **Niemals Wörter trennt**, auch wenn dies bedeutet, dass wir auf mehr Tokens warten müssen
2. **Natürliche Sprachgrenzen priorisiert** (Satzenden, Kommas, Leerzeichen)
3. **Einen Puffer verwendet** und erst flusht, wenn ein komplettes Wort oder ein Satz vorliegt

## Wichtigste Änderungen

### 1. WebSocketAudioService.cs:

#### A. Verbesserte `ShouldFlush`-Methode:
- Konservativere Entscheidung, wann ein Text-Segment verarbeitet werden soll
- Prüft auf natürliche Sprachgrenzen wie Satzenden, Absätze, vollständige Wörter
- Spezielle Behandlung für Ellipsen (...), um sie nicht als Satzende zu interpretieren
- Nur Flushing bei Überschreitung einer Größenschwelle UND wenn dabei kein Wort getrennt wird

```csharp
bool ShouldFlush(StringBuilder buffer, char lastChar)
{
    // VERBESSERTE STRATEGIE:
    // 1. Satzenden haben höchste Priorität
    // 2. Absätze sind ebenfalls gute Trennstellen
    // 3. Bei Längenüberschreitung trotzdem auf natürliche Grenzen warten
    
    // Keine Verarbeitung bei leerem Puffer
    if (buffer.Length == 0) return false;
    
    // Schneller Pfad: Satzenden sind immer gute Stellen zum Flushen
    // Aber Vorsicht vor Ellipsen (...) - nicht als Satzende zählen
    bool isEndOfSentence = ".!?".Contains(lastChar);
    
    // Bei Punkten prüfen, ob es sich um eine Ellipse handelt
    if (lastChar == '.' && buffer.Length >= 3)
    {
        // Ist dies Teil einer Ellipse?
        if (buffer.Length >= 3 && 
            buffer[buffer.Length - 2] == '.' && 
            buffer[buffer.Length - 3] == '.')
        {
            // Teil einer Ellipse, kein echtes Satzende
            isEndOfSentence = false;
        }
    }
    
    // Weitere Prüfungen...
    
    // Priorisiere natürliche Grenzen für das Flushen
    return (isEndOfSentence && hasCompleteWord) || 
           (isParagraphEnd && hasCompleteWord) || 
           (lastChar == ',' && hasCompleteWord) ||
           (lastChar == ';' && hasCompleteWord) ||
           (lastChar == ':' && hasCompleteWord) ||
           (hasReachedSizeThreshold && hasCompleteWord);
}
```

#### B. Neuer `FlushSegmentAtSentenceBoundary`-Algorithmus:
- Implementiert einen Lookahead-Mechanismus, der einen Buffer zurückhält, bis eine natürliche Grenze erreicht ist
- Gibt Text nur zurück, wenn er an einer natürlichen Grenze endet
- Wenn keine natürliche Grenze gefunden wird, gibt er einen leeren String zurück und wartet auf mehr Text

```csharp
string FlushSegmentAtSentenceBoundary(StringBuilder buffer)
{
    string text = buffer.ToString();
    
    // IMPLEMENTIERUNG EINES LOOKAHEAD-MECHANISMUS:
    // 1. Erst nach Satzgrenzen suchen (höchste Priorität)
    // 2. Wenn keine Satzgrenze gefunden, nach Wortgrenzen suchen
    // 3. Niemals mitten im Wort trennen - lieber warten!
    
    // Setze einen Ziel-Limit für die Suche (Soft-Limit, nicht hart)
    int targetLimit = 200; // Ungefährer Zielwert, aber nie erzwungen
    
    // 1. SCHRITT: Suche zunächst nach einer Satzgrenze
    int splitPos = -1;
    for (int i = Math.Min(text.Length - 1, targetLimit * 2); i >= 0; i--)
    {
        if (IsSentenceEndBoundary(text, i))
        {
            splitPos = i + 1; // inkl. Satzzeichen
            break;
        }
    }

    // 2. SCHRITT: Falls keine Satzgrenze gefunden, suche nach einer Wortgrenze
    if (splitPos <= 0)
    {
        // Suche nach dem letzten Leerzeichen nahe dem Ziel
        for (int i = Math.Min(text.Length - 1, targetLimit * 2); i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                splitPos = i + 1; // inkl. Leerzeichen
                break;
            }
        }
    }

    // 3. SCHRITT: KRITISCHER LOOKAHEAD-MECHANISMUS
    // Wenn wir einen Trennpunkt haben, prüfen, ob dieser tatsächlich eine natürliche Trennstelle ist
    if (splitPos > 0)
    {
        // Lookahead: Warte auf echtes Wort- oder Satzende
        // Wenn der gefundene Punkt mitten in einem Wort ist, vorwärts suchen
        while (splitPos < text.Length &&
              !char.IsWhiteSpace(text[splitPos - 1]) &&
              !IsSentenceEndBoundary(text, splitPos - 1))
        {
            // Wenn wir am Ende des Puffers sind, abbrechen und auf mehr Text warten
            if (splitPos == text.Length - 1)
            {
                // Noch kein natürliches Ende - alles zurück in den Puffer
                return string.Empty;
            }
            splitPos++;
        }
    }

    // Wenn keine natürliche Grenze gefunden, warte auf mehr Input
    if (splitPos <= 0 || splitPos >= text.Length)
    {
        return string.Empty;
    }

    // Weitere Prüfungen und Verarbeitung...
    
    return flush; // Gibt den Text bis zur natürlichen Grenze zurück
}
```

### 2. ProgressiveTTSSynthesizer.cs:

#### A. Angepasste Konstanten:
- `MaxChunkSize` auf einen sehr hohen Wert gesetzt (5000), um deutlich zu machen, dass es nur ein Soft-Limit ist
- Klarere Dokumentation, dass Wortgrenzen Priorität über Chunklängen haben
- `NeverSplitWords = true` als nicht verhandelbarer Wert festgelegt

```csharp
/// <summary>
/// Soft-Limit für sehr lange Chunks - wird NIEMALS erzwungen, 
/// sondern nur als Hinweis verwendet, nach einem geeigneten Wort/Satzende zu suchen.
/// Wenn kein geeignetes Ende gefunden wird, wird auch bei größeren Texten NICHT getrennt.
/// </summary>
private const int MaxChunkSize = 5000;  // Absichtlich sehr hoch angesetzt
```

#### B. Verbesserte `FindWordBoundary`-Methode:
- Implementiert einen umfassenden Lookahead-Mechanismus, der bis zu 100 Zeichen nach einer Position schaut
- Priorisiert Satzenden, dann Kommas/Semikolons, dann andere Satzzeichen, dann Wortgrenzen
- Stellt sicher, dass wir immer an natürlichen Grenzen trennen, selbst wenn wir länger warten müssen

```csharp
// NEUER LOOKAHEAD-MECHANISMUS:
// Selbst wenn wir bereits eine Position gefunden haben, die nicht mitten im Wort ist,
// suchen wir nach einer natürlicheren Trennstelle in einem erweiterten Bereich.

// Priorisierung:
// 1. Satzende (. ! ?)
// 2. Komma oder Semikolon
// 3. Anderes Satzzeichen 
// 4. Leerzeichen nach einem Wort

// Suchbereich für Lookahead - wir schauen bis zu 100 Zeichen nach der aktuellen Position
int lookaheadLimit = Math.Min(text.Length, position + 100);
int splitPos = position;  // Start mit der aktuellen Position

// Lookahead: Warte auf echtes Wort- oder Satzende
// 1. Satzenden haben höchste Priorität
for (int i = position; i < lookaheadLimit; i++)
{
    if (i < text.Length && (text[i] == '.' || text[i] == '!' || text[i] == '?'))
    {
        // Prüfe, ob es ein echtes Satzende ist (kein Abkürzungs-Punkt)
        bool isEllipsis = (i > 0 && text[i-1] == '.') || (i < text.Length - 1 && text[i+1] == '.');
        bool isRealSentenceEnd = !isEllipsis && (i == text.Length - 1 || char.IsWhiteSpace(text[i+1]));
        
        if (isRealSentenceEnd)
        {
            // Setze die Position direkt hinter das Satzzeichen
            int newPos = i + 1;
            // Überspringe Leerzeichen nach dem Satzende
            while (newPos < text.Length && char.IsWhiteSpace(text[newPos])) newPos++;
            
            LogDebug($"LOOKAHEAD: Found sentence end at position {i}, moving to {newPos}");
            return newPos;
        }
    }
}

// Weitere Priorisierungen und Fallbacks...
```

#### C. Verbesserte `SplitTextIntoSentenceChunks`-Methode:
- Verwendet eine präzisere Regex für Satzerkennung, die Ellipsen und Abkürzungen berücksichtigt
- Behandelt Resttexte intelligenter mit dem Lookahead-Mechanismus
- Fasst sehr kurze Chunks zusammen für bessere Audioqualität

```csharp
// SCHRITT 1: Versuche zunächst, komplette Sätze zu extrahieren
// Verwende eine präzisere Regex für Satzenden, die auch Ellipsen korrekt berücksichtigt
var sentencePattern = new Regex(
    @".*?(?<!\.)(?<!\.)(?<!\w\.\w)(?<!\w[A-Z])(?<!\w[A-Z]\.)(?<!etc)(?<!i\.e)(?<!e\.g)[\.!?](?=\s|$|""|'|\)|\]|\})", 
    RegexOptions.Singleline | RegexOptions.IgnoreCase
);
```

## Fazit

Der implementierte Lookahead-Mechanismus stellt sicher, dass Text-Chunks nur an natürlichen Sprachgrenzen getrennt werden. Dies führt zu einer deutlich natürlicheren Sprachausgabe, da Wörter niemals in der Mitte getrennt werden, selbst wenn dies bedeutet, dass die Chunks größer oder kleiner als die Zielgröße sein können.

Die Strategie ist jetzt:
1. Sammle Tokens im Puffer
2. Prüfe an natürlichen Grenzen (Satzenden, Absätze, vollständige Wörter), ob wir flushen sollten
3. Wenn wir flushen, verwende den Lookahead-Mechanismus, um sicherzustellen, dass wir nur an natürlichen Grenzen trennen
4. Wenn keine natürliche Grenze gefunden wird, warte auf mehr Text