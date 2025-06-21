using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant
{    public interface IAudioFrameProcessor
    {
        Task ProcessFrameAsync(byte[] audioFrame, string sessionId);
        void UpdateSettings(VadSettings vadSettings, PipelineOptions pipelineOptions);
        
        // Traditional complete segment detection
        event Func<byte[], string, Task> SpeechSegmentDetected;
        
        // Streaming events
        event Func<string, Task> SpeechStreamStarted;          // When speech begins
        event Func<byte[], string, Task> SpeechFrameReady;     // Continuous frames during speech
        event Func<string, Task> SpeechStreamEnded;           // When speech ends
    }
}
