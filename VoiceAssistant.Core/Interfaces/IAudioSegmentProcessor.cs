using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant
{
    public interface IAudioSegmentProcessor
    {
        // Events to communicate back to the WebSocketHandler without a direct dependency
        event Func<string, string, Task> OnTranscriptionReady;
        event Func<byte[], int, string, Task> OnAudioChunkReady;
        event Func<string, string, Task> OnError;

        Task ProcessSegmentAsync(byte[] audioBytes, string sessionId, PipelineOptions pipelineOptions, VadSettings vadSettings);
    }
}
