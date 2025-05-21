using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;

namespace VoiceAssistant.Plugins.OpenAI
{
    /// <summary>
    /// Progressive Text-to-Speech synthesizer using OpenAI API.
    /// Breaks text into natural chunks for faster feedback while synthesizing speech.
    /// Ensures words are never split between chunks for natural-sounding speech.
    /// </summary>
    public class ProgressiveTTSSynthesizer : ISynthesizer
    {
        private readonly HttpClient _httpClient;
        private readonly bool _enableDebugLogging = true;

        /// <summary>
        /// Minimum chunk size in characters to process as a separate unit.
        /// This is more of a guidance than a strict limit - we'll allow smaller chunks 
        /// if it means not breaking a word.
        /// </summary>
        private const int MinChunkSize = 5;

        /// <summary>
        /// Soft-Limit für sehr lange Chunks - wird NIEMALS erzwungen, 
        /// sondern nur als Hinweis verwendet, nach einem geeigneten Wort/Satzende zu suchen.
        /// Wenn kein geeignetes Ende gefunden wird, wird auch bei größeren Texten NICHT getrennt.
        /// </summary>
        private const int MaxChunkSize = 5000;  // Absichtlich sehr hoch angesetzt

        /// <summary>
        /// Target chunk size in characters for most chunks - the algorithm will try to create chunks close to this size.
        /// Dies ist nur ein Richtwert - NIE wird ein Wort dafür getrennt.
        /// </summary>
        private const int TargetChunkSize = 250;  // Erhöht für bessere Audioqualität

        /// <summary>
        /// Target size for the first chunk - we want it smaller for faster initial feedback.
        /// Dies ist nur ein Richtwert - NIE wird ein Wort dafür getrennt.
        /// </summary>
        private const int FirstChunkTargetSize = 60;  // Etwas größer für bessere Qualität des ersten Chunks

        /// <summary>
        /// Target size for early chunks (chunks 2-3) - slightly larger than first but still optimized for quick delivery
        /// Dies ist nur ein Richtwert - NIE wird ein Wort dafür getrennt.
        /// </summary>
        private const int EarlyChunkTargetSize = 80;

        /// <summary>
        /// Number of early chunks to optimize for quick delivery
        /// </summary>
        private const int EarlyChunkCount = 3;

        /// <summary>
        /// ABSOLUTE höchste Priorität: Wörter nie trennen, auch wenn die Chunk-Größe dadurch stark variiert.
        /// Dies ist ein nicht verhandelbarer Wert!
        /// </summary>
        private const bool NeverSplitWords = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressiveTTSSynthesizer"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client to use for API requests.</param>
        public ProgressiveTTSSynthesizer(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <summary>
        /// Implements the ISynthesizer.SynthesizeTextChunkAsync method for single-shot TTS.
        /// Synthesizes a single text chunk without chunking/segmentation.
        /// </summary>
        /// <param name="textChunk">Text chunk to synthesize.</param>
        /// <param name="voice">Voice identifier.</param>
        /// <returns>Raw audio bytes for this text chunk.</returns>
        public Task<byte[]> SynthesizeTextChunkAsync(string textChunk, string voice)
            => SynthesizeAsync(textChunk, voice);

        /// <summary>
        /// Regular implementation of ISynthesizer interface for backward compatibility.
        /// Synthesizes the complete text in a single request.
        /// </summary>
        /// <param name="text">The text to synthesize.</param>
        /// <param name="voice">The voice to use.</param>
        /// <returns>Audio bytes representing the synthesized speech.</returns>
        public async Task<byte[]> SynthesizeAsync(string text, string voice)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Text input for speech synthesis cannot be empty.", nameof(text));
            }

            text = text.Trim();
            if (text.Length == 0)
            {
                text = "No response available.";
            }

            LogDebug($"Standard synthesis of {text.Length} characters with voice {voice}");

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

