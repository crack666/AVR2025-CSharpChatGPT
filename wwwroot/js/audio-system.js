// Constants
const FRAME_DURATION_MS = 20;
const TARGET_SAMPLE_RATE = 16000;
const SAMPLES_PER_CHUNK = TARGET_SAMPLE_RATE * (FRAME_DURATION_MS / 1000); // 320 samples

// Import modules
import * as ttsPlayback from './tts-playback.js';
import * as audioContextManager from './audio-context.js';
import * as microphoneManager from './microphone.js';
import * as webSocketHandler from './websocket-handler.js';
import * as audioUtils from './audio-utils.js'; // NEW IMPORT

// --- Core Audio State ---
let microphoneStream = null; // This will now primarily hold the raw MediaStream object from getUserMedia
// let mediaStreamSource = null; // MOVED to microphone.js
// let scriptProcessorNode = null; // MOVED to microphone.js
let isRecordingActive = false;
let isTTSSpeaking = false;
// let audioBufferForServer = new Float32Array(0); // MOVED to microphone.js

// --- WebSocket State ---
// let webSocket = null; // MOVED to websocket-handler.js
// let currentBotModel = null; // MOVED to websocket-handler.js
// let currentBotVoice = null; // MOVED to websocket-handler.js

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

    microphoneManager.initMicrophone({
        getAudioContext: audioContextManager.getAudioContext,
        onAudioChunkProcessed: sendAudioChunkToServer, // Still using local sendAudioChunkToServer
        debugLog: audioUtils.debugLog,
        getIsRecordingActive: () => isRecordingActive,
        getPipelineOptions: () => window.optimizationManager?.getCurrentPipelineOptions(),
        getIsTTSSpeaking: () => isTTSSpeaking,
        updateAudioVisualization: (rms) => window.uiManager?.updateAudioVisualization(rms),
        samplesPerChunk: SAMPLES_PER_CHUNK
    });

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
    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
        window.uiManager.showStatus("Verbinde Audio-Pipeline...");
    }

    ttsPlayback.resetTTSPlaybackState();
    // Clear any previous bot message UI that might be half-streamed
    if (window.uiManager && typeof window.uiManager.clearCurrentBotMessage === 'function') {
        window.uiManager.clearCurrentBotMessage();
    }

    try {
        const contextRunning = await audioContextManager.resumeAudioContext();
        if (!contextRunning) {
            updateUIAfterAudioInitAttempt(false, 'AudioContext not running after resume attempt.');
            return;
        }

        // Request microphone access if not already available
        if (!microphoneStream) {
            audioUtils.debugLog("Requesting microphone access...");
            microphoneStream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, sampleRate: TARGET_SAMPLE_RATE }, video: false });
            audioUtils.debugLog("Microphone access granted.");
        }

        await webSocketHandler.connectWebSocket(); // Use module function
        await microphoneManager.startMicrophoneProcessing(microphoneStream);
        
        isRecordingActive = true;
        updateUIAfterAudioInitAttempt(true);

    } catch (error) {
        console.error("Failed to start recording:", error);
        let reason = 'Unknown error during startRecording';
        if (error.name === 'NotAllowedError' || error.name === 'PermissionDeniedError') {
            reason = 'Microphone access denied';
        } else if (error.message.includes('AudioContext')) {
            reason = 'AudioContext issue';
        } else if (error.message.includes('WebSocket')) { // General WebSocket error
            reason = 'WebSocket connection error: ' + error.message;
        } else if (error.message.includes('WebSocket busy')) { // Specific error from our handler
            reason = 'WebSocket busy, please wait.';
        } else if (error.message.includes('Microphone module')) {
            reason = 'Microphone processing setup error: ' + error.message;
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
    updateUIAfterStopRecording();
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
    // No need to call initAudioSystem() directly if it just does attemptAutomaticAudioStart
    // The user will typically click "start recording" which handles the full setup.
    // However, ensuring the audio context is ready is good.
    await attemptAutomaticAudioStart(); 
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

// --- Internal Core Logic ---
async function attemptAutomaticAudioStart() {
    audioUtils.debugLog('Attempting automatic audio start on page load...');
    try {
        const contextRunning = await audioContextManager.resumeAudioContext();
        if (!contextRunning) {
            audioUtils.debugLog("Automatic AudioContext resume failed or context not running. User gesture will be required.");
            updateUIAfterAudioInitAttempt(false, 'AudioContext blocked');
            return;
        }
        
        audioUtils.debugLog("Requesting microphone access automatically on page load...");
        if (!microphoneStream) {
            microphoneStream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, sampleRate: TARGET_SAMPLE_RATE }, video: false });
            audioUtils.debugLog("Microphone access granted automatically.");
        }
        
        await webSocketHandler.connectWebSocket(); // Use module function
        await microphoneManager.startMicrophoneProcessing(microphoneStream);

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
        } else if (error.message.includes('WebSocket busy')) {
            reason = 'WebSocket busy, please wait.';
        } else if (error.message.includes('Microphone module')) {
            reason = 'Microphone processing setup error: ' + error.message;
        }
        updateUIAfterAudioInitAttempt(false, reason);
    }
}

