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
    }
}