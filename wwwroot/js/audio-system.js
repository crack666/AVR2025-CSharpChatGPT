// Constants
const FRAME_DURATION_MS = 20;
const TARGET_SAMPLE_RATE = 16000;
const SAMPLES_PER_CHUNK = TARGET_SAMPLE_RATE * (FRAME_DURATION_MS / 1000); // 320 samples
const AUDIO_PROCESSOR_URL = './js/audio-processor.js';

// Import modules
import * as ttsPlayback from './tts-playback.js';
import * as audioContextManager from './audio-context.js';
import * as webSocketHandler from './websocket-handler.js';
import * as audioUtils from './audio-utils.js';

// --- Core Audio State ---
let microphoneStream = null;
let mediaStreamSource = null;
let audioWorkletNode = null;
let isRecordingActive = false;
let isTTSSpeaking = false;
let currentBotModel = 'gpt-4o'; // Default model
let currentBotVoice = 'alloy'; // Default voice

// --- Exported Core Functions ---
export async function initAudioSystem() {
    audioUtils.debugLog('Initializing audio system...');

    audioContextManager.initAudioContextModule(TARGET_SAMPLE_RATE, audioUtils.debugLog);

    ttsPlayback.initTtsPlayback(
        audioContextManager.getAudioContext,
        audioUtils.debugLog,
        (speaking) => { isTTSSpeaking = speaking; },
        () => window.allAudioSources,
        () => window.currentBot
    );

    webSocketHandler.initWebSocketHandler({
        debugLog: audioUtils.debugLog,
        getPipelineOptions: () => window.optimizationManager?.getCurrentPipelineOptions(),
        getVadSettings: () => window.optimizationManager?.getCurrentVadSettings(),
        onOpen: handleWebSocketOpen,
        onClose: handleWebSocketClose,
        onMessage: handleWebSocketMessage,
        onError: handleWebSocketError
    });

    await attemptAutomaticAudioStart();
    // Ensure optimizationManager and uiManager are initialized by main.js before this
    if (window.optimizationManager && typeof window.optimizationManager.init === 'function') {
        // Assuming optimizationManager.init() is idempotent or handles multiple calls gracefully
        // window.optimizationManager.init(); 
    } else {
        console.warn("[AUDIO-SYSTEM] optimizationManager not found on window or not initialized.");
    }
    if (window.uiManager && typeof window.uiManager.init === 'function') {
        // window.uiManager.init();
    } else {
        console.warn("[AUDIO-SYSTEM] uiManager not found on window or not initialized.");
    }
}

export async function startRecording() {
    if (isRecordingActive) {
        audioUtils.debugLog("Recording is already active.");
        return;
    }
    audioUtils.debugLog("Attempting to start recording...");
    if (window.uiManager?.showStatus) {
        window.uiManager.showStatus("Verbinde Audio-Pipeline...");
    }

    ttsPlayback.resetTTSPlaybackState();
    if (window.uiManager?.clearCurrentBotMessage) {
        window.uiManager.clearCurrentBotMessage();
    }

    try {
        const audioContext = await audioContextManager.getAudioContext(true); // Ensure context is running
        if (!audioContext) {
            throw new Error("AudioContext could not be created or resumed.");
        }        if (!microphoneStream) {
            audioUtils.debugLog("Requesting microphone access...");
            // Use simple audio constraints like the working MASTER version
            const audioConstraints = { audio: true };
            microphoneStream = await navigator.mediaDevices.getUserMedia({ audio: audioConstraints, video: false });
            audioUtils.debugLog("Microphone access granted.");
        }

        // --- Set up the audio pipeline ---
        await setupAudioPipeline(microphoneStream);

        await webSocketHandler.connectWebSocket();
        
        isRecordingActive = true;
        updateUIAfterAudioInitAttempt(true);

    } catch (error) {
        console.error("Failed to start recording:", error);
        let reason = 'Unknown error during startRecording';
        if (error.name === 'NotAllowedError' || error.name === 'PermissionDeniedError') {
            reason = 'Microphone access denied';
        } else if (error.message.includes('AudioContext')) {
            reason = 'AudioContext issue: ' + error.message;
        } else if (error.message.includes('WebSocket')) {
            reason = 'WebSocket connection error: ' + error.message;
        } else if (error.message.includes('worklet')) {
            reason = 'Audio processing setup error: ' + error.message;
        }
        updateUIAfterAudioInitAttempt(false, reason);
        await cleanUpAudioResources(false);
    }
}

