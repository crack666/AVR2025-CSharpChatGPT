namespace VoiceAssistant.Core.Models
{
    /// <summary>
    /// Configuration settings for voice-activity detection (VAD).
    /// </summary>
    public class VadSettings
    {
        /// <summary>
        /// Amplitude threshold (RMS) above which frames are considered for speech.
        /// </summary>
        public double Threshold { get; set; } = 0.008;

        /// <summary>
        /// Silence timeout in seconds before ending a speech segment.
        /// </summary>
        public double SilenceTimeoutSec { get; set; } = 0.5;

        /// <summary>
        /// Minimum continuous speech duration in seconds required before starting a speech segment.
        /// </summary>
        public double MinSpeechDurationSec { get; set; } = 1.0;
        
        /// <summary>
        /// Pre-buffer duration in seconds: how much audio before VAD start is included in a segment.
        /// </summary>
        public double PreSpeechDurationSec { get; set; } = 0.2;

        /// <summary>
        /// Window duration in seconds for RMS smoothing (EMA).
        /// </summary>
        public double RmsSmoothingWindowSec { get; set; } = 0.1;

        /// <summary>
        /// RMS threshold for detecting start of speech.
        /// </summary>
        public double StartThreshold { get; set; } = 0.008;

        /// <summary>
        /// RMS threshold for detecting end of speech.
        /// </summary>
        public double EndThreshold { get; set; } = 0.005;

        /// <summary>
        /// Duration in seconds to wait after the last speech frame (hang-over) before closing a segment.
        /// </summary>
        public double HangoverDurationSec { get; set; } = 0.2;
    }
}