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
        private readonly ISynthesizer _synthesizer;        public event Func<string, string, Task> OnTranscriptionReady;
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
            long transcriptionTimeMs = 0;
            long llmTimeMs = 0;
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

                var llmProcessingStopwatch = Stopwatch.StartNew();
                if (!pipelineOptions.DisableTokenStreaming)
                {
                    reply = await HandleStreamingChatResponseAsync(prompt, pipelineOptions, sessionId);
                }
                else
                {
                    reply = await HandleNonStreamingChatResponseAsync(prompt, pipelineOptions, sessionId);
                }
                llmProcessingStopwatch.Stop();
                llmTimeMs = llmProcessingStopwatch.ElapsedMilliseconds;

                segmentProcessingStopwatch.Stop();
                long totalProcessingTimeMs = segmentProcessingStopwatch.ElapsedMilliseconds;

                LogAndSendFinalEvents(reply, transcriptionTimeMs, llmTimeMs, totalProcessingTimeMs, pipelineOptions, sessionId);
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

        private async Task<string> HandleStreamingChatResponseAsync(string prompt, PipelineOptions pipelineOptions, string sessionId)
        {
            var voice = pipelineOptions.TtsVoice;
            var accumulatedTextForTts = new StringBuilder();
            string fullReply = string.Empty;
            int ttsChunkIndex = 0;
            var sentenceDelimiters = new char[] { '.', '!', '?' };            await foreach (var (token, isFinalToken) in _chatService.StreamResponseAsync(_chatLogManager.GetMessages(), pipelineOptions.ChatModel.ToString()))
            {
                if (!string.IsNullOrEmpty(token))
                {
                    accumulatedTextForTts.Append(token);
                    fullReply += token;
                    
                    // Send token to frontend if token streaming is enabled
                    if (OnTokenReady != null) await OnTokenReady.Invoke(token, sessionId);
                }

                if (!pipelineOptions.DisableTts && !pipelineOptions.DisableProgressiveTts)
                {
                    bool flushNow = false;
                    string textToSynthesize = null;

                    if (isFinalToken && accumulatedTextForTts.Length > 0)
                    {
                        textToSynthesize = accumulatedTextForTts.ToString();
                        accumulatedTextForTts.Clear();
                        flushNow = true;
                    }
                    else if (accumulatedTextForTts.Length > 0)
                    {
                        int lastDelimiter = -1;
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
                        else if (accumulatedTextForTts.Length >= forceFlushLength && !isFinalToken)
                        {
                            textToSynthesize = accumulatedTextForTts.ToString();
                            accumulatedTextForTts.Clear();
                            flushNow = true;
                        }
                    }

                    if (flushNow && !string.IsNullOrWhiteSpace(textToSynthesize))
                    {
                        _logger.LogDebug("Session {SessionId}: TTS (Progressive) - Sending segment (Length {Length}): \"{SegmentText}\"", sessionId, textToSynthesize.Length, textToSynthesize);
                        await _synthesizer.ChunkedSynthesisAsync(textToSynthesize, voice, async (audioBytesChunk) =>
                        {
                            if (audioBytesChunk != null && audioBytesChunk.Length > 0)
                            {
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
        }        private async void LogAndSendFinalEvents(string reply, long transcriptionTimeMs, long llmTimeMs, long totalTimeMs, PipelineOptions pipelineOptions, string sessionId)
        {
            var performanceMetrics = new
            {
                transcription_latency_ms = transcriptionTimeMs,
                llm_latency_ms = llmTimeMs,
                total_latency_ms = totalTimeMs,
                full_reply = reply
            };

            // Send done event to frontend
            if (OnDone != null) await OnDone.Invoke(sessionId, performanceMetrics, sessionId);

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