export async function stopRecording(sendEndOfStream = true) {
    if (!isRecordingActive) {
        audioUtils.debugLog("Recording is not active.");
        return;
    }
    audioUtils.debugLog(`Stopping recording. Send end of stream: ${sendEndOfStream}`);
    isRecordingActive = false; 
    await cleanUpAudioResources(sendEndOfStream);
    if (window.uiManager?.updateUIAfterStopRecording) {
        window.uiManager.updateUIAfterStopRecording();
    }
}

export async function restartAudioSystemAndClearState() {
    audioUtils.debugLog("Restarting audio system and clearing state...");
    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
        window.uiManager.showStatus("Audio-System wird neu gestartet...");
    }
    await stopAllAudioPlayback();
    await stopRecording(false);
    ttsPlayback.resetTTSPlaybackState();
    if (window.uiManager && typeof window.uiManager.clearCurrentBotMessage === 'function') {
        window.uiManager.clearCurrentBotMessage();
    }
    
    await new Promise(resolve => setTimeout(resolve, 100)); 
    
    audioUtils.debugLog("Re-initializing audio system components...");
    // The user will typically click "start recording" which handles the full setup.
    await audioContextManager.resumeAudioContext();
    
    if (window.uiManager && typeof window.uiManager.hideStatus === 'function') {
        window.uiManager.hideStatus();
    }
    if (window.uiManager && typeof window.uiManager.updateButtonStates === 'function') {
        window.uiManager.updateButtonStates(false); // Set to not recording
    }
}

export function stopAllAudioPlayback() {
    audioUtils.debugLog("Stopping all TTS audio playback.");
    if (window.allAudioSources) {
        window.allAudioSources.forEach(src => {
            try {
                src.onended = null; // Prevent onended logic from firing
                src.stop();
            } catch (e) {
                // console.warn("Error stopping an audio source:", e);
            }
        });
        window.allAudioSources = [];
    }
    if (window.currentBot && window.currentBot.audioSources) {
        window.currentBot.audioSources = [];
    }
    ttsPlayback.resetTTSPlaybackState(); // Also resets TTS specific flags
    isTTSSpeaking = false; // General flag
    // If using the other audioQueue system, clear it too
    // audioQueue = []; 
    // if (currentAudioElement) { currentAudioElement.pause(); currentAudioElement = null; }
}

// --- UI Helper for Audio Init Status ---
function updateUIAfterAudioInitAttempt(success, reason) {
    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
        if (success) {
            window.uiManager.showStatus('Audio bereit.', false, 2000);
        } else {
            window.uiManager.showStatus('Audio-Fehler: ' + (reason || 'Unbekannt'), true);
        }
    }
    if (window.uiManager && typeof window.uiManager.updateButtonStates === 'function') {
        window.uiManager.updateButtonStates(!!success);
    }
}

// --- Internal Core Logic ---

