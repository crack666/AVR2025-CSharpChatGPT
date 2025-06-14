namespace VoiceAssistant.Core.Models
{
    /// <summary>
    /// Feature flags for selecting pipeline modes and optimizations.
    /// </summary>
    public class PipelineOptions
    {
        /// <summary>Use the legacy HTTP-based endpoints instead of WebSocket streaming.</summary>
        public bool UseLegacyHttp { get; set; }
        /// <summary>Disable voice activity detection (process full audio segments).</summary>
        public bool DisableVad { get; set; }
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

        /// <summary>Minimum length for the first TTS chunk to trigger synthesis.</summary>
        public int TtsMinFirstChunkLength { get; set; } = 50; // Default value, can be tuned

        /// <summary>Maximum length for the first TTS chunk.</summary>
        public int TtsMaxFirstChunkLength { get; set; } = 100; // Default value, can be tuned

        /// <summary>Target length for subsequent TTS chunks.</summary>
        public int TtsSubsequentChunkLength { get; set; } = 250; // Default value, can be tuned
    }
}