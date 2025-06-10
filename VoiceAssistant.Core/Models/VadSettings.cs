using WebRtcVadSharp;

namespace VoiceAssistant.Core.Models
{
    /// <summary>
    /// Configuration settings for voice-activity detection (VAD).
    /// </summary>
    public class VadSettings
    {
        /// <summary>
        /// Operating mode for the WebRTC-VAD:
        /// 0 = Quality, 1 = Low Bitrate, 2 = Aggressive, 3 = Very Aggressive.
        /// </summary>
        public OperatingMode OperatingMode { get; set; } = OperatingMode.VeryAggressive;

        /// <summary>
        /// Static pre-amplification factor applied to incoming audio samples.
        /// </summary>
        public float PreAmplification { get; set; } = 1.0f;

        /// <summary>
        /// Minimum continuous speech duration in seconds required before starting a speech segment.
        /// </summary>
        public double MinSpeechDurationSec { get; set; } = 0.4;

        /// <summary>
        /// Duration in seconds for pre-buffer (pre-roll) before speech start.
        /// </summary>
        public double PreSpeechDurationSec { get; set; } = 0.2;

        /// <summary>
        /// Hang-over duration in seconds after the last speech frame before closing a segment.
        /// </summary>
        public double HangoverDurationSec { get; set; } = 0.5;

        /// <summary>
        /// Minimum final segment duration in seconds to accept as real speech (post-filter).
        /// Segments shorter than this are discarded.
        /// </summary>
        public double MinSegmentDurationSec { get; set; } = 0.1;

        /// <summary>
        /// Factor by which the estimated noise floor is multiplied to derive a dynamic RMS threshold.
        /// </summary>
        public double NoiseThresholdFactor { get; set; } = 1.8;

        /// <summary>
        /// EMA smoothing factor (alpha) when updating the noise floor estimate.
        /// </summary>
        public double NoiseFloorAlpha { get; set; } = 0.95;

        /// <summary>
        /// Lower clamp for the estimated noise floor. Prevents threshold from falling below this value.
        /// </summary>
        public double MinNoiseFloor { get; set; } = 0.0001;

        /// <summary>
        /// Duration in seconds of continuous silence required before adapting the noise floor.
        /// Prevents rapid decay during short pauses.
        /// </summary>
        public double SilenceAdaptationTimeSec { get; set; } = 1.0;

        /// <summary>
        /// RMS threshold for a frame to be considered a potential 'spike' or start of speech.
        /// This is a more aggressive check than the regular dynamic threshold for faster initial detection.
        /// </summary>
        public double VadSpikeThreshold { get; set; } = 0.05; // Example value, needs tuning

        /// <summary>
        /// Enables or disables the spike detection mechanism.
        /// </summary>
        public bool EnableSpikeDetection { get; set; } = true;

        /// <summary>
        /// Enables or disables the third-party VAD (e.g., WebRTC VAD).
        /// </summary>
        public bool EnableThirdPartyVad { get; set; } = true;
    }
}