async function setupAudioPipeline(stream) {
    const audioContext = audioContextManager.getAudioContext();
    if (!audioContext || !stream) {
        throw new Error("AudioContext or microphone stream is not available for pipeline setup.");
    }

    // Ensure the audio processor module is loaded
    audioUtils.debugLog(`Loading AudioWorklet processor from: ${AUDIO_PROCESSOR_URL}`);
    try {
        await audioContext.audioWorklet.addModule(AUDIO_PROCESSOR_URL);
        audioUtils.debugLog("AudioWorklet processor loaded successfully.");
    } catch (e) {
        console.error(`Failed to load audio worklet processor from ${AUDIO_PROCESSOR_URL}`, e);
        // Don't throw if it's already been added.
        if (!e.message.toLowerCase().includes("already been loaded")) {
             throw new Error(`Failed to load audio worklet processor.`);
        } else {
            audioUtils.debugLog("AudioWorklet module was already loaded.");
        }
    }
    
    // Create the source and worklet node
    mediaStreamSource = audioContext.createMediaStreamSource(stream);
    audioUtils.debugLog("MediaStreamSource created.");

    audioWorkletNode = new AudioWorkletNode(audioContext, 'audio-processor', {
        processorOptions: {
            samplesPerChunk: SAMPLES_PER_CHUNK
        }
    });
    audioUtils.debugLog("AudioWorkletNode created.");

    // Set up the message handler for data from the worklet
    audioWorkletNode.port.onmessage = (event) => {
        if (event.data.type === 'audioData') {
            // Forward audio data to WebSocket handler if recording is active
            if (isRecordingActive) {
                const pipelineOptions = window.optimizationManager?.getCurrentPipelineOptions() || {};
                const progressiveTTSEnabled = !pipelineOptions.DisableProgressiveTts;
                const isCurrentlySpeaking = isTTSSpeaking;

                if (progressiveTTSEnabled && isCurrentlySpeaking) {
                    return; // Pause sending mic data if progressive TTS is active and speaking
                }
                sendAudioChunkToServer(event.data.buffer);
            }        } else if (event.data.type === 'rms') {
            // [TRACE LOG] - Very verbose RMS logging, only for detailed debugging
            audioUtils.traceLog(`[AudioSystem] Received RMS from worklet: ${event.data.rms}`);
            // Forward RMS value to UI manager for visualization
            if (window.uiManager?.updateAudioVisualization) {
                window.uiManager.updateAudioVisualization(event.data.rms);
            }
        }
    };
    audioUtils.debugLog("AudioWorkletNode message handler set up.");    // Connect the nodes to establish the audio processing chain
    mediaStreamSource.connect(audioWorkletNode);
    // NOTE: Do NOT connect worklet to destination - this can cause feedback and issues
    // audioWorkletNode.connect(audioContext.destination);
    audioUtils.debugLog("Audio pipeline connected: MediaStreamSource -> AudioWorkletNode (no destination connection).");
}

async function attemptAutomaticAudioStart() {
    audioUtils.debugLog('Attempting automatic audio start on page load...');
    try {
        const contextRunning = await audioContextManager.resumeAudioContext();
        if (!contextRunning) {
            audioUtils.debugLog("Automatic AudioContext resume failed. User gesture will be required.");
            updateUIAfterAudioInitAttempt(false, 'AudioContext blocked');
            return;
        }
          audioUtils.debugLog("Requesting microphone access automatically on page load...");
        if (!microphoneStream) {
            // Use simple audio constraints like the working MASTER version
            const audioConstraints = { audio: true };
            microphoneStream = await navigator.mediaDevices.getUserMedia({ audio: audioConstraints, video: false });
            audioUtils.debugLog("Microphone access granted automatically.");
        }
        
        await setupAudioPipeline(microphoneStream);

        await webSocketHandler.connectWebSocket();

        isRecordingActive = true;
        updateUIAfterAudioInitAttempt(true);
        audioUtils.debugLog("Automatic audio start completed successfully. Recording is now active.");

    } catch (error) {
        console.warn("Automatic audio start failed:", error);
        let reason = 'Automatic start failed';
        if (error.name === 'NotAllowedError' || error.name === 'PermissionDeniedError') {
            reason = 'Microphone access denied';
        } else if (error.message.includes('AudioContext')) {
            reason = 'AudioContext issue';
        } else if (error.message.includes('WebSocket')) {
            reason = 'WebSocket connection error: ' + error.message;
        } else if (error.message.includes('worklet')) {
            reason = 'Audio processing setup error: ' + error.message;
        }
        updateUIAfterAudioInitAttempt(false, reason);
    }
}

async function cleanUpAudioResources(sendEndOfStream = true) {
    audioUtils.debugLog("Cleaning up audio resources...");

    if (audioWorkletNode) {
        audioUtils.debugLog("Disconnecting AudioWorkletNode...");
        audioWorkletNode.port.onmessage = null;
        audioWorkletNode.disconnect();
        audioWorkletNode = null;
    }
    if (mediaStreamSource) {
        audioUtils.debugLog("Disconnecting MediaStreamSource...");
        mediaStreamSource.disconnect();
        mediaStreamSource = null;
    }
    if (microphoneStream) {
        audioUtils.debugLog("Stopping microphone stream tracks...");
        microphoneStream.getTracks().forEach(track => track.stop());
        microphoneStream = null;
    }

    await webSocketHandler.closeWebSocket(sendEndOfStream);
}

