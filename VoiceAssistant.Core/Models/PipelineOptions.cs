using System;

namespace VoiceAssistant.Core.Models
{
    /// <summary>
    /// Feature flags for selecting pipeline modes and optimizations.
    /// </summary>
    public class PipelineOptions
    {        /// <summary>Use the legacy HTTP-based endpoints instead of WebSocket streaming.</summary>
        public bool UseLegacyHttp { get; set; }
        /// <summary>Disable voice activity detection (process full audio segments).</summary>
        public bool DisableVad { get; set; }
        /// <summary>Use OpenAI Realtime API with built-in VAD instead of local WebRTC VAD.</summary>
        public bool UseOpenAIRealtimeVad { get; set; }
        /// <summary>Disable token-level streaming for chat responses.</summary>
        public bool DisableTokenStreaming { get; set; }
        /// <summary>Disable progressive (chunked) TTS; use single-shot synthesis.</summary>
        public bool DisableProgressiveTts { get; set; }
        /// <summary>Disable Text-to-Speech output entirely.</summary>
        public bool DisableTts { get; set; }
        /// <summary>Chat model identifier (e.g., "gpt-3.5-turbo").</summary>
        public string ChatModel { get; set; } = "gpt-3.5-turbo";
        /// <summary>TTS voice identifier (e.g., "nova").</summary>
        public string TtsVoice { get; set; } = "nova";

        /// <summary>Language for speech recognition (e.g., "en").</summary>
        public string Language { get; set; } = "en";

        /// <summary>Minimum length for the first TTS chunk to trigger synthesis.</summary>
        public int TtsMinFirstChunkLength { get; set; } = 50; // Default value, can be tuned

        /// <summary>Maximum length for the first TTS chunk.</summary>
        public int TtsMaxFirstChunkLength { get; set; } = 100; // Default value, can be tuned

        /// <summary>Target length for subsequent TTS chunks.</summary>
        public int TtsSubsequentChunkLength { get; set; } = 250; // Default value, can be tuned

        public void CopyFrom(PipelineOptions other)
        {
            if (other == null) return;            UseLegacyHttp = other.UseLegacyHttp;
            DisableVad = other.DisableVad;
            UseOpenAIRealtimeVad = other.UseOpenAIRealtimeVad;
            DisableTokenStreaming = other.DisableTokenStreaming;
            DisableProgressiveTts = other.DisableProgressiveTts;
            DisableTts = other.DisableTts;
            ChatModel = other.ChatModel;
            TtsVoice = other.TtsVoice;
            Language = other.Language;
            TtsMinFirstChunkLength = other.TtsMinFirstChunkLength;
            TtsMaxFirstChunkLength = other.TtsMaxFirstChunkLength;
            TtsSubsequentChunkLength = other.TtsSubsequentChunkLength;
        }

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
            {
                return false;
            }

            PipelineOptions other = (PipelineOptions)obj;
            return UseLegacyHttp == other.UseLegacyHttp &&
                   DisableVad == other.DisableVad &&
                   DisableTokenStreaming == other.DisableTokenStreaming &&
                   DisableProgressiveTts == other.DisableProgressiveTts &&
                   DisableTts == other.DisableTts &&
                   ChatModel == other.ChatModel &&
                   TtsVoice == other.TtsVoice &&
                   Language == other.Language &&
                   TtsMinFirstChunkLength == other.TtsMinFirstChunkLength &&
                   TtsMaxFirstChunkLength == other.TtsMaxFirstChunkLength &&
                   TtsSubsequentChunkLength == other.TtsSubsequentChunkLength;
        }

        public override int GetHashCode()
        {
            // Manual implementation of GetHashCode
            int hash = 17;
            hash = hash * 23 + UseLegacyHttp.GetHashCode();
            hash = hash * 23 + DisableVad.GetHashCode();
            hash = hash * 23 + DisableTokenStreaming.GetHashCode();
            hash = hash * 23 + DisableProgressiveTts.GetHashCode();
            hash = hash * 23 + DisableTts.GetHashCode();
            hash = hash * 23 + (ChatModel?.GetHashCode() ?? 0);
            hash = hash * 23 + (TtsVoice?.GetHashCode() ?? 0);
            hash = hash * 23 + (Language?.GetHashCode() ?? 0);
            hash = hash * 23 + TtsMinFirstChunkLength.GetHashCode();
            hash = hash * 23 + TtsMaxFirstChunkLength.GetHashCode();
            hash = hash * 23 + TtsSubsequentChunkLength.GetHashCode();
            return hash;
        }
    }
}