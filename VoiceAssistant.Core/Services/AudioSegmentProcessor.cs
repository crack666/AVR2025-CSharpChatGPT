using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant.Core.Services
{
    public class AudioSegmentProcessor : IAudioSegmentProcessor
    {
        private readonly ILogger<AudioSegmentProcessor> _logger;
        private readonly IRecognizer _recognizer;
        private readonly IChatService _chatService;
        private readonly ChatLogManager _chatLogManager;
        private readonly ISynthesizer _synthesizer; public event Func<string, string, Task> OnTranscriptionReady;
        public event Func<string, string, Task> OnTokenReady;
        public event Func<byte[], int, string, Task> OnAudioChunkReady;
        public event Func<string, string, Task> OnError;
        public event Func<string, object, string, Task> OnDone;

        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int BitsPerSample = 16;

        public AudioSegmentProcessor(
            ILogger<AudioSegmentProcessor> logger,
            IRecognizer recognizer,
            IChatService chatService,
            ChatLogManager chatLogManager,
            ISynthesizer synthesizer)
        {
            _logger = logger;
            _recognizer = recognizer;
            _chatService = chatService;
            _chatLogManager = chatLogManager;
            _synthesizer = synthesizer;
        }
        public async Task ProcessSegmentAsync(byte[] audioBytes, string sessionId, PipelineOptions pipelineOptions, VadSettings vadSettings)
        {
            var segmentProcessingStopwatch = Stopwatch.StartNew();
            var latencyTracker = new LatencyTracker();
            long transcriptionTimeMs = 0;
            string reply = string.Empty;

            double durationSec = (double)audioBytes.Length / (SampleRate * Channels * BitsPerSample / 8);
            _logger.LogDebug("Session {SessionId}: Processing audio segment - {Bytes} bytes, Duration: {Duration:F3}s in AudioSegmentProcessor", sessionId, audioBytes.Length, durationSec);

            if (durationSec < vadSettings.MinSegmentDurationSec)
            {
                _logger.LogDebug("Session {SessionId}: Segment discarded - Duration {Duration:F3}s < Min {MinSec:F3}s", sessionId, durationSec, vadSettings.MinSegmentDurationSec);
                segmentProcessingStopwatch.Stop();
                return;
            }

            try
            {
                _logger.LogDebug("Session {SessionId}: Starting segment processing pipeline", sessionId);

                var transcriptionStopwatch = Stopwatch.StartNew();
                MemoryStream audioMemoryStream = PrepareAudioStreamForTranscription(audioBytes);
                string prompt = await GetTranscriptionAsync(audioMemoryStream, pipelineOptions.Language, sessionId);
                transcriptionStopwatch.Stop();
                transcriptionTimeMs = transcriptionStopwatch.ElapsedMilliseconds;

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    _logger.LogDebug("Session {SessionId}: Empty transcription, skipping LLM and TTS processing", sessionId);
                    segmentProcessingStopwatch.Stop();
                    _logger.LogDebug("Session {SessionId}: Empty segment processing completed in {TotalTimeMs}ms", sessionId, segmentProcessingStopwatch.ElapsedMilliseconds);
                    return;
                }

                _chatLogManager.AddMessage(ChatRole.User, prompt);
                if (OnTranscriptionReady != null) await OnTranscriptionReady.Invoke(sessionId, prompt);

                // Start LLM processing and measure first response times
                if (!pipelineOptions.DisableTokenStreaming)
                {
                    reply = await HandleStreamingChatResponseAsync(prompt, pipelineOptions, sessionId, segmentProcessingStopwatch, latencyTracker);
                }
                else
                {
                    reply = await HandleNonStreamingChatResponseAsync(prompt, pipelineOptions, sessionId);
                    // For non-streaming, consider the full response as "first token time"
                    latencyTracker.TimeToFirstTokenMs = segmentProcessingStopwatch.ElapsedMilliseconds;
                }

                segmentProcessingStopwatch.Stop();
                long totalProcessingTimeMs = segmentProcessingStopwatch.ElapsedMilliseconds;

                LogAndSendFinalEvents(reply, transcriptionTimeMs, latencyTracker, totalProcessingTimeMs, pipelineOptions, sessionId);
            }
            catch (Exception ex)
            {
                segmentProcessingStopwatch.Stop();
                _logger.LogError(ex, "Session {SessionId}: Error processing segment. Total time before error: {TotalTimeMs}ms", sessionId, segmentProcessingStopwatch.ElapsedMilliseconds);
                if (OnError != null) await OnError.Invoke(sessionId, ex.Message);
            }
        }

        private MemoryStream PrepareAudioStreamForTranscription(byte[] audioBytes)
        {
            var ms = new MemoryStream();
            var header = CreateWavHeader(audioBytes.Length);
            ms.Write(header, 0, header.Length);
            ms.Write(audioBytes, 0, audioBytes.Length);
            ms.Position = 0;
            return ms;
        }

        private async Task<string> GetTranscriptionAsync(MemoryStream audioMemoryStream, string language, string sessionId)
        {
            _logger.LogDebug("Session {SessionId}: Recognizing audio with language '{Language}'", sessionId, language);
            string prompt = await _recognizer.RecognizeAsync(audioMemoryStream, language, "audio/wav", "segment.wav");
            _logger.LogInformation("Session {SessionId}: Transcription complete: '{Prompt}' (Length: {Length})", sessionId, prompt, prompt.Length);
            return prompt;
        }
        private async Task<string> HandleStreamingChatResponseAsync(string prompt, PipelineOptions pipelineOptions, string sessionId,
            Stopwatch mainStopwatch, LatencyTracker latencyTracker)
        {
            var voice = pipelineOptions.TtsVoice;
            var accumulatedTextForTts = new StringBuilder();
            string fullReply = string.Empty;
            int ttsChunkIndex = 0;

            await foreach (var (token, isFinalToken) in _chatService.StreamResponseAsync(_chatLogManager.GetMessages(), pipelineOptions.ChatModel.ToString()))
            {
                if (!string.IsNullOrEmpty(token))
                {
                    accumulatedTextForTts.Append(token);
                    fullReply += token;

                    // Measure time to first token
                    if (!latencyTracker.FirstTokenSent)
                    {
                        latencyTracker.TimeToFirstTokenMs = mainStopwatch.ElapsedMilliseconds;
                        latencyTracker.FirstTokenSent = true;
                        _logger.LogDebug("Session {SessionId}: First token sent at {TimeMs}ms", sessionId, latencyTracker.TimeToFirstTokenMs);
                    }

                    // Send token to frontend if token streaming is enabled
                    if (OnTokenReady != null) await OnTokenReady.Invoke(token, sessionId);
                }
                if (!pipelineOptions.DisableTts && !pipelineOptions.DisableProgressiveTts)
                {
                    bool flushNow = false;
                    string textToSynthesize = null;

                    if (isFinalToken && accumulatedTextForTts.Length > 0)
                    {
                        // Final token: flush everything remaining
                        textToSynthesize = accumulatedTextForTts.ToString();
                        accumulatedTextForTts.Clear();
                        flushNow = true;
                    }
                    else if (accumulatedTextForTts.Length > 0 && !isFinalToken)
                    {
                        // Progressive processing: check for sentence boundaries
                        string currentText = accumulatedTextForTts.ToString();
                        int safeSplitPosition = FindSafeSplitPosition(currentText);

                        if (safeSplitPosition != -1)
                        {
                            // Found a safe sentence boundary
                            textToSynthesize = currentText.Substring(0, safeSplitPosition + 1);
                            accumulatedTextForTts.Remove(0, safeSplitPosition + 1);
                            flushNow = true;

                            // Log the decision
                            _logger.LogDebug("Session {SessionId}: TTS split at safe boundary, chunk: '{ChunkPreview}' (Length: {Length})",
                                sessionId, textToSynthesize.Length > 50 ? textToSynthesize.Substring(0, 47) + "..." : textToSynthesize, textToSynthesize.Length);
                        }
                        else if (currentText.Length > 200)
                        {
                            // Safety valve: if text gets too long without sentence boundary, flush anyway
                            // This prevents memory issues with very long sentences
                            textToSynthesize = currentText;
                            accumulatedTextForTts.Clear();
                            flushNow = true;

                            _logger.LogWarning("Session {SessionId}: TTS force-flush due to length ({Length} chars), no sentence boundary found",
                                sessionId, currentText.Length);
                        }
                        // Otherwise: continue accumulating until we find a proper sentence boundary
                    }
                    if (flushNow && !string.IsNullOrWhiteSpace(textToSynthesize))
                    {
                        _logger.LogDebug("Session {SessionId}: TTS (Progressive) - Sending segment (Length {Length}): \"{SegmentText}\"", sessionId, textToSynthesize.Length, textToSynthesize);
                        await _synthesizer.ChunkedSynthesisAsync(textToSynthesize, voice, async (audioBytesChunk) =>
                        {
                            if (audioBytesChunk != null && audioBytesChunk.Length > 0)
                            {
                                // Measure time to first audio chunk
                                if (!latencyTracker.FirstAudioChunkSent)
                                {
                                    latencyTracker.TimeToFirstAudioChunkMs = mainStopwatch.ElapsedMilliseconds;
                                    latencyTracker.FirstAudioChunkSent = true;
                                    _logger.LogDebug("Session {SessionId}: First audio chunk sent at {TimeMs}ms", sessionId, latencyTracker.TimeToFirstAudioChunkMs);
                                }

                                if (OnAudioChunkReady != null) await OnAudioChunkReady.Invoke(audioBytesChunk, ttsChunkIndex, sessionId);
                                ttsChunkIndex++;
                            }
                        });
                    }
                }
            }
            if (accumulatedTextForTts.Length > 0 && !pipelineOptions.DisableTts && !pipelineOptions.DisableProgressiveTts)
            {
                string finalTextToSynthesize = accumulatedTextForTts.ToString();
                _logger.LogWarning("Session {SessionId}: TTS (Progressive) - Flushing remaining text after loop (Length {Length}): \"{SegmentText}\".", sessionId, finalTextToSynthesize.Length, finalTextToSynthesize);
                await _synthesizer.ChunkedSynthesisAsync(finalTextToSynthesize, voice, async (audioBytesChunk) =>
                {
                    if (audioBytesChunk != null && audioBytesChunk.Length > 0)
                    {
                        // Measure time to first audio chunk (fallback for final flush)
                        if (!latencyTracker.FirstAudioChunkSent)
                        {
                            latencyTracker.TimeToFirstAudioChunkMs = mainStopwatch.ElapsedMilliseconds;
                            latencyTracker.FirstAudioChunkSent = true;
                            _logger.LogDebug("Session {SessionId}: First audio chunk sent at {TimeMs}ms (final flush)", sessionId, latencyTracker.TimeToFirstAudioChunkMs);
                        }

                        if (OnAudioChunkReady != null) await OnAudioChunkReady.Invoke(audioBytesChunk, ttsChunkIndex, sessionId);
                        ttsChunkIndex++;
                    }
                });
            }

            _chatLogManager.AddMessage(ChatRole.Bot, fullReply);
            return fullReply;
        }

        private async Task<string> HandleNonStreamingChatResponseAsync(string prompt, PipelineOptions pipelineOptions, string sessionId)
        {
            string reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages(), pipelineOptions.ChatModel.ToString());
            _chatLogManager.AddMessage(ChatRole.Bot, reply);
            _logger.LogInformation("Session {SessionId}: Non-streaming chat response: '{Reply}'", sessionId, reply);

            if (!pipelineOptions.DisableTts)
            {
                _logger.LogDebug("Session {SessionId}: TTS (Non-Progressive) - Synthesizing full reply (Length {Length}): \"{ReplyText}\"", sessionId, reply.Length, reply);
                var ttsAudioBytes = await _synthesizer.SynthesizeAsync(reply, pipelineOptions.TtsVoice);
                if (ttsAudioBytes != null && ttsAudioBytes.Length > 0)
                {
                    if (OnAudioChunkReady != null) await OnAudioChunkReady.Invoke(ttsAudioBytes, 0, sessionId);
                }
                else
                {
                    _logger.LogWarning("Session {SessionId}: TTS (Non-Progressive) - Synthesizer returned null or empty for reply: \"{ReplyText}\"", sessionId, reply);
                }
            }
            return reply;
        }
        private async void LogAndSendFinalEvents(string reply, long transcriptionTimeMs, LatencyTracker latencyTracker, long totalTimeMs, PipelineOptions pipelineOptions, string sessionId)
        {
            var performanceMetrics = new
            {
                textLatency = latencyTracker.TimeToFirstTokenMs > 0 ? latencyTracker.TimeToFirstTokenMs : transcriptionTimeMs,
                audioLatency = latencyTracker.TimeToFirstAudioChunkMs > 0 ? latencyTracker.TimeToFirstAudioChunkMs : -1,
                total = totalTimeMs,
                // Legacy field names for compatibility
                transcription_latency_ms = transcriptionTimeMs,
                llm_latency_ms = latencyTracker.TimeToFirstTokenMs,
                total_latency_ms = totalTimeMs,
                full_reply = reply
            };

            // Send done event to frontend
            if (OnDone != null) await OnDone.Invoke(sessionId, performanceMetrics, sessionId);

            _logger.LogInformation("Session {SessionId}: Interaction completed - Final reply sent. Latency (ms): FirstToken={FirstTokenTime}, FirstAudio={FirstAudioTime}, Total={TotalTime}",
                sessionId, latencyTracker.TimeToFirstTokenMs, latencyTracker.TimeToFirstAudioChunkMs, totalTimeMs);
        }

        private byte[] CreateWavHeader(int dataLength)
        {
            int byteRate = SampleRate * Channels * BitsPerSample / 8;
            short blockAlign = (short)(Channels * BitsPerSample / 8);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)BitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength); return ms.ToArray();
        }        /// <summary>
                 /// Finds the best position to split text for TTS, avoiding common false positives.
                 /// Prefers longer, complete sentences to avoid tiny audio chunks.
                 /// Returns -1 if no suitable split position is found.
                 /// </summary>
                 /// <param name="text">The text to find a split position in</param>
                 /// <returns>Position of last character to include in the split (like LastIndexOfAny), or -1 if none found</returns>
        private int FindSafeSplitPosition(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;

            // Build the same robust pattern as in ProgressiveTTSSynthesizer
            string pattern = @"(?<=[.!?])"; // Match after sentence ending punctuation
            pattern += @"(?:\s+(?=[A-ZÄÖÜ])|$)"; // Followed by whitespace+capital or end of string
            pattern += @"(?<!(?:\b(?:"; // Negative lookbehind - NOT preceded by abbreviations
            pattern += @"z\.B|u\.a|d\.h|m\.E|z\.T|z\.Z|"; // German abbreviations part 1
            pattern += @"ggf|evtl|etc|usw|vgl|bzw|"; // German abbreviations part 2
            pattern += @"Dr|Prof|Hr|Fr|Mr|Mrs|Ms|"; // Titles
            pattern += @"ca|inkl|exkl|max|min"; // Other abbreviations
            pattern += @")\.)|(?:\b\d{1,2}\.)|(?:\d\.\d))"; // Close abbreviations, ordinal numbers, decimals

            var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);

            if (matches.Count > 0)
            {
                // Find the best split position - prefer later matches for longer chunks
                // But avoid splitting if the result would be too short
                for (int i = matches.Count - 1; i >= 0; i--)
                {
                    var match = matches[i];
                    // Find the punctuation character before this position
                    for (int j = match.Index - 1; j >= 0; j--)
                    {
                        if (text[j] == '.' || text[j] == '!' || text[j] == '?')
                        {
                            // Check if this would create a reasonable chunk
                            string potentialChunk = text.Substring(0, j + 1).Trim();

                            // Prefer chunks that form complete thoughts (at least some minimum reasonable length)
                            // But don't be too restrictive - let natural sentence boundaries guide us
                            if (potentialChunk.Length >= 20) // Very conservative minimum for meaningful sentences
                            {
                                return j; // Return position of the punctuation mark
                            }
                        }
                    }
                }
            }

            return -1; // No suitable split position found
        }

        // Helper class for tracking first-response latencies
        private class LatencyTracker
        {
            public long TimeToFirstTokenMs { get; set; } = -1;
            public long TimeToFirstAudioChunkMs { get; set; } = -1;
            public bool FirstTokenSent { get; set; } = false;
            public bool FirstAudioChunkSent { get; set; } = false;
        }
    }
}
