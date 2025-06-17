using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        private readonly ISynthesizer _synthesizer;
        private readonly IWebSocketHandler _webSocketHandler; // To send events and audio

        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int BitsPerSample = 16;

        public AudioSegmentProcessor(
            ILogger<AudioSegmentProcessor> logger,
            IRecognizer recognizer,
            IChatService chatService,
            ChatLogManager chatLogManager,
            ISynthesizer synthesizer,
            IWebSocketHandler webSocketHandler)
        {
            _logger = logger;
            _recognizer = recognizer;
            _chatService = chatService;
            _chatLogManager = chatLogManager;
            _synthesizer = synthesizer;
            _webSocketHandler = webSocketHandler;
        }

        public async Task ProcessSegmentAsync(byte[] audioBytes, WebSocket webSocket, string sessionId, PipelineOptions pipelineOptions, VadSettings vadSettings) // Added pipelineOptions and vadSettings
        {
            var segmentProcessingStopwatch = Stopwatch.StartNew();
            long transcriptionTimeMs = 0;
            long llmTimeMs = 0;
            string reply = string.Empty;

            double durationSec = (double)audioBytes.Length / (SampleRate * Channels * BitsPerSample / 8);
            _logger.LogDebug("Session {SessionId}: Processing audio segment - {Bytes} bytes, Duration: {Duration:F3}s in AudioSegmentProcessor", sessionId, audioBytes.Length, durationSec);

            if (durationSec < vadSettings.MinSegmentDurationSec) // Use vadSettings from parameter
            {
                _logger.LogDebug("Session {SessionId}: Segment discarded - Duration {Duration:F3}s < Min {MinSec:F3}s", sessionId, durationSec, vadSettings.MinSegmentDurationSec);
                segmentProcessingStopwatch.Stop();
                // Optionally send an event indicating segment was too short
                // await _webSocketHandler.SendEventAsync(webSocket, "segment_too_short", new { duration = durationSec, minDuration = vadSettings.MinSegmentDurationSec });
                return;
            }

            try
            {
                _logger.LogDebug("Session {SessionId}: Starting segment processing pipeline", sessionId);

                var transcriptionStopwatch = Stopwatch.StartNew();
                MemoryStream audioMemoryStream = PrepareAudioStreamForTranscription(audioBytes);
                string prompt = await GetTranscriptionAsync(audioMemoryStream, pipelineOptions.Language, sessionId); // Pass language and sessionId
                transcriptionStopwatch.Stop();
                transcriptionTimeMs = transcriptionStopwatch.ElapsedMilliseconds;

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    _logger.LogDebug("Session {SessionId}: Empty transcription, skipping LLM and TTS processing", sessionId);
                    await _webSocketHandler.SendEventAsync(webSocket, "done", new { reason = "Empty transcription, no action taken" });
                    segmentProcessingStopwatch.Stop();
                    _logger.LogDebug("Session {SessionId}: Empty segment processing completed in {TotalTimeMs}ms", sessionId, segmentProcessingStopwatch.ElapsedMilliseconds);
                    return;
                }

                _chatLogManager.AddMessage(ChatRole.User, prompt);
                await _webSocketHandler.SendEventAsync(webSocket, "prompt", new { prompt });

                var llmProcessingStopwatch = Stopwatch.StartNew();
                // Updated condition: no longer checks for StreamingOpenAIChatService type explicitly.
                // Relies on the IChatService implementation to handle streaming appropriately via StreamResponseAsync.
                if (!pipelineOptions.DisableTokenStreaming)
                {
                    // Pass the _chatService instance (which is IChatService) directly.
                    reply = await HandleStreamingChatResponseAsync(webSocket, _chatService, prompt, pipelineOptions, sessionId);
                }
                else
                {
                    reply = await HandleNonStreamingChatResponseAsync(webSocket, prompt, pipelineOptions, sessionId);
                }
                llmProcessingStopwatch.Stop();
                llmTimeMs = llmProcessingStopwatch.ElapsedMilliseconds;

                segmentProcessingStopwatch.Stop();
                long totalProcessingTimeMs = segmentProcessingStopwatch.ElapsedMilliseconds;

                await LogAndSendFinalEventsAsync(webSocket, reply, transcriptionTimeMs, llmTimeMs, totalProcessingTimeMs, pipelineOptions, sessionId);
            }
            catch (Exception ex)
            {
                segmentProcessingStopwatch.Stop();
                _logger.LogError(ex, "Session {SessionId}: Error processing segment. Total time before error: {TotalTimeMs}ms", sessionId, segmentProcessingStopwatch.ElapsedMilliseconds);
                await _webSocketHandler.SendEventAsync(webSocket, "error", new { error = ex.Message });
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

        // Changed signature to accept IChatService
        private async Task<string> HandleStreamingChatResponseAsync(WebSocket webSocket, IChatService chatService, string prompt, PipelineOptions pipelineOptions, string sessionId)
        {
            var voice = pipelineOptions.TtsVoice;
            var accumulatedTextForTts = new StringBuilder();
            string fullReply = string.Empty;
            int ttsChunkIndex = 0;
            var sentenceDelimiters = new char[] { '.', '!', '?' };

            // Use chatService.StreamResponseAsync which returns IAsyncEnumerable<(string token, bool isFinalToken)>
            await foreach (var (token, isFinalToken) in chatService.StreamResponseAsync(_chatLogManager.GetMessages(), pipelineOptions.ChatModel.ToString()))
            {
                if (!string.IsNullOrEmpty(token))
                {
                    accumulatedTextForTts.Append(token);
                    fullReply += token;
                    await _webSocketHandler.SendEventAsync(webSocket, "token", new { token });
                }

                if (!pipelineOptions.DisableTts && !pipelineOptions.DisableProgressiveTts)
                {
                    bool flushNow = false;
                    string textToSynthesize = null;

                    // If it's the final token and there's accumulated text, flush it all.
                    if (isFinalToken && accumulatedTextForTts.Length > 0)
                    {
                        textToSynthesize = accumulatedTextForTts.ToString();
                        accumulatedTextForTts.Clear();
                        flushNow = true;
                    }
                    // Otherwise, check for sentence delimiters or buffer length for intermediate flushing.
                    else if (accumulatedTextForTts.Length > 0)
                    {
                        int lastDelimiter = -1;
                        // Check for delimiters only if not the final token, to avoid splitting the very last part unnecessarily if it doesn't end with a delimiter.
                        if (!isFinalToken) 
                        {
                             lastDelimiter = accumulatedTextForTts.ToString().LastIndexOfAny(sentenceDelimiters);
                        }
                        
                        const int forceFlushLength = 150; 

                        if (lastDelimiter != -1)
                        {
                            textToSynthesize = accumulatedTextForTts.ToString().Substring(0, lastDelimiter + 1);
                            accumulatedTextForTts.Remove(0, lastDelimiter + 1);
                            flushNow = true;
                        }
                        else if (accumulatedTextForTts.Length >= forceFlushLength && !isFinalToken) // Avoid force flush if it's the final token, as it will be flushed anyway
                        {
                            textToSynthesize = accumulatedTextForTts.ToString();
                            accumulatedTextForTts.Clear();
                            flushNow = true;
                        }
                    }

                    if (flushNow && !string.IsNullOrWhiteSpace(textToSynthesize))
                    {
                        _logger.LogDebug("Session {SessionId}: TTS (Progressive) - Sending segment (Length {Length}): \"{SegmentText}\" (IsFinalToken Trigger: {IsFinal})", sessionId, textToSynthesize.Length, textToSynthesize, isFinalToken && textToSynthesize == fullReply.Substring(fullReply.Length - textToSynthesize.Length));
                        await _synthesizer.ChunkedSynthesisAsync(textToSynthesize, voice, async (audioBytesChunk) =>
                        {
                            if (audioBytesChunk != null && audioBytesChunk.Length > 0)
                            {
                                await _webSocketHandler.SendAudioChunkAsync(webSocket, audioBytesChunk, ttsChunkIndex, sessionId);
                                ttsChunkIndex++;
                            }
                        });
                    }
                }
            }
            
            // Removed final flush after loop; isFinalToken logic should handle the last segment.
            // Ensure any remaining text in accumulatedTextForTts (e.g. if stream ends abruptly without isFinalToken=true on last content) is handled.
            // However, a well-behaved StreamResponseAsync should ensure the last content token has isFinalToken=true.
            if (accumulatedTextForTts.Length > 0 && !pipelineOptions.DisableTts && !pipelineOptions.DisableProgressiveTts)
            {
                string finalTextToSynthesize = accumulatedTextForTts.ToString();
                _logger.LogWarning("Session {SessionId}: TTS (Progressive) - Flushing remaining text after loop (Length {Length}): \"{SegmentText}\". This might indicate an unexpected stream end.", sessionId, finalTextToSynthesize.Length, finalTextToSynthesize);
                await _synthesizer.ChunkedSynthesisAsync(finalTextToSynthesize, voice, async (audioBytesChunk) =>
                {
                    if (audioBytesChunk != null && audioBytesChunk.Length > 0)
                    {
                        await _webSocketHandler.SendAudioChunkAsync(webSocket, audioBytesChunk, ttsChunkIndex, sessionId);
                        ttsChunkIndex++;
                    }
                });
            }

            _chatLogManager.AddMessage(ChatRole.Bot, fullReply);
            return fullReply;
        }

        private async Task<string> HandleNonStreamingChatResponseAsync(WebSocket webSocket, string prompt, PipelineOptions pipelineOptions, string sessionId)
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
                    await _webSocketHandler.SendAudioChunkAsync(webSocket, ttsAudioBytes, 0, sessionId);
                }
                else
                {
                    _logger.LogWarning("Session {SessionId}: TTS (Non-Progressive) - Synthesizer returned null or empty for reply: \"{ReplyText}\"", sessionId, reply);
                }
            }
            return reply;
        }

        private async Task LogAndSendFinalEventsAsync(WebSocket webSocket, string reply, long transcriptionTimeMs, long llmTimeMs, long totalTimeMs, PipelineOptions pipelineOptions, string sessionId)
        {
            var latencyInfo = new
            {
                transcriptionTime = transcriptionTimeMs,
                llmTime = llmTimeMs,
                totalTime = totalTimeMs
            };
            await _webSocketHandler.SendEventAsync(webSocket, "reply", new { reply, latency_info = latencyInfo });

            if (!pipelineOptions.DisableTts)
            {
                await _webSocketHandler.SendEventAsync(webSocket, "audio-done", null);
            }
            await _webSocketHandler.SendEventAsync(webSocket, "done", null);
            _logger.LogInformation("Session {SessionId}: Interaction completed - Final reply sent. Latency (ms): Trans={TransTime}, LLM={LlmTime}, Total={TotalTime}",
                sessionId, transcriptionTimeMs, llmTimeMs, totalTimeMs);
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
            writer.Write(dataLength);
            return ms.ToArray();
        }
    }
}
