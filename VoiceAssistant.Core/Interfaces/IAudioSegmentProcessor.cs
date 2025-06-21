using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant
{    public interface IAudioSegmentProcessor
    {        
        // Events to communicate back to the WebSocketHandler without a direct dependency
        event Func<string, string, Task> OnTranscriptionReady;
        event Func<string, string, Task> OnTokenReady;
        event Func<byte[], int, string, Task> OnAudioChunkReady;
        event Func<string, string, Task> OnError;
        event Func<string, object, string, Task> OnDone;

        // Traditional segment processing
        Task ProcessSegmentAsync(byte[] audioBytes, string sessionId, PipelineOptions pipelineOptions, VadSettings vadSettings);
        
        // Streaming session management
        Task StartStreamingSessionAsync(string sessionId, PipelineOptions pipelineOptions);
        Task ProcessStreamingChunkAsync(byte[] audioChunk, string sessionId, PipelineOptions pipelineOptions);
        Task EndStreamingSessionAsync(string sessionId, PipelineOptions pipelineOptions);
    }
}