            var payload = new { model = "tts-1", voice = voice, input = text };
            var body = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                var msg = Encoding.UTF8.GetString(bytes);
                throw new ApplicationException($"TTS failed: {msg}");
            }
            return bytes;
        }

        private bool IsSentenceEndPunctuation(string text, int pos)
        {
            // Prüft, ob an Position pos ein Punkt/!/? steht, der nicht Teil einer Ellipse oder Abkürzung ist,
            // sondern direkt gefolgt wird von Whitespace oder String-Ende.
            if (pos < 0 || pos >= text.Length) return false;
            // Regex: ^[.!?](?=\s|$)  --> Satzzeichen gefolgt von Leerraum oder Ende
            return Regex.IsMatch(text.Substring(pos, Math.Min(2, text.Length - pos)), "^[.!?](?=\\s|$)");
        }


        /// <summary>
        /// Synthesizes speech from text in chunks, providing each chunk via callback as it becomes available.
        /// This allows for faster perceived response time as the UI can play audio while the rest is being synthesized.
        /// Uses natural language boundaries to create sensible chunks for better speech quality.
        /// </summary>
        /// <param name="text">The text to synthesize.</param>
        /// <param name="voice">The voice to use.</param>
        /// <param name="onChunkReady">Callback that will receive audio bytes for each synthesized chunk.</param>
        /// <returns>A task representing the complete synthesis operation.</returns>
        public async Task ChunkedSynthesisAsync(string text, string voice, Action<byte[]> onChunkReady)
        {
            if (onChunkReady == null)
                throw new ArgumentNullException(nameof(onChunkReady));

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Text input for speech synthesis cannot be empty.", nameof(text));
            }

            text = text.Trim();
            if (text.Length == 0)
            {
                throw new ArgumentException("Trimmed text input is empty.", nameof(text));
            }

            // For very short text, just do a single synthesis
            if (text.Length < MinChunkSize * 2)
            {
                LogDebug($"Text too short ({text.Length} chars), using single synthesis");
                var audioBytes = await SynthesizeAsync(text, voice);
                onChunkReady(audioBytes);
                return;
            }

            LogDebug($"Starting chunked synthesis of {text.Length} characters with voice {voice}");

            // Extra debug info about the raw text
            LogChunkingDebug($"RAW INPUT TEXT: '{text}'", true);
            LogChunkingDebug("Character analysis:", true);
            for (int i = 0; i < Math.Min(100, text.Length); i++)
            {
                if (char.IsLetterOrDigit(text[i]) || char.IsPunctuation(text[i]))
                {
                    LogChunkingDebug($"  Pos {i}: '{text[i]}' (IsLetter: {char.IsLetter(text[i])}, IsDigit: {char.IsDigit(text[i])}, IsPunctuation: {char.IsPunctuation(text[i])})", true);
                }
            }

            // Split text into natural language chunks
            // This is the key function that determines where to break the text
            //var chunks = SplitTextIntoNaturalChunks(text);
            var chunks = SplitTextIntoSentenceChunks(text);
            LogDebug($"Split text into {chunks.Count} chunks");

            // Detailed logging of chunks with character analysis at boundaries
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                LogChunkingDebug($"CHUNK #{i}: '{chunk}'", true);

                // If there's a next chunk, analyze the boundary
                if (i < chunks.Count - 1)
                {
                    string nextChunk = chunks[i + 1];
                    LogChunkingDebug($"BOUNDARY ANALYSIS between chunk {i} and {i + 1}:", true);

                    // Log last few chars of current chunk
                    int end = chunk.Length;
                    for (int j = Math.Max(0, end - 5); j < end; j++)
                    {
                        LogChunkingDebug($"  Chunk {i} Pos {j}: '{chunk[j]}' (IsLetter: {char.IsLetter(chunk[j])}, IsPunctuation: {char.IsPunctuation(chunk[j])})", true);
                    }

                    // Log first few chars of next chunk
                    for (int j = 0; j < Math.Min(5, nextChunk.Length); j++)
                    {
                        LogChunkingDebug($"  Chunk {i + 1} Pos {j}: '{nextChunk[j]}' (IsLetter: {char.IsLetter(nextChunk[j])}, IsPunctuation: {char.IsPunctuation(nextChunk[j])})", true);
                    }
                }
            }

            // Process each chunk sequentially
            // We could parallelize this, but sequential processing ensures chunks are received in order
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                LogDebug($"Synthesizing chunk {i + 1}/{chunks.Count}: \"{ShortenForLog(chunk)}\" ({chunk.Length} chars)");

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

                    // Add a bit of breathing room at the end of chunks that aren't followed by punctuation
                    // This helps create more natural spacing between chunks
                    string processedChunk = chunk;
                    if (i < chunks.Count - 1 && !EndsWithPunctuation(chunk))
                    {
                        // Adding a slight pause if the chunk doesn't end with punctuation
                        processedChunk = chunk + ",";
                        LogDebug($"Added comma to chunk for natural pause");
                    }

                    var payload = new { model = "tts-1", voice = voice, input = processedChunk };
                    var body = JsonSerializer.Serialize(payload);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(request);
                    var bytes = await response.Content.ReadAsByteArrayAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        var msg = Encoding.UTF8.GetString(bytes);
                        LogDebug($"Chunk synthesis error: {msg}");
                        throw new ApplicationException($"TTS failed for chunk {i + 1}: {msg}");
                    }

                    // Invoke callback with the synthesized audio for this chunk
                    LogDebug($"Chunk {i + 1} synthesized: {bytes.Length} bytes");
                    onChunkReady(bytes);
                }
                catch (Exception ex)
                {
                    LogDebug($"Error synthesizing chunk {i + 1}: {ex.Message}");
                    throw;
                }
            }

            LogDebug("Chunked synthesis completed");
        }

        /// <summary>
        /// Checks if the text ends with punctuation that would naturally create a pause.
        /// </summary>
        /// <param name="text">The text to check.</param>
        /// <returns>True if the text ends with punctuation, false otherwise.</returns>
        private bool EndsWithPunctuation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            char lastChar = text[text.Length - 1];
            return lastChar == '.' || lastChar == '!' || lastChar == '?' ||
                   lastChar == ';' || lastChar == ':' || lastChar == ',' ||
                   lastChar == ')' || lastChar == ']' || lastChar == '}';
        }

        /// <summary>
        /// Creates a shortened version of the chunk text suitable for logging.
        /// </summary>
        /// <param name="text">The text to shorten.</param>
        /// <returns>A shortened version of the text.</returns>
        private string ShortenForLog(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            const int maxLogLength = 50;

            if (text.Length <= maxLogLength)
                return text;

            // This truncation is only for logging display purposes
            // Using a special marker "[...]" instead of "..." to avoid confusion with actual text content
            return text.Substring(0, maxLogLength / 2 - 3) + "[...]" +
                   text.Substring(text.Length - maxLogLength / 2);
        }

        /// <summary>
        /// Verbesserte Methode zum Aufteilen von Text in natürliche Satzeinheiten
        /// Verwendet den optimierten Lookahead-Mechanismus, um sicherzustellen, dass 
        /// Wörter niemals getrennt werden.
        /// </summary>
        /// <param name="input">Der zu verarbeitende Text</param>
        /// <returns>Liste mit Satz-Chunks</returns>
        private List<string> SplitTextIntoSentenceChunks(string input)
        {
            var chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(input)) return chunks;

            LogChunkingDebug($"VERBESSERTE SATZTRENNUNG: Verarbeite {input.Length} Zeichen...", true);

            // SCHRITT 1: Versuche zunächst, komplette Sätze zu extrahieren
            // Verwende eine präzisere Regex für Satzenden, die auch Ellipsen korrekt berücksichtigt
            var sentencePattern = new Regex(
                @".*?(?<!\.)(?<!\.)(?<!\w\.\w)(?<!\w[A-Z])(?<!\w[A-Z]\.)(?<!etc)(?<!i\.e)(?<!e\.g)[\.!?](?=\s|$|""|'|\)|\]|\})",
                RegexOptions.Singleline | RegexOptions.IgnoreCase
            );
            var matches = sentencePattern.Matches(input);

            int lastIndex = 0;
            foreach (Match match in matches)
            {
                string sentence = match.Value.Trim();
                if (!string.IsNullOrEmpty(sentence))
                {
                    LogChunkingDebug($"Gefundener Satz: '{sentence}'", true);
                    chunks.Add(sentence);
                }
                lastIndex = match.Index + match.Length;
            }

            // SCHRITT 2: Verarbeite Resttext (falls nach letztem Satz noch Text übrig)
            if (lastIndex < input.Length)
            {
                string remainder = input.Substring(lastIndex).Trim();
                if (!string.IsNullOrEmpty(remainder))
                {
                    LogChunkingDebug($"Resttext nach Satzerkennung: '{remainder}'", true);

                    // VERBESSERUNG: Lange Reste intelligent an natürlichen Wortgrenzen teilen
                    if (remainder.Length > 100)
                    {
                        // Finde geeignete Trennstelle mit dem Lookahead-Mechanismus
                        int splitPos = FindWordBoundary(remainder, 80);

                        // Lookahead: Warte auf echtes Wort- oder Satzende
                        while (splitPos < remainder.Length &&
                               !char.IsWhiteSpace(remainder[splitPos - 1]) &&
                               !IsPunctuation(remainder[splitPos - 1]))
                        {
                            // Wenn Ende des Textes, beenden
                            if (splitPos == remainder.Length - 1) break;
                            splitPos++;
                        }

                        // Wenn wir eine gute Trennstelle gefunden haben, teile den Text
                        if (splitPos > 0 && splitPos < remainder.Length - 10)
                        {
                            string firstPart = remainder.Substring(0, splitPos).TrimEnd();
                            string secondPart = remainder.Substring(splitPos).TrimStart();

                            LogChunkingDebug($"Teile langen Resttext: '{firstPart}' | '{secondPart}'", true);

                            if (!string.IsNullOrEmpty(firstPart))
                                chunks.Add(firstPart);

                            if (!string.IsNullOrEmpty(secondPart))
                                chunks.Add(secondPart);
                        }
                        else
                        {
                            // Keine gute Trennstelle gefunden, behalte den gesamten Rest
                            chunks.Add(remainder);
                        }
                    }
                    else
                    {
                        // Kurzer Rest kann direkt als Chunk hinzugefügt werden
                        chunks.Add(remainder);
                    }
                }
            }

            // Schritt 3: Überprüfe, ob es sehr kurze Chunks gibt, die mit dem nächsten zusammengefasst werden könnten
            for (int i = chunks.Count - 2; i >= 0; i--)
            {
                // Wenn der aktuelle und der nächste Chunk zusammen unter einem Schwellwert liegen,
                // fasse sie zusammen für bessere Audioqualität
                if (chunks[i].Length + chunks[i + 1].Length < 50)
                {
                    LogChunkingDebug($"Fasse kurze Chunks zusammen: '{chunks[i]}' + '{chunks[i + 1]}'", true);
                    chunks[i] = chunks[i] + " " + chunks[i + 1];
                    chunks.RemoveAt(i + 1);
                }
            }

            LogChunkingDebug($"Finale Chunk-Anzahl nach Satz-Splitting: {chunks.Count}", true);
            return chunks;
        }


        /// <summary>
        /// Splits a text into natural language chunks based on sentence and phrase boundaries.
        /// Tries to respect natural breaks in speech for better synthesis quality.
        /// Makes the first chunk shorter for faster initial playback.
        /// Ensures that words are never split between chunks for better audio quality.
        /// </summary>
        /// <param name="text">The text to split.</param>
        /// <returns>A list of text chunks.</returns>
        private List<string> SplitTextIntoNaturalChunks(string text)
        {
            // *** COMPLETELY REWRITTEN WITH BRUTE FORCE APPROACH TO FIX WORD SPLITTING ISSUES ***
            // Instead of trying to find "natural" boundaries, we will now use a simplistic
            // sentence/word based approach that guarantees no word splits

            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text)) return chunks;

            // Log the full text for debugging
            LogChunkingDebug($"FULL TEXT TO SPLIT: '{text}'", true);

            // ======= SENTENCE-FIRST APPROACH =======
            // In this approach, we prioritize natural sentence boundaries above all else
            // We only split at word boundaries if absolutely necessary
            // We never, ever split words

            // Helper function to check if a character is at the end of a sentence
            // but also filtering out special cases like ellipses (...)
            bool IsSentenceEnd(string text, int pos)
            {
                if (pos < 0 || pos >= text.Length) return false;

                // If the character is not a potential sentence ender, return false
                if (text[pos] != '.' && text[pos] != '!' && text[pos] != '?') return false;

                // Check for ellipsis: if we have "..." don't consider it a sentence end
                if (text[pos] == '.' &&
                    ((pos > 0 && text[pos - 1] == '.') ||
                     (pos < text.Length - 1 && text[pos + 1] == '.')))
                {
                    return false;
                }

                // For other punctuation, it should be a sentence end if followed by space or end of text
                return pos == text.Length - 1 || char.IsWhiteSpace(text[pos + 1]);
            }

            LogChunkingDebug($"Starting sentence-first splitting approach", true);

            // First, extract all complete sentences
            List<string> sentences = new List<string>();
            StringBuilder currentSentence = new StringBuilder();

            // Break the text into sentences
            for (int i = 0; i < text.Length; i++)
            {
                currentSentence.Append(text[i]);

                // If we found a sentence end, add it to our list
                if (IsSentenceEnd(text, i))
                {
                    // Also make sure we include the next whitespace to maintain proper spacing
                    int j = i + 1;
                    while (j < text.Length && char.IsWhiteSpace(text[j]))
                    {
                        currentSentence.Append(text[j]);
                        j++;
                    }

                    string sentence = currentSentence.ToString();
                    LogChunkingDebug($"Found sentence ending at pos {i}: '{sentence}'", true);
                    sentences.Add(sentence);
                    currentSentence.Clear();

                    // Skip the whitespace we just added
                    i = j - 1;
                }
            }

            // Add any remaining text as a final sentence
            if (currentSentence.Length > 0)
            {
                string sentence = currentSentence.ToString();
                LogChunkingDebug($"Found final (incomplete) sentence: '{sentence}'", true);
                sentences.Add(sentence);
            }

            LogChunkingDebug($"Split text into {sentences.Count} sentences", true);

            // For very short texts with just one sentence
            if (sentences.Count == 1 && sentences[0].Length <= 100)
            {
                LogChunkingDebug($"Text is a single short sentence, using it as a single chunk", true);
                chunks.Add(sentences[0]);
                return chunks;
            }

            // Special case: If we only have one very long sentence, we'll have to split it
            if (sentences.Count == 1)
            {
                string longSentence = sentences[0];
                LogChunkingDebug($"Single long sentence ({longSentence.Length} chars), splitting at boundary", true);

                int splitPos = FindWordBoundary(longSentence, FirstChunkTargetSize);
                // Lookahead: warte auf echtes Wort- oder Satzende per Regex
                while (splitPos < longSentence.Length &&
                       !char.IsWhiteSpace(longSentence[splitPos]) &&
                       !IsSentenceEndPunctuation(longSentence, splitPos))
                {
                    splitPos++;
                }
                if (splitPos >= longSentence.Length)
                {
                    chunks.Add(longSentence);
                    return chunks;
                }

                string firstChunk = longSentence.Substring(0, splitPos).TrimEnd();
                string rest = longSentence.Substring(splitPos).TrimStart();

                LogChunkingDebug($"Adding FIRST CHUNK (truncated): '{firstChunk}' ({firstChunk.Length} chars)", true);
                chunks.Add(firstChunk);
                sentences[0] = rest;
            }


            // For multiple sentences, create chunks based purely on sentence boundaries
            // First sentence is ALWAYS its own chunk for fastest response
            var firstText = sentences[0];
            if (firstText.Length > FirstChunkTargetSize)
            {
                int splitPos = FindWordBoundary(firstText, FirstChunkTargetSize);
                while (splitPos < firstText.Length &&
                        !char.IsWhiteSpace(firstText[splitPos]) &&
                        !IsSentenceEndPunctuation(firstText, splitPos))
                {
                    splitPos++;
                }
                string firstChunk = firstText.Substring(0, splitPos).TrimEnd();
                sentences[0] = firstText.Substring(splitPos).TrimStart();

                LogChunkingDebug($"Adding FIRST CHUNK (truncated): '{firstChunk}' ({firstChunk.Length} chars)", true);
                chunks.Add(firstChunk);
            }
            else
            {
                LogChunkingDebug($"Adding FIRST CHUNK (full): '{firstText}' ({firstText.Length} chars)", true);
                chunks.Add(firstText);
            }



            // For remaining sentences, combine into chunks naturally
            StringBuilder currentChunk = new StringBuilder();
            int totalSentencesInCurrentChunk = 0;

            // Process from the second sentence onward
            for (int i = 1; i < sentences.Count; i++)
            {
                string sentence = sentences[i];

                // Special case: If this single sentence is extremely long (over 200 chars), 
                // it might make sense to make it its own chunk
                if (sentence.Length > 200)
                {
                    // If we have accumulated text, add it as a chunk first
                    if (currentChunk.Length > 0)
                    {
                        string chunkText = currentChunk.ToString().Trim();
                        LogChunkingDebug($"Adding CHUNK (before long sentence): '{chunkText}' ({chunkText.Length} chars)", true);
                        chunks.Add(chunkText);
                        currentChunk.Clear();
                        totalSentencesInCurrentChunk = 0;
                    }

                    // Add this long sentence as its own chunk
                    LogChunkingDebug($"Adding CHUNK (long sentence): '{sentence}' ({sentence.Length} chars)", true);
                    chunks.Add(sentence);
                    continue;
                }

                // For typical sentences, accumulate until we have a reasonable chunk size
                // Base the decision on number of sentences rather than character count
                if (totalSentencesInCurrentChunk >= 2)
                {
                    // After 2-3 sentences, finalize the chunk
                    string chunkText = currentChunk.ToString().Trim();
                    LogChunkingDebug($"Adding CHUNK (multiple sentences): '{chunkText}' ({chunkText.Length} chars)", true);
                    chunks.Add(chunkText);
                    currentChunk.Clear();
                    totalSentencesInCurrentChunk = 0;
                }

                // Add this sentence to the current chunk
                currentChunk.Append(sentence);
                totalSentencesInCurrentChunk++;

                // Special case: if this is the last sentence, add it as a chunk
                if (i == sentences.Count - 1 && currentChunk.Length > 0)
                {
                    string chunkText = currentChunk.ToString().Trim();
                    LogChunkingDebug($"Adding FINAL CHUNK: '{chunkText}' ({chunkText.Length} chars)", true);
                    chunks.Add(chunkText);
                }
            }

            LogChunkingDebug($"Final chunks count: {chunks.Count}", true);

            // Verify no empty chunks
            for (int i = chunks.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(chunks[i]))
                {
                    LogChunkingDebug($"Removing empty chunk at index {i}", true);
                    chunks.RemoveAt(i);
                }
            }

            // The new algorithm doesn't require handling the remaining text separately
            // It processes all text in the main loop above

            // Make a final pass to combine chunks that are too small
            // This is crucial to avoid having too many small chunks that would cause interruptions
            List<string> optimizedChunks = new List<string>();
            StringBuilder combined = new StringBuilder();

            int minOptimalChunkSize = MinChunkSize * 2; // We prefer slightly larger chunks for better flow

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                bool isLastChunk = (i == chunks.Count - 1);

                // If this chunk is very small and not the last one, try to combine it
                if (chunk.Length < minOptimalChunkSize && !isLastChunk &&
                    combined.Length + chunk.Length <= MaxChunkSize)
                {
                    if (combined.Length > 0) combined.Append(" ");
                    combined.Append(chunk);
                    continue;
                }

                // If we can combine this chunk with what we have so far, do it
                if (combined.Length + (combined.Length > 0 ? 1 : 0) + chunk.Length <= MaxChunkSize)
                {
                    if (combined.Length > 0) combined.Append(" ");
                    combined.Append(chunk);

                    // If this is the last chunk, add the combined content
                    if (isLastChunk && combined.Length > 0)
                    {
                        string combinedText = combined.ToString();
                        LogDebug($"Added optimized chunk: \"{combinedText}\" ({combinedText.Length} chars)");
                        optimizedChunks.Add(combinedText);
                    }
                }
                else
                {
                    // Can't combine, so finalize the current combined chunk if any
                    if (combined.Length > 0)
                    {
                        string combinedText = combined.ToString();
                        LogDebug($"Added optimized chunk: \"{combinedText}\" ({combinedText.Length} chars)");
                        optimizedChunks.Add(combinedText);
                        combined.Clear();
                    }

                    // Now handle the current chunk
                    if (chunk.Length <= MaxChunkSize)
                    {
                        // Start a new combined chunk with this one
                        combined.Append(chunk);

                        // If this is the last chunk, add it
                        if (isLastChunk)
                        {
                            string finalText = combined.ToString();
                            LogDebug($"Added final optimized chunk: \"{finalText}\" ({finalText.Length} chars)");
                            optimizedChunks.Add(finalText);
                        }
                    }
                    else
                    {
                        // This chunk is still too big, add it directly
                        LogDebug($"Added large chunk directly: \"{chunk}\" ({chunk.Length} chars)");
                        optimizedChunks.Add(chunk);
                    }
                }
            }

            if (combined.Length > 0 && !optimizedChunks.Contains(combined.ToString()))
            {
                string combinedText = combined.ToString();
                LogDebug($"Added remaining combined chunk: \"{combinedText}\" ({combinedText.Length} chars)");
                optimizedChunks.Add(combinedText);
            }

            // Log the final chunk count
            LogDebug($"Final chunk count: {optimizedChunks.Count} (original: {chunks.Count})");

            return optimizedChunks.Count > 0 ? optimizedChunks : chunks;
        }

        /// <summary>
        /// Finds a good phrase boundary (comma, semicolon, colon, etc.) within a range of text.
        /// Prioritizes natural language boundaries for better speech synthesis chunking.
        /// </summary>
        /// <param name="text">The text to search.</param>
        /// <param name="startIndex">Start position to search from.</param>
        /// <param name="endIndex">End position to search to.</param>
        /// <returns>Position of phrase boundary or -1 if not found.</returns>
        private int FindPhraseEndPosition(string text, int startIndex, int endIndex)
        {
            // Using a Dictionary to prioritize different separators - higher priority ones are checked first
            // This helps ensure we split at more natural points in speech
            Dictionary<char, int> phraseSeparatorPriorities = new Dictionary<char, int>
            {
                { ';', 10 },  // Semicolon - highest priority
                { ':', 9 },   // Colon
                { ',', 8 },   // Comma
                { ')', 7 },   // Closing parenthesis
                { '(', 6 },   // Opening parenthesis
                { ']', 5 },   // Closing bracket
                { '[', 4 },   // Opening bracket
                { '}', 3 },   // Closing brace
                { '{', 2 },   // Opening brace
                { '-', 1 }    // Dash - lowest priority
            };

            // First pass: look for higher priority separators (semicolons, colons, commas)
            for (int priority = 10; priority >= 8; priority--)
            {
                // Search backward from endIndex to find the closest separator with this priority
                for (int i = Math.Min(endIndex, text.Length - 1); i >= startIndex; i--)
                {
                    if (phraseSeparatorPriorities.TryGetValue(text[i], out int separatorPriority) &&
                        separatorPriority == priority)
                    {
                        // Extra check: if it's a comma, make sure it's not part of a number (e.g., 1,000)
                        if (text[i] == ',' && i > 0 && i < text.Length - 1)
                        {
                            bool isDigitBefore = i > 0 && char.IsDigit(text[i - 1]);
                            bool isDigitAfter = i < text.Length - 1 && char.IsDigit(text[i + 1]);

                            if (isDigitBefore && isDigitAfter)
                            {
                                // This looks like a comma in a number, skip it
                                continue;
                            }
                        }

                        LogDebug($"Found phrase boundary: '{text[i]}' at position {i}");
                        return i;
                    }
                }
            }

            // Second pass: look for quote boundaries or other punctuation that could be reasonable places to split
            for (int i = Math.Min(endIndex, text.Length - 1); i >= startIndex; i--)
            {
                // Check for quotes, which can be natural pauses
                if (text[i] == '"' || text[i] == '\'' || text[i] == '"' || text[i] == '"' || text[i] == '\'')
                {
                    LogDebug($"Found quote boundary: '{text[i]}' at position {i}");
                    return i;
                }

                // Check for other medium-priority separators
                if (phraseSeparatorPriorities.TryGetValue(text[i], out int separatorPriority) &&
                    separatorPriority < 8 && separatorPriority > 1)
                {
                    LogDebug($"Found medium-priority boundary: '{text[i]}' at position {i}");
                    return i;
                }
            }

            // Third pass: check for dashes or other low-priority separators
            for (int i = Math.Min(endIndex, text.Length - 1); i >= startIndex; i--)
            {
                if (phraseSeparatorPriorities.TryGetValue(text[i], out int separatorPriority) &&
                    separatorPriority == 1)
                {
                    LogDebug($"Found low-priority boundary: '{text[i]}' at position {i}");
                    return i;
                }
            }

            // Last resort: look for natural word endings like articles or prepositions
            string textSubstring = text.Substring(startIndex, Math.Min(endIndex - startIndex + 1, text.Length - startIndex));
            string[] commonWordsToPrecede = { " the ", " a ", " an ", " to ", " of ", " in ", " on ", " with ", " by " };

            foreach (var word in commonWordsToPrecede)
            {
                int lastIndex = textSubstring.LastIndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (lastIndex >= 0)
                {
                    int position = startIndex + lastIndex + word.Length - 1;
                    LogDebug($"Found word boundary at '{word}', position {position}");
                    return position;
                }
            }

            return -1; // No suitable separator found
        }

        /// <summary>
        /// Finds a word boundary near the given position, ensuring we NEVER break a word in the middle.
        /// Uses a smart algorithm with lookahead to find the most natural place to split text, prioritizing sentence
        /// boundaries, then punctuation, then word boundaries.
        /// Implementing lookahead: Selbst wenn eine Position nahe dem Zielwert gefunden wird,
        /// springe noch weiter vor, bis eine wirklich natürliche Grenze erreicht ist.
        /// </summary>
        /// <param name="text">The text to search.</param>
        /// <param name="position">The approximate position to find a boundary near.</param>
        /// <returns>Position of a word boundary, guaranteed to be between words, not within a word.</returns>
        private int FindWordBoundary(string text, int position)
        {
            // Safety checks for bounds
            if (text == null || text.Length == 0) return 0;
            position = Math.Min(position, text.Length - 1);
            position = Math.Max(position, 0);

            // CRITICAL: Check if we're at position 0, which is always safe
            if (position == 0) return 0;

            // Before anything else, check if we're in the middle of a word
            // If we are, we MUST move to either the beginning or end of that word
            // We prefer the end of the word to avoid splitting

            // First see if the character at position is part of a word
            // First check if the position is in the middle of a word
            // A word character is not whitespace and not punctuation
            bool inWord = position < text.Length &&
                        !char.IsWhiteSpace(text[position]) &&
                        !IsPunctuation(text[position]);

            // Also check if we're at a period surrounded by letters (like "v.s") which should be kept together
            bool atInternalPeriod = position < text.Length && text[position] == '.' &&
                                  position > 0 && position < text.Length - 1 &&
                                  char.IsLetterOrDigit(text[position - 1]) &&
                                  char.IsLetterOrDigit(text[position + 1]);

            // Handle cases of being inside a word or at an internal period (like v.s)
            if (inWord || atInternalPeriod)
            {
                // We're inside a word or at a period within a word - find the end
                int wordEnd = position;

                // Continue until we hit whitespace or punctuation (except periods within words)
                while (wordEnd < text.Length)
                {
                    // If we're at whitespace, stop
                    if (char.IsWhiteSpace(text[wordEnd]))
                        break;

                    // If we're at punctuation, check if it's an internal period
                    if (IsPunctuation(text[wordEnd]))
                    {
                        // Only continue if this is a period surrounded by letters
                        bool isPeriodWithinWord = text[wordEnd] == '.' &&
                                               wordEnd < text.Length - 1 &&
                                               char.IsLetterOrDigit(text[wordEnd + 1]);

                        if (!isPeriodWithinWord)
                            break;
                    }

                    wordEnd++;
                }

                // Skip any whitespace after the word
                while (wordEnd < text.Length && char.IsWhiteSpace(text[wordEnd]))
                {
                    wordEnd++;
                }

                LogDebug($"CRITICAL: Avoiding word/period break! Moving from position {position} to word end at {wordEnd}");
                position = wordEnd; // Update position to use this safe location for later checks
            }

            // Also check if the previous character is part of a word
            // This handles cases where position is between words or at the start of a word
            bool prevInWord = position > 0 &&
                           !char.IsWhiteSpace(text[position - 1]) &&
                           !IsPunctuation(text[position - 1]);

            // Check for cases like "word ... word" where we might be at the ellipsis
            bool atEllipsis = position > 0 && position < text.Length - 1 &&
                           text[position] == '.' &&
                           (position > 1 && text[position - 1] == '.' && text[position - 2] == '.') ||
                           (position < text.Length - 2 && text[position + 1] == '.' && text[position + 2] == '.');

            // If we're at the beginning of a word or near an ellipsis, we need to adjust
            if (prevInWord || atEllipsis)
            {
                // Handle nearby ellipsis specially
                if (atEllipsis)
                {
                    // If we're in the middle of ellipsis, move past it
                    int ellipsisEnd = position;
                    while (ellipsisEnd < text.Length && text[ellipsisEnd] == '.')
                    {
                        ellipsisEnd++;
                    }

                    // Skip any whitespace after the ellipsis
                    while (ellipsisEnd < text.Length && char.IsWhiteSpace(text[ellipsisEnd]))
                    {
                        ellipsisEnd++;
                    }

                    LogDebug($"CRITICAL: At ellipsis! Moving from position {position} to ellipsis end at {ellipsisEnd}");
                    position = ellipsisEnd;
                }
                else
                {
                    // Find the end of the current word - NEVER split a word
                    int wordEnd = position;
                    while (wordEnd < text.Length &&
                           !char.IsWhiteSpace(text[wordEnd]) &&
                           !IsPunctuation(text[wordEnd]))
                    {
                        wordEnd++;
                    }

                    // Skip whitespace after the word
                    while (wordEnd < text.Length && char.IsWhiteSpace(text[wordEnd]))
                    {
                        wordEnd++;
                    }

                    LogDebug($"CRITICAL: Previous char in word! Moving from position {position} to word end at {wordEnd}");
                    position = wordEnd;
                }
            }

            // Immediate check: if we're already at a whitespace, use the next position
            if (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                // Skip all consecutive whitespace
                int wsEnd = position;
                while (wsEnd < text.Length && char.IsWhiteSpace(text[wsEnd])) wsEnd++;
                position = wsEnd;
            }

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
                    bool isEllipsis = (i > 0 && text[i - 1] == '.') || (i < text.Length - 1 && text[i + 1] == '.');
                    bool isRealSentenceEnd = !isEllipsis && (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1]));

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

            // 2. Komma oder Semikolon hat zweithöchste Priorität
            for (int i = position; i < lookaheadLimit; i++)
            {
                if (i < text.Length && (text[i] == ',' || text[i] == ';' || text[i] == ':'))
                {
                    // Prüfe, ob es kein Komma in einer Zahl ist (z.B. 1,000)
                    bool isNumberSeparator = text[i] == ',' &&
                                           i > 0 && i < text.Length - 1 &&
                                           char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]);

                    if (!isNumberSeparator)
                    {
                        // Setze die Position direkt hinter das Komma/Semikolon
                        int newPos = i + 1;
                        // Überspringe Leerzeichen nach dem Komma
                        while (newPos < text.Length && char.IsWhiteSpace(text[newPos])) newPos++;

                        LogDebug($"LOOKAHEAD: Found comma/semicolon at position {i}, moving to {newPos}");
                        return newPos;
                    }
                }
            }

            // 3. Klammern und andere Satzzeichen
            char[] punctuationChars = { ')', ']', '}', '"', '"', '\'', '…' };
            for (int i = position; i < lookaheadLimit; i++)
            {
                if (i < text.Length && Array.IndexOf(punctuationChars, text[i]) >= 0)
                {
                    // Setze die Position direkt hinter die Klammer/das Satzzeichen
                    int newPos = i + 1;
                    // Überspringe Leerzeichen nach dem Satzzeichen
                    while (newPos < text.Length && char.IsWhiteSpace(text[newPos])) newPos++;

                    LogDebug($"LOOKAHEAD: Found punctuation at position {i}, moving to {newPos}");
                    return newPos;
                }
            }

            // 4. Leerzeichen zwischen Wörtern - hier suchen wir nach einem Leerzeichen,
            // das auf ein komplettes Wort folgt
            for (int i = position; i < lookaheadLimit; i++)
            {
                if (i < text.Length && char.IsWhiteSpace(text[i]) &&
                    i > 0 && !char.IsWhiteSpace(text[i - 1]))
                {
                    // Setze die Position nach dem Leerzeichen und alle folgenden
                    int newPos = i;
                    while (newPos < text.Length && char.IsWhiteSpace(text[newPos])) newPos++;

                    LogDebug($"LOOKAHEAD: Found word boundary at position {i}, moving to {newPos}");
                    return newPos;
                }
            }

            // Wenn wir bis hierhin kommen, dann haben wir trotz des Lookaheads 
            // keine bessere Position gefunden. Wir verwenden die bereits angepasste Position.
            LogDebug($"LOOKAHEAD: No better position found, using adjusted position {position}");
            return position;
        }

        /// <summary>
        /// Determines if a character is punctuation, more broadly than char.IsPunctuation
        /// </summary>
        private bool IsPunctuation(char c)
        {
            if (char.IsPunctuation(c)) return true;

            // Additional punctuation-like characters
            return c == '—' || c == '–' || c == '-' || c == '…';
        }

        /// <summary>
        /// Logs debug information if debug logging is enabled.
        /// </summary>
        /// <param name="message">The message to log.</param>
        private void LogDebug(string message)
        {
            if (_enableDebugLogging)
            {
                Console.WriteLine($"[DEBUG] ProgressiveTTSSynthesizer: {message}");
            }
        }

        /// <summary>
        /// Enhanced debug logging specifically for the chunking algorithm.
        /// This provides more detailed information about the chunking process.
        /// </summary>
        private void LogChunkingDebug(string message, bool forceOutput = false)
        {
            if (_enableDebugLogging || forceOutput)
            {
                Console.WriteLine($"[CHUNKING-DEBUG] {message}");
            }
        }
    }
}