function getWebSocketUrlWithParams() {
    const protocol = window.location.protocol === 'https:' ? 'wss' : 'ws';
    const host = window.location.host;
    
    let queryParams = {};
    if (window.optimizationManager && typeof window.optimizationManager.getCurrentPipelineOptions === 'function') {
        queryParams = window.optimizationManager.getCurrentPipelineOptions();
        // Store model and voice for creating bot message later
        currentBotModel = queryParams.ChatModel;
        currentBotVoice = queryParams.TtsVoice;
        audioUtils.debugLog("[AUDIO-SYSTEM] Using pipeline options from optimizationManager:", queryParams);
    } else {
        console.error("[AUDIO-SYSTEM] CRITICAL: optimizationManager.getCurrentPipelineOptions() is not available. WebSocket connection will likely fail or use incorrect parameters.");
        // Fallback to some very basic defaults, though this state should ideally not be reached.
        queryParams = {
            Language: 'en',
            ChatModel: 'gpt-3.5-turbo',
            TtsVoice: 'nova',
            DisableVad: false,
            DisableTts: false,
            DisableProgressiveTts: false,
        };
        currentBotModel = queryParams.ChatModel;
        currentBotVoice = queryParams.TtsVoice;
    }

    if (window.optimizationManager && typeof window.optimizationManager.getCurrentVadSettings === 'function') {
        const vadSettings = window.optimizationManager.getCurrentVadSettings();
        // Only include VAD settings if VAD is not disabled by pipeline options
        if (!queryParams.DisableVad) {
            queryParams = {...queryParams, ...vadSettings};
            audioUtils.debugLog("[AUDIO-SYSTEM] Including VAD settings from optimizationManager:", vadSettings);
        } else {
            audioUtils.debugLog("[AUDIO-SYSTEM] VAD is disabled by pipeline options, not including VAD settings.");
        }
    } else {
        console.warn("[AUDIO-SYSTEM] optimizationManager.getCurrentVadSettings() not available. VAD settings might be incorrect if VAD is enabled.");
    }

    const queryString = new URLSearchParams(queryParams).toString();
    const path = `/ws/audio?${queryString}`;
    const fullUrl = `${protocol}://${host}${path}`;
    audioUtils.debugLog("[AUDIO-SYSTEM] Constructed WebSocket URL:", fullUrl);
    return fullUrl;
}

// --- WebSocket Event Handlers (to be passed to websocket-handler.js) ---
function handleWebSocketOpen(event) {
    audioUtils.debugLog('[WebSocket] Connection established.');
    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
        window.uiManager.showStatus("Audio-Pipeline verbunden.", false, 2000);
    }
    // If VAD settings need to be sent immediately on connect (alternative to sending on already open)
    // This is now handled by connectWebSocket in the handler if VAD is enabled.
}

