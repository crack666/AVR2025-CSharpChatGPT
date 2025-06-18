// Constants
const FRAME_DURATION_MS = 20;
const TARGET_SAMPLE_RATE = 16000;
const SAMPLES_PER_CHUNK = TARGET_SAMPLE_RATE * (FRAME_DURATION_MS / 1000); // 320 samples

// Import modules
import * as ttsPlayback from './tts-playback.js';
import * as audioContextManager from './audio-context.js';
import * as microphoneManager from './microphone.js';
import * as webSocketHandler from './websocket-handler.js';
import * as audioUtils from './audio-utils.js';

// --- Core Audio State ---
let microphoneStream = null; // This will now primarily hold the raw MediaStream object from getUserMedia
// let mediaStreamSource = null; // MOVED to microphone.js
// let scriptProcessorNode = null; // MOVED to microphone.js
let isRecordingActive = false;
let isTTSSpeaking = false;
let isInitializationInProgress = false;

// This function will now orchestrate the entire startup sequence,
// triggered automatically on page load.
export async function initAndStartAudioSystem() {
    if (isRecordingActive || isInitializationInProgress) {
        audioUtils.debugLog("Audio system is already running or initialization is in progress.");
        return;
    }
    isInitializationInProgress = true;
    audioUtils.debugLog('Attempting to initialize and start audio system automatically...');
    if (window.uiManager) window.uiManager.showStatus("Warte auf Mikrofonberechtigung...");

    try {
        // 1. Get microphone access first. This is the required user gesture.
        audioUtils.debugLog("Requesting microphone access...");
        microphoneStream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, sampleRate: TARGET_SAMPLE_RATE }, video: false });
        audioUtils.debugLog("Microphone access granted.");

        // 2. Now that we have permission, create and resume the AudioContext.
        audioContextManager.initAudioContextModule(TARGET_SAMPLE_RATE, audioUtils.debugLog);
        const contextRunning = await audioContextManager.resumeAudioContext();
        if (!contextRunning) {
            throw new Error("AudioContext could not be started.");
        }
        audioUtils.debugLog(`AudioContext is active (state: ${audioContextManager.getAudioContext().state}).`);

        // 3. Initialize all modules that depend on the AudioContext or other configs.
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

        // The microphone manager now needs the worklet to be loaded, which is async.
        await microphoneManager.initMicrophone({
            getAudioContext: audioContextManager.getAudioContext,
            onAudioChunkProcessed: sendAudioChunkToServer,
            debugLog: audioUtils.debugLog,
            getIsRecordingActive: () => isRecordingActive,
            updateAudioVisualization: (rms) => window.uiManager?.updateAudioVisualization(rms),
            samplesPerChunk: SAMPLES_PER_CHUNK
        });

        // 4. Start microphone processing for immediate UI feedback.
        audioUtils.debugLog("Starting microphone processing for UI feedback...");
        await microphoneManager.startMicrophoneProcessing(microphoneStream);
        audioUtils.debugLog("Microphone processing started.");

        // 5. Connect the WebSocket in the background.
        audioUtils.debugLog("Connecting WebSocket in the background...");
        webSocketHandler.connectWebSocket().catch(error => {
            console.error("Background WebSocket connection failed:", error);
            updateUIAfterAudioInitAttempt(false, "WebSocket-Verbindung fehlgeschlagen");
            stopRecording(false); // Stop if WebSocket fails
        });

        isRecordingActive = true;
        isInitializationInProgress = false;
        updateUIAfterAudioInitAttempt(true);

    } catch (error) {
        console.error("Failed to start audio system:", error);
        let reason = 'Unknown error during startup';
        if (error.name === 'NotAllowedError' || error.name === 'PermissionDeniedError') {
            reason = 'Mikrofonberechtigung verweigert';
        } else {
            reason = error.message;
        }
        updateUIAfterAudioInitAttempt(false, reason);
        await cleanUpAudioResources(false);
        isInitializationInProgress = false;
    }
}

// Manual start function, in case automatic start fails or user wants to restart.
export async function startRecording() {
    if (isRecordingActive) {
        audioUtils.debugLog("Recording is already active.");
        return;
    }
    // Simply re-run the main initialization sequence.
    await initAndStartAudioSystem();
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
    
    audioUtils.debugLog("Audio system has been reset. Waiting for user to start recording.");
    // The call to the removed function `attemptAutomaticAudioStart` was here.
    // It's been removed because the system should now wait for a manual user action 
    // (e.g., clicking the start button) to begin recording. This prevents race conditions
    // and respects browser autoplay policies.
    
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

function updateUIAfterStopRecording(reason) {
    audioUtils.debugLog("Updating UI after stopping recording.");
    if (window.uiManager) {
        if (reason) {
            window.uiManager.showStatus(reason, true); // Show as an error/warning
        } else {
            window.uiManager.showStatus("Aufnahme gestoppt.", false, 2000); // Acknowledge stop
        }
        window.uiManager.updateButtonStates(false); // Set to non-recording state
        window.uiManager.updateAudioVisualization(0); // Reset audio level meter
    }
}

// --- Internal Core Logic ---
// REMOVED: The entire faulty attemptAutomaticAudioStart function is gone.

async function cleanUpAudioResources(sendEndOfStream = true) {
    audioUtils.debugLog("Cleaning up audio resources...");
    microphoneManager.stopMicrophoneProcessing(); // This now also stops the tracks of its internally held stream
    microphoneStream = null; // Nullify the main microphoneStream variable in audio-system.js

    // audioBufferForServer is now managed within microphone.js
    await webSocketHandler.closeWebSocket(sendEndOfStream); // Use module function
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
    // The websocket-handler module manages the WebSocket instance and nullifies it on close.
    // This handler's job is to react to the close event from the perspective of the audio system.
    if (isRecordingActive) { // If we were recording, this is an unexpected close
        updateUIAfterStopRecording("WebSocket unerwartet geschlossen.");
        isRecordingActive = false; // Ensure state is updated
    } else {
        // If not recording, it was likely a deliberate close (e.g., from stopRecording)
        // or a failed connection attempt. The UI for this is handled by the functions
        // that initiated the stop or connection attempt.
        audioUtils.debugLog("WebSocket closed while not in an active recording state.");
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