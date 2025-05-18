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
        /// Minimum continuous speech duration in seconds required before starting recording.
        /// </summary>
        public double MinSpeechDurationSec { get; set; } = 1.0;
    }
}