async function handleWebSocketMessage(event) {
    if (typeof event.data === 'string') {
        try {
            const message = JSON.parse(event.data);
            // audioUtils.debugLog('[WebSocket] Received JSON message:', message); // Can be too verbose
            const botDetails = webSocketHandler.getCurrentBotDetails();

            switch (message.type) {
                case 'bot_status':
                    if (window.uiManager && typeof window.uiManager.updateBotStatusIndicator === 'function') {
                        window.uiManager.updateBotStatusIndicator(message.payload.is_thinking, message.payload.is_speaking, message.payload.is_processing);
                    }
                    break;                case 'prompt':
                    if (window.uiManager && typeof window.uiManager.addUserMessage === 'function') {
                        window.uiManager.addUserMessage(message.payload.text);
                    }
                    if (window.uiManager && typeof window.uiManager.addBotMessage === 'function') {
                        window.uiManager.addBotMessage('', botDetails.model, botDetails.voice);
                    }
                    break;
                case 'transcription':
                    audioUtils.debugLog('Server sent transcription:', message.payload);
                    if (window.uiManager && typeof window.uiManager.addUserMessage === 'function') {
                        // Handle transcription message - extract text from payload
                        const transcriptionText = message.payload?.text || message.payload;
                        window.uiManager.addUserMessage(transcriptionText);
                    }
                    if (window.uiManager && typeof window.uiManager.addBotMessage === 'function') {
                        window.uiManager.addBotMessage('', botDetails.model, botDetails.voice);
                    }
                    break;
                case 'token':
                    if (window.uiManager?.appendTokenToBotMessage) {
                        window.uiManager.appendTokenToBotMessage(message.payload.token);
                    }
                    // Update speaking state based on token stream
                    isTTSSpeaking = true; 
                    break;
                case 'done':
                    audioUtils.debugLog('Server signaled end of response.', message.payload);
                    if (window.uiManager?.finalizeBotMessage) {
                        window.uiManager.finalizeBotMessage(message.payload.full_reply, message.payload.performance_metrics);
                    }
                    // Update speaking state when response is done
                    isTTSSpeaking = false;
                    break;
                case 'error':
                    console.error('[WebSocket] Server error message:', message.payload.message);
                    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
                        window.uiManager.showStatus(`Server Fehler: ${message.payload.message}`, true);
                    }
                    break;
                case 'ping':
                    webSocketHandler.sendWebSocketMessage({ type: 'pong', timestamp: message.timestamp }); // Use module
                    break;                case 'audio-chunk-info':
                    audioUtils.debugLog(`[WebSocket] Received audio-chunk-info with index: ${message.index}`);
                    audioUtils.setLastKnownAudioChunkInfoIndex(message.index); // Use util function
                    break;
                case 'tts-all-chunks-sent':
                    audioUtils.debugLog('Server signaled all TTS audio chunks have been sent.');
                    ttsPlayback.signalAllTTSAudioChunksReceived();
                    // This is about chunks being *sent*, not finished playing.
                    // isTTSSpeaking state is better handled by ttsPlayback module.
                    break;
                case 'vad_settings_updated':
                    audioUtils.debugLog('[WebSocket] Server confirmed VAD settings update:', message.payload);
                    if (window.optimizationManager && typeof window.optimizationManager.updateVadSettingsStateFromRemote === 'function') {
                        window.optimizationManager.updateVadSettingsStateFromRemote(message.payload);
                    }
                    break;
                case 'pipeline_options_updated':
                    audioUtils.debugLog('[WebSocket] Server confirmed PipelineOptions update:', message.payload);
                    if (window.optimizationManager && typeof window.optimizationManager.updatePipelineOptionsStateFromRemote === 'function') {
                        window.optimizationManager.updatePipelineOptionsStateFromRemote(message.payload);
                    }
                    break;
                default:
                    console.warn('Received unhandled JSON message type:', message.type, message);
            }
        } catch (e) {
            console.error("Error parsing JSON from server or handling message:", e, event.data);
        }    } else if (event.data instanceof Blob) {
        audioUtils.debugLog(`[WebSocket] Received audio blob, size: ${event.data.size} bytes`);
        const audioData = await event.data.arrayBuffer();
        const audioContextRef = audioContextManager.getAudioContext();
        if (!audioContextRef) {
            console.error("AudioContext not available for decoding TTS chunk.");
            return;
        }
        try {
            const audioBuffer = await audioContextRef.decodeAudioData(audioData);
            const currentChunkIndex = audioUtils.getLastKnownAudioChunkInfoIndex(); // Use util function

            if (currentChunkIndex === -1) {
                console.warn("Received an audio chunk without a preceding valid info message/index. Using fallback index.");
                // Use a fallback strategy: assign next available index
                const fallbackIndex = ttsPlayback.getNextExpectedIndex();
                ttsPlayback.addTTSAudioChunk(fallbackIndex, audioBuffer);
                audioUtils.debugLog(`Received and decoded TTS audio chunk #${fallbackIndex} (fallback), duration=${audioBuffer.duration.toFixed(2)}s. Total buffered: ${ttsPlayback.getIndexedAudioChunks().size}`);
            } else {
                ttsPlayback.addTTSAudioChunk(currentChunkIndex, audioBuffer);
                audioUtils.debugLog(`Received and decoded TTS audio chunk #${currentChunkIndex}, duration=${audioBuffer.duration.toFixed(2)}s. Total buffered: ${ttsPlayback.getIndexedAudioChunks().size}`);
                audioUtils.resetLastKnownAudioChunkInfoIndex(); // Use util function
            }
            
            ttsPlayback.ttsPlayLoop();
        } catch (error) {
            console.error('Error decoding audio data:', error);
        }
    }
}

function handleWebSocketError(error) {
    console.error('WebSocket Error:', error);
    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
        window.uiManager.showStatus("WebSocket Fehler. Aufnahme gestoppt.", true);
    }
    // No need to call updateUIAfterStopRecording() directly, as onclose will handle it.
}