async function cleanUpAudioResources(sendEndOfStream = true) {
    audioUtils.debugLog("Cleaning up audio resources...");
    microphoneManager.stopMicrophoneProcessing(); // This now also stops the tracks of its internally held stream
    microphoneStream = null; // Nullify the main microphoneStream variable in audio-system.js

    // audioBufferForServer is now managed within microphone.js
    await webSocketHandler.closeWebSocket(sendEndOfStream); // Use module function
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
            audioUtils.debugLog('[WebSocket] Received JSON message:', message);
            const botDetails = webSocketHandler.getCurrentBotDetails(); // Get model/voice

            switch (message.type) {
                case 'bot_status':
                    if (window.uiManager && typeof window.uiManager.updateBotStatusIndicator === 'function') {
                        window.uiManager.updateBotStatusIndicator(message.payload.is_thinking, message.payload.is_speaking, message.payload.is_processing);
                    }
                    break;
                case 'prompt':
                    if (window.uiManager && typeof window.uiManager.addUserMessage === 'function') {
                        window.uiManager.addUserMessage(message.payload.text);
                    }
                    if (window.uiManager && typeof window.uiManager.addBotMessage === 'function') {
                        window.uiManager.addBotMessage('', botDetails.model, botDetails.voice);
                    }
                    break;
                case 'token':
                    if (window.uiManager && typeof window.uiManager.appendTokenToBotMessage === 'function') {
                        window.uiManager.appendTokenToBotMessage(message.payload.token);
                    }
                    break;
                case 'done':
                    audioUtils.debugLog('Server signaled end of response.', message.payload);
                    if (window.uiManager && typeof window.uiManager.finalizeBotMessage === 'function') {
                        window.uiManager.finalizeBotMessage(message.payload.full_reply, message.payload.performance_metrics);
                    }
                    break;
                case 'error':
                    console.error('[WebSocket] Server error message:', message.payload.message);
                    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
                        window.uiManager.showStatus(`Server Fehler: ${message.payload.message}`, true);
                    }
                    break;
                case 'ping':
                    webSocketHandler.sendWebSocketMessage({ type: 'pong', timestamp: message.timestamp }); // Use module
                    break;
                case 'audio-chunk-info':
                    audioUtils.setLastKnownAudioChunkInfoIndex(message.index); // Use util function
                    break;
                case 'tts-all-chunks-sent':
                    audioUtils.debugLog('Server signaled all TTS audio chunks have been sent.');
                    ttsPlayback.signalAllTTSAudioChunksReceived();
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
        }
    } else if (event.data instanceof Blob) {
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
                console.warn("Received an audio chunk without a preceding valid info message/index. Discarding.");
                return;
            }

            ttsPlayback.addTTSAudioChunk(currentChunkIndex, audioBuffer);
            audioUtils.debugLog(`Received and decoded TTS audio chunk #${currentChunkIndex}, duration=${audioBuffer.duration.toFixed(2)}s. Total buffered: ${ttsPlayback.getIndexedAudioChunks().size}`);
            ttsPlayback.ttsPlayLoop();
            audioUtils.resetLastKnownAudioChunkInfoIndex(); // Use util function
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
        updateUIAfterStopRecording("WebSocket unerwartet geschlossen.");
        isRecordingActive = false; // Ensure state is updated
    } else {
        // If not recording, it might be a deliberate close after stopRecording, or a failed connection attempt.
        // updateUIAfterStopRecording(); // This might be redundant if stopRecording already handled UI
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
// ...existing code...
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