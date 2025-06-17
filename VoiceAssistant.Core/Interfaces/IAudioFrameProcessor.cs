using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant
{
    public interface IAudioFrameProcessor
    {
        Task ProcessFrameAsync(byte[] audioFrame, string sessionId);
        void UpdateSettings(VadSettings vadSettings, PipelineOptions pipelineOptions);
        event Func<byte[], string, Task> SpeechSegmentDetected;
    }
}