function handleWebSocketClose(event) {
    audioUtils.debugLog(`[WebSocket] Connection closed. Code: ${event.code}, Reason: ${event.reason}, Clean: ${event.wasClean}`);
    // webSocket = null; // Nullify the instance in websocket-handler.js if it does this itself, or manage here.
                      // For now, websocket-handler doesn't nullify its internal `webSocket` on close, it just reports.
    if (isRecordingActive) { // If we were recording, this is an unexpected close
        if (window.uiManager?.updateUIAfterStopRecording) {
            window.uiManager.updateUIAfterStopRecording("WebSocket unerwartet geschlossen.");
        }
        isRecordingActive = false; // Ensure state is updated
    } else {
        // If not recording, it might be a deliberate close after stopRecording, or a failed connection attempt.
        if (window.uiManager?.updateUIAfterStopRecording) {
            // window.uiManager.updateUIAfterStopRecording(); // This might be redundant if stopRecording already handled UI
        }
    }
}

// sendAudioChunkToServer remains here as it uses convertFloat32ToPcm16 and calls websocket-handler's sendBinaryData
function sendAudioChunkToServer(float32AudioChunk) {
    if (webSocketHandler.getWebSocketState() === WebSocket.OPEN) {
        const pcm16Buffer = audioUtils.convertFloat32ToPcm16(float32AudioChunk); // Use util function
        webSocketHandler.sendBinaryData(pcm16Buffer); // Use module function
    } else {
        // debugLog("WebSocket not open, cannot send audio chunk.");
    }
}

// Functions to send VAD/Pipeline settings (will use websocket-handler.sendWebSocketMessage)
export function sendVadSettingsUpdate(settings) {
    audioUtils.debugLog("Sending VAD settings update to server:", settings);
    webSocketHandler.sendWebSocketMessage({ type: 'updateVadSettings', payload: settings });
}

export function sendPipelineOptionsUpdate(options) {
    audioUtils.debugLog("Sending PipelineOptions update to server:", options);
    webSocketHandler.sendWebSocketMessage({ type: 'updatePipelineOptions', payload: options });
}

// --- General Purpose Audio Playback (e.g., for UI sounds) ---
// This section might also use audioContextManager.getAudioContext()
function playGeneralAudio(url) {
    fetch(url)
        .then(response => response.arrayBuffer())
        .then(data => {
            const ac = audioContextManager.getAudioContext(); // Use module function
            if (!ac) {
                console.error("AudioContext not available for general audio playback.");
                return;
            }
            return ac.decodeAudioData(data);
        })
    // ...existing code...
}

function processGeneralAudioQueue() {
    if (generalAudioQueue.length === 0 || isGeneralAudioPlaying) return;
    isGeneralAudioPlaying = true;
    const audioBuffer = generalAudioQueue.shift();
    const ac = audioContextManager.getAudioContext(); // Use module function
    const sourceNode = ac.createBufferSource();
    sourceNode.buffer = audioBuffer;
    sourceNode.connect(ac.destination);
    sourceNode.onended = () => {
        processGeneralAudioQueue(); // Play next
    };
    sourceNode.start();
    audioUtils.debugLog("Playing general audio from queue.");
}

// --- DOMContentLoaded ---
// It's generally better if main.js or another entry point explicitly calls initAudioSystem.
// However, if this script is loaded standalone and needs to self-initialize:
/*document.addEventListener('DOMContentLoaded', () => {
    debugLog("DOMContentLoaded event. audio-system.js is ready.");
    // If uiManager and optimizationManager are expected to be ready, init here.
    // Otherwise, ensure main.js calls initAudioSystem() after all managers are set up.
    // initAudioSystem(); // Consider if this should be called here or by an external orchestrator.
});*/

// Example of how ui-manager.js might interact:
/*
// In ui-manager.js (conceptual)
import { startRecording, stopRecording, restartAudioSystemAndClearState, stopAllAudioPlayback } from './audio-system.js';

document.getElementById('startRecordBtn').addEventListener('click', startRecording);
document.getElementById('stopRecordBtn').addEventListener('click', () => stopRecording(true));
document.getElementById('restartSystemBtn').addEventListener('click', restartAudioSystemAndClearState);
document.getElementById('stopTTSBtn').addEventListener('click', stopAllAudioPlayback);
*/