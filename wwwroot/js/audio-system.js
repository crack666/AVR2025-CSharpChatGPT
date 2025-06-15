// Constants
const FRAME_DURATION_MS = 20;
const TARGET_SAMPLE_RATE = 16000;
const SAMPLES_PER_CHUNK = TARGET_SAMPLE_RATE * (FRAME_DURATION_MS / 1000); // 320 samples
const IS_DEBUG_MODE = true; // Enable/disable extensive logging

// --- Progressive TTS Playback State & Functions ---
const indexedAudioChunks = new Map();
let nextPlaybackIndex = 0;
let isCurrentlyPlayingTTS = false;
let allAudioChunksReceived = false;
let isTTSLoopActive = false;
let lastReceivedAudioChunkIndex = -1; // Stores the index from 'audio-chunk-info'

function debugLog(...args) {
    if (IS_DEBUG_MODE) {
        console.log('%c[AUDIO-SYSTEM]', 'color: cyan', ...args);
    }
}

function scheduleTTSChunk() {
    if (indexedAudioChunks.has(nextPlaybackIndex)) {
        const buffer = indexedAudioChunks.get(nextPlaybackIndex);
        indexedAudioChunks.delete(nextPlaybackIndex);

        debugLog(`Playing TTS chunk #${nextPlaybackIndex}, duration=${buffer.duration.toFixed(2)}s, remaining TTS chunks=${indexedAudioChunks.size}`);

        const audioContextRef = getAudioContext(); // Ensure AudioContext is available
        if (!audioContextRef) {
            console.error("Cannot play TTS chunk, AudioContext not available.");
            isCurrentlyPlayingTTS = false;
            return;
        }

        const src = audioContextRef.createBufferSource();
        src.buffer = buffer;
        src.connect(audioContextRef.destination);

        // Manage audio sources for stopping
        window.allAudioSources = window.allAudioSources || [];
        window.allAudioSources.push(src);
        if (window.currentBot && window.currentBot.audioSources) { // If per-message tracking is still used
            window.currentBot.audioSources.push(src);
        }

        isCurrentlyPlayingTTS = true;
        const playedChunkIndex = nextPlaybackIndex; // Capture index for onended
        nextPlaybackIndex++;

        src.onended = () => {
            debugLog(`Finished playing TTS chunk #${playedChunkIndex}`);
            isCurrentlyPlayingTTS = false;
            
            const indexInAll = window.allAudioSources.indexOf(src);
            if (indexInAll > -1) window.allAudioSources.splice(indexInAll, 1);
            if (window.currentBot && window.currentBot.audioSources) {
                const indexInBot = window.currentBot.audioSources.indexOf(src);
                if (indexInBot > -1) window.currentBot.audioSources.splice(indexInBot, 1);
            }

            ttsPlayLoop(); // Attempt to play the next chunk

            if (allAudioChunksReceived && indexedAudioChunks.size === 0 && !isCurrentlyPlayingTTS) {
                debugLog('All received TTS chunks have been played. Resetting TTS playback state.');
                resetTTSPlaybackState();
            }
        };
        src.start();
    }
}

function ttsPlayLoop() {
    if (!isTTSLoopActive) {
        isTTSLoopActive = true;
        debugLog('TTS PlayLoop started.');
    }
    if (isCurrentlyPlayingTTS) return;
    if (indexedAudioChunks.has(nextPlaybackIndex)) {
        scheduleTTSChunk();
    } else {
        // debugLog(`TTS PlayLoop: Next chunk #${nextPlaybackIndex} not available. Pausing loop.`);
    }
}

function resetTTSPlaybackState() {
    debugLog('Resetting TTS playback state.');
    indexedAudioChunks.clear();
    nextPlaybackIndex = 0;
    isCurrentlyPlayingTTS = false;
    isTTSLoopActive = false;
    allAudioChunksReceived = false;
    lastReceivedAudioChunkIndex = -1;
    isTTSSpeaking = false; // Ensure this is also reset
}

// --- Core Audio State ---
let audioContext = null; // Singleton AudioContext
let microphoneStream = null;
let mediaStreamSource = null;
let scriptProcessorNode = null;
let isRecordingActive = false; // Is microphone actively capturing and sending data
let isTTSSpeaking = false; // Is TTS audio currently playing (used to pause mic processing)
let audioBufferForServer = new Float32Array(0); // Buffer for microphone audio before sending

// --- WebSocket State ---
let webSocket = null;
let currentBotModel = null; // To store the model used for the current/last bot message
let currentBotVoice = null; // To store the voice used for the current/last bot message

// --- Helper Functions ---
function getAudioContext() {
    if (!audioContext) {
        const AudioContextGlobal = window.AudioContext || window.webkitAudioContext;
        if (AudioContextGlobal) {
            try {
                audioContext = new AudioContextGlobal({ sampleRate: TARGET_SAMPLE_RATE });
                debugLog(`AudioContext created (state: ${audioContext.state}). Sample rate: ${audioContext.sampleRate}`);
            } catch (e) {
                console.error("Failed to create AudioContext:", e);
                return null;
            }
        } else {
            console.error("Browser does not support AudioContext.");
            return null;
        }
    }
    return audioContext;
}

async function resumeAudioContext() {
    const ac = getAudioContext();
    if (ac && ac.state === 'suspended') {
        try {
            await ac.resume();
            debugLog(`AudioContext resumed (state: ${ac.state}).`);
        } catch (e) {
            console.error("Error resuming AudioContext:", e);
            throw e; // Re-throw for caller to handle
        }
    }
    return ac && ac.state === 'running';
}

// --- Exported Core Functions ---
export async function initAudioSystem() {
    debugLog('Initializing audio system...');
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
        debugLog("Recording is already active.");
        return;
    }
    debugLog("Attempting to start recording...");
    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
        window.uiManager.showStatus("Verbinde Audio-Pipeline...");
    }

    resetTTSPlaybackState();
    // Clear any previous bot message UI that might be half-streamed
    if (window.uiManager && typeof window.uiManager.clearCurrentBotMessage === 'function') {
        window.uiManager.clearCurrentBotMessage(); 
    }

    try {
        const contextRunning = await resumeAudioContext();
        if (!contextRunning) {
            updateUIAfterAudioInitAttempt(false, 'AudioContext not running after resume attempt.');
            return;
        }

        if (!microphoneStream) {
            debugLog("Requesting microphone access...");
            microphoneStream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, sampleRate: TARGET_SAMPLE_RATE }, video: false });
            debugLog("Microphone access granted.");
        }

        await connectAudioPipeline(); // This will also connect WebSocket with new params
        isRecordingActive = true;
        updateUIAfterAudioInitAttempt(true);

    } catch (error) {
        console.error("Failed to start recording:", error);
        let reason = 'Unknown error during startRecording';
        if (error.name === 'NotAllowedError' || error.name === 'PermissionDeniedError') {
            reason = 'Microphone access denied';
        } else if (error.message.includes('AudioContext')) {
            reason = 'AudioContext issue';
        } else if (error.message.includes('WebSocket')) {
            reason = 'WebSocket connection error: ' + error.message;
        }
        updateUIAfterAudioInitAttempt(false, reason);
        await cleanUpAudioResources(false);
    }
}

export async function stopRecording(sendEndOfStream = true) {
    if (!isRecordingActive) {
        debugLog("Recording is not active.");
        return;
    }
    debugLog(`Stopping recording. Send end of stream: ${sendEndOfStream}`);
    isRecordingActive = false; 
    await cleanUpAudioResources(sendEndOfStream);
    updateUIAfterStopRecording();
}

export async function restartAudioSystemAndClearState() {
    debugLog("Restarting audio system and clearing state...");
    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
        window.uiManager.showStatus("Audio-System wird neu gestartet...");
    }
    await stopAllAudioPlayback();
    await stopRecording(false);
    resetTTSPlaybackState();
    if (window.uiManager && typeof window.uiManager.clearCurrentBotMessage === 'function') {
        window.uiManager.clearCurrentBotMessage();
    }
    
    await new Promise(resolve => setTimeout(resolve, 100)); 
    
    debugLog("Re-initializing audio system components...");
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
    debugLog("Stopping all TTS audio playback.");
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
    resetTTSPlaybackState(); // Also resets TTS specific flags
    isTTSSpeaking = false; // General flag
    // If using the other audioQueue system, clear it too
    // audioQueue = []; 
    // if (currentAudioElement) { currentAudioElement.pause(); currentAudioElement = null; }
}

// --- Internal Core Logic ---
async function attemptAutomaticAudioStart() {
    debugLog('Attempting automatic audio start on page load...');
    try {
        const contextRunning = await resumeAudioContext();
        if (!contextRunning) {
            debugLog("Automatic AudioContext resume failed or context not running. User gesture will be required.");
            updateUIAfterAudioInitAttempt(false, 'AudioContext blocked'); // Inform UI, but don't mark as critical error yet
            return;
        }
        // Don't request microphone automatically here, wait for startRecording or specific user action
        // If we wanted to auto-start mic:
        // if (!microphoneStream) {
        //     microphoneStream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, sampleRate: TARGET_SAMPLE_RATE }, video: false });
        // }
        // await connectAudioPipeline(); // This would also connect WebSocket
        // isRecordingActive = true;
        // updateUIAfterAudioInitAttempt(true);
        debugLog("AudioContext is running. Ready for user to start recording.");

    } catch (error) {
        console.warn("Automatic audio start failed:", error);
        updateUIAfterAudioInitAttempt(false, 'Automatic start failed');
    }
}

async function connectAudioPipeline() {
    const ac = getAudioContext();
    if (!microphoneStream || !ac || ac.state !== 'running') {
        throw new Error("Cannot connect audio pipeline: Stream or AudioContext not ready or not running.");
    }

    debugLog('Connecting audio pipeline...');
    if (mediaStreamSource) mediaStreamSource.disconnect();
    if (scriptProcessorNode) scriptProcessorNode.disconnect();

    mediaStreamSource = ac.createMediaStreamSource(microphoneStream);
    const bufferSize = 4096;
    scriptProcessorNode = ac.createScriptProcessor(bufferSize, 1, 1);

    scriptProcessorNode.onaudioprocess = (audioProcessingEvent) => {
        if (!isRecordingActive) return; // Only gate on isRecordingActive
        
        // Check if TTS is speaking *only if* progressive TTS is enabled and active
        // For non-progressive, or if TTS is disabled, we should not pause mic input based on isTTSSpeaking
        let pipelineOptions = {};
        if (window.optimizationManager && typeof window.optimizationManager.getCurrentPipelineOptions === 'function') {
            pipelineOptions = window.optimizationManager.getCurrentPipelineOptions();
        }
        const progressiveTTSEnabled = !pipelineOptions.DisableProgressiveTts;

        if (progressiveTTSEnabled && isTTSSpeaking) {
            // debugLog("Microphone processing paused due to active progressive TTS.");
            return; // Pause sending mic data if progressive TTS is active and speaking
        }

        const inputData = audioProcessingEvent.inputBuffer.getChannelData(0);
        const currentServerBuffer = audioBufferForServer;
        const combinedBuffer = new Float32Array(currentServerBuffer.length + inputData.length);
        combinedBuffer.set(currentServerBuffer);
        combinedBuffer.set(inputData, currentServerBuffer.length);
        audioBufferForServer = combinedBuffer;

        while (audioBufferForServer.length >= SAMPLES_PER_CHUNK) {
            const chunkToProcess = audioBufferForServer.slice(0, SAMPLES_PER_CHUNK);
            audioBufferForServer = audioBufferForServer.slice(SAMPLES_PER_CHUNK);
            sendAudioChunkToServer(chunkToProcess);
        }
        
        let sumSquares = 0.0;
        for (const sample of inputData) sumSquares += sample * sample;
        const rms = Math.sqrt(sumSquares / inputData.length);
        if (window.uiManager && typeof window.uiManager.updateAudioVisualization === 'function') {
            window.uiManager.updateAudioVisualization(rms);
        }
    };

    mediaStreamSource.connect(scriptProcessorNode);
    scriptProcessorNode.connect(ac.destination);

    debugLog('Audio pipeline connected to ScriptProcessor.');
    await connectWebSocket();
}

async function cleanUpAudioResources(sendEndOfStream = true) {
    debugLog("Cleaning up audio resources...");
    if (scriptProcessorNode) {
        scriptProcessorNode.disconnect();
        scriptProcessorNode.onaudioprocess = null; 
        // scriptProcessorNode = null; // Can be reused if context is the same
    }
    if (mediaStreamSource) {
        mediaStreamSource.disconnect();
        // mediaStreamSource = null;
    }
    if (microphoneStream) {
        microphoneStream.getTracks().forEach(track => track.stop());
        microphoneStream = null;
        debugLog('Microphone stream stopped.');
    }
    // Don't close/nullify audioContext here as it can be reused.
    // It's closed by user action or if page unloads.

    audioBufferForServer = new Float32Array(0); // Clear buffer
    await closeWebSocket(sendEndOfStream);
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
        debugLog("[AUDIO-SYSTEM] Using pipeline options from optimizationManager:", queryParams);
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
            debugLog("[AUDIO-SYSTEM] Including VAD settings from optimizationManager:", vadSettings);
        } else {
            debugLog("[AUDIO-SYSTEM] VAD is disabled by pipeline options, not including VAD settings.");
        }
    } else {
        console.warn("[AUDIO-SYSTEM] optimizationManager.getCurrentVadSettings() not available. VAD settings might be incorrect if VAD is enabled.");
    }

    const queryString = new URLSearchParams(queryParams).toString();
    const path = `/ws/audio?${queryString}`;
    const fullUrl = `${protocol}://${host}${path}`;
    debugLog("[AUDIO-SYSTEM] Constructed WebSocket URL:", fullUrl);
    return fullUrl;
}

async function connectWebSocket() {
    if (webSocket && webSocket.readyState === WebSocket.OPEN) {
        debugLog('WebSocket already open. Checking if VAD settings need update.');
        if (window.optimizationManager && typeof window.optimizationManager.getCurrentVadSettings === 'function') {
            const pipelineOptions = window.optimizationManager.getCurrentPipelineOptions ? window.optimizationManager.getCurrentPipelineOptions() : { DisableVad: false };
            if (!pipelineOptions.DisableVad) {
                const vadSettings = window.optimizationManager.getCurrentVadSettings();
                if (vadSettings) sendWebSocketMessage({ type: 'vad_settings', payload: vadSettings });
            } else {
                 debugLog("VAD disabled, not sending VAD settings update on existing WebSocket.");
            }
        }
        return;
    }
    if (webSocket && webSocket.readyState === WebSocket.CONNECTING) {
        debugLog("WebSocket is already connecting. Aborting new connection attempt.");
        return Promise.reject(new Error("WebSocket connection attempt already in progress."));
    }

    const wsUrl = getWebSocketUrlWithParams();
    debugLog(`Connecting to WebSocket: ${wsUrl}`);
    webSocket = new WebSocket(wsUrl);
    webSocket.binaryType = 'arraybuffer';

    return new Promise((resolve, reject) => {
        webSocket.onopen = () => {
            debugLog('WebSocket connection established.');
            if (window.optimizationManager && typeof window.optimizationManager.getCurrentVadSettings === 'function') {
                const pipelineOptions = window.optimizationManager.getCurrentPipelineOptions ? window.optimizationManager.getCurrentPipelineOptions() : { DisableVad: false };
                if (!pipelineOptions.DisableVad) {
                    const vadSettings = window.optimizationManager.getCurrentVadSettings();
                    if (vadSettings) {
                        sendWebSocketMessage({ type: 'vad_settings', payload: vadSettings });
                        debugLog('Sent initial VAD settings via WebSocket:', vadSettings);
                    }
                } else {
                    debugLog("VAD disabled, not sending initial VAD settings.");
                }
            }
            if (window.uiManager && typeof window.uiManager.hideStatus === 'function') {
                window.uiManager.hideStatus();
            }
            resolve();
        };

        webSocket.onmessage = (event) => {
            handleWebSocketMessage(event);
        };

        webSocket.onerror = (error) => {
            console.error('WebSocket Error:', error);
            if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
                window.uiManager.showStatus("WebSocket Fehler. Aufnahme gestoppt.", true);
            }
            // Ensure recording is marked as stopped and UI is updated
            if (isRecordingActive) {
                isRecordingActive = false; // Critical to set this before calling updateUIAfterStopRecording
                updateUIAfterStopRecording(); 
            }
            webSocket = null; // Nullify to allow reconnect attempts
            reject(new Error('WebSocket connection error')); // Reject promise for connectWebSocket
        };

        webSocket.onclose = (event) => {
            debugLog(`WebSocket connection closed. Code: ${event.code}, Reason: ${event.reason}, WasClean: ${event.wasClean}`);
            const wasRecording = isRecordingActive;
            isRecordingActive = false; // Always set recording to false on close

            if (wasRecording && !event.wasClean) {
                if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
                    window.uiManager.showStatus("WebSocket unerwartet geschlossen. Aufnahme gestoppt.", true);
                }
            }
            // Update UI regardless of clean close, as recording has stopped
            updateUIAfterStopRecording(); 
            webSocket = null; // Clear instance to allow new connections
            // Do not reject here if onopen was already called, as the promise would have resolved.
            // The error handling for unexpected close during active recording is handled above.
        };
    });
}

async function closeWebSocket(sendEndOfStream = true) {
    if (webSocket) {
        if (sendEndOfStream && webSocket.readyState === WebSocket.OPEN) {
            debugLog("Sending end_of_stream to WebSocket.");
            sendWebSocketMessage({ type: 'end_of_stream' });
            // Wait a moment for the message to be sent before closing
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        if (webSocket.readyState === WebSocket.OPEN || webSocket.readyState === WebSocket.CONNECTING) {
           webSocket.close(1000, "Client initiated disconnect");
        }
        webSocket = null; // Ensure it's nulled after calling close
        debugLog('WebSocket connection closed or closing initiated.');
    }
}

function sendWebSocketMessage(messageObject) {
    if (webSocket && webSocket.readyState === WebSocket.OPEN) {
        try {
            webSocket.send(JSON.stringify(messageObject));
        } catch (e) {
            console.error("Error sending JSON message via WebSocket:", e, messageObject);
        }
    } else {
        // console.warn('[WebSocket] Attempted to send JSON message but WebSocket is not open:', messageObject);
    }
}

function sendAudioChunkToServer(float32ArrayChunk) {
    if (webSocket && webSocket.readyState === WebSocket.OPEN) {
        // Convert Float32Array to Int16Array (PCM16)
        const pcmData = new Int16Array(float32ArrayChunk.length);
        for (let i = 0; i < float32ArrayChunk.length; i++) {
            pcmData[i] = Math.max(-32768, Math.min(32767, float32ArrayChunk[i] * 32768));
        }
        try {
            webSocket.send(pcmData.buffer); // Send as ArrayBuffer
        } catch (e) {
            console.error("Error sending audio chunk via WebSocket:", e);
        }
    } else {
        // console.warn('[WebSocket] Attempted to send audio chunk but WebSocket is not open.');
    }
}

async function handleWebSocketMessage(event) {
    if (typeof event.data === 'string') {
        try {
            const msg = JSON.parse(event.data);
            // debugLog('Received JSON from server:', msg); // Can be too verbose

            switch (msg.type) {
                case 'vad_settings_updated':
                case 'pipeline_options_updated':
                    debugLog(`Server confirmed ${msg.type}:`, msg.payload);
                    if (window.optimizationManager && typeof window.optimizationManager.updateSettingsFromServer === 'function') {
                        window.optimizationManager.updateSettingsFromServer(msg.payload);
                    }
                    break;
                case 'transcription_update':
                    if (window.uiManager && typeof window.uiManager.updateRecognizedText === 'function') {
                        window.uiManager.updateRecognizedText(msg.payload.text, msg.payload.isFinal);
                    }
                    if (msg.payload.isFinal && msg.payload.text) {
                        debugLog("Final transcription received:", msg.payload.text);
                        // Latency tracking for transcription can happen here or in uiManager
                        if (window.optimizationManager && typeof window.optimizationManager.trackLatency === 'function') {
                            window.optimizationManager.trackLatency('transcriptionReceived');
                        }
                    }
                    break;
                case 'final_transcription': // Deprecated if transcription_update with isFinal=true is used
                     if (window.uiManager && typeof window.uiManager.updateRecognizedText === 'function') {
                        window.uiManager.updateRecognizedText(msg.payload.prompt, true);
                        debugLog("Legacy final_transcription received:", msg.payload.prompt);
                    }
                    break;
                case 'llm_token':
                    if (window.uiManager && typeof window.uiManager.appendTokenToBotMessage === 'function') {
                        // Ensure a bot message container exists, using current model/voice
                        if (!window.uiManager.currentBotMessageDiv) {
                             if (window.optimizationManager && typeof window.optimizationManager.trackLatency === 'function') {
                                window.optimizationManager.trackLatency('llmResponseStart');
                            }
                            window.uiManager.createBotMessage("", currentBotModel, currentBotVoice);
                        }
                        window.uiManager.appendTokenToBotMessage(msg.payload.token);
                    }
                    break;
                case 'llm_reply': // Final LLM reply (if not fully streamed or for summary)
                    debugLog('Received final LLM reply object:', msg.payload);
                    if (window.uiManager && typeof window.uiManager.finalizeBotMessage === 'function') {
                        // If streaming was not used or to confirm full text
                        window.uiManager.finalizeBotMessage(msg.payload.reply, currentBotModel, currentBotVoice);
                    }
                    if (msg.payload.latency_info && window.uiManager && typeof window.uiManager.updateMessageLatency === 'function') {
                        window.uiManager.updateMessageLatency(msg.payload.latency_info);
                    }
                    // Determine if TTS should play based on server hint and client settings
                    let pipelineOpts = {};
                    if (window.optimizationManager && typeof window.optimizationManager.getCurrentPipelineOptions === 'function') {
                        pipelineOpts = window.optimizationManager.getCurrentPipelineOptions();
                    }
                    const clientWantsTTS = !pipelineOpts.DisableTts;
                    const serverWillSendTTS = !msg.payload.disableTts; // Server hint
                    isTTSSpeaking = clientWantsTTS && serverWillSendTTS;
                    if (isTTSSpeaking) {
                        debugLog("TTS will play for this reply.");
                         if (window.optimizationManager && typeof window.optimizationManager.trackLatency === 'function') {
                            window.optimizationManager.trackLatency('ttsStart');
                        }
                    } else {
                        debugLog("TTS is disabled for this reply (client or server).");
                         if (window.optimizationManager && typeof window.optimizationManager.trackLatency === 'function') {
                            window.optimizationManager.trackLatency('ttsEnd'); // No TTS, so it ends immediately
                        }
                    }
                    break;
                case 'audio_chunk_info':
                    lastReceivedAudioChunkIndex = msg.payload.index;
                    if (msg.payload.isFinal) {
                        allAudioChunksReceived = true;
                    }
                    isTTSSpeaking = true; // Actively receiving TTS data
                    break;
                case 'audio_stream_end':
                    debugLog('Server signaled audio_stream_end for current TTS.');
                    allAudioChunksReceived = true;
                    // isTTSSpeaking will be set to false by the ttsPlayLoop when all chunks are played
                    // or if resetTTSPlaybackState is called.
                    // However, if no chunks were ever played (e.g. empty TTS), reset it here.
                    if (indexedAudioChunks.size === 0 && !isCurrentlyPlayingTTS) {
                        debugLog('All TTS chunks played or none to play after audio_stream_end. Resetting TTS state.');
                        resetTTSPlaybackState(); // This sets isTTSSpeaking to false
                         if (window.optimizationManager && typeof window.optimizationManager.trackLatency === 'function') {
                            window.optimizationManager.trackLatency('ttsEnd');
                        }
                    }
                    break;
                case 'error':
                    console.error('[WebSocket] Server error message:', msg.payload.message);
                    if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
                        window.uiManager.showStatus(`Server Fehler: ${msg.payload.message}`, true);
                    }
                    break;
                case 'ping':
                    sendWebSocketMessage({ type: 'pong', timestamp: msg.timestamp });
                    break;
                default:
                    console.warn('Received unhandled JSON message type:', msg.type, msg);
            }
        } catch (e) {
            console.error("Error parsing JSON from server or handling message:", e, event.data);
        }
    } else if (event.data instanceof ArrayBuffer) { // Binary data for Progressive TTS
        // debugLog(`Received binary audio data (ArrayBuffer), size: ${event.data.byteLength} bytes. Expected for chunk index: ${lastReceivedAudioChunkIndex}`);
        const ac = getAudioContext();
        if (!ac) {
            console.error("Cannot process binary audio, AudioContext not available.");
            return;
        }
        if (lastReceivedAudioChunkIndex === -1) {
            console.error("Received binary audio data, but lastReceivedAudioChunkIndex is not set. 'audio_chunk_info' might be missing or out of order.");
            return;
        }
        try {
            const audioBuffer = await ac.decodeAudioData(event.data);
            indexedAudioChunks.set(lastReceivedAudioChunkIndex, audioBuffer);
            // debugLog(`Queued TTS audio chunk #${lastReceivedAudioChunkIndex}. Total queued: ${indexedAudioChunks.size}`);
            lastReceivedAudioChunkIndex = -1; // Reset for the next 'audio_chunk_info'
            ttsPlayLoop();
        } catch (e) {
            console.error('Error decoding audio data for TTS:', e, event.data);
        }
    } else {
        console.warn("[WebSocket] Received unknown message type:", event.data);
    }
}


function updateUIAfterAudioInitAttempt(success, reason = '') {
    if (window.uiManager && typeof window.uiManager.updateButtonStates === 'function') {
        window.uiManager.updateButtonStates(isRecordingActive); // Pass current recording state
    }
    if (success) {
        if (window.uiManager && typeof window.uiManager.hideStatus === 'function') {
            window.uiManager.hideStatus();
        }
        debugLog(`Audio initialization/start successful. isRecordingActive: ${isRecordingActive}. Reason: ${reason || 'N/A'}`);
    } else {
        if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
            let message = "Mikrofonzugriff oder Audio-Initialisierung fehlgeschlagen.";
            if (reason === 'AudioContext blocked') {
                message = "Audiowiedergabe blockiert. Bitte klicken Sie auf die Aufnahme-Schaltfläche.";
            } else if (reason === 'Microphone access denied') {
                message = "Mikrofonzugriff verweigert. Bitte Zugriff erlauben und erneut versuchen.";
            } else if (reason && reason.toLowerCase().includes('audiocontext not running')) {
                message = `Audio System nicht bereit. Bitte klicken Sie auf die Aufnahme-Schaltfläche. (${reason})`;
            } else if (reason) {
                message = `Fehler: ${reason}.`;
            }
            window.uiManager.showStatus(message, true);
        }
        console.warn(`Audio initialization/start failed: ${reason}`);
    }
}

function updateUIAfterStopRecording() {
    if (window.uiManager && typeof window.uiManager.updateButtonStates === 'function') {
        window.uiManager.updateButtonStates(false); // Explicitly false as recording has stopped
    }
    // Optionally, show a status if it wasn't an error that stopped it
    // if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
    //     window.uiManager.showStatus("Aufnahme gestoppt.");
    // }
    debugLog("UI updated after stopping recording.");
}

// --- Settings Change Handler ---
export async function handleSettingsChange({ type }) {
    debugLog(`[AUDIO-SYSTEM] handleSettingsChange called with type: ${type}`);
    if (!window.optimizationManager) {
        console.error("[AUDIO-SYSTEM] optimizationManager not found. Cannot handle settings change.");
        return;
    }

    if (type === 'pipeline') {
        debugLog("[AUDIO-SYSTEM] Pipeline settings changed (e.g., model, voice, language, TTS mode).");
        if (isRecordingActive) {
            debugLog("[AUDIO-SYSTEM] Recording is active. Restarting recording with new pipeline settings.");
            if (window.uiManager && typeof window.uiManager.showStatus === 'function') {
                window.uiManager.showStatus("Pipeline-Einstellungen geändert. Audio wird neu gestartet...", false);
            }
            await stopRecording(false); // Stop current recording without sending end_of_stream
            // Add a small delay to ensure resources are released before reconnecting
            await new Promise(resolve => setTimeout(resolve, 250)); 
            await startRecording(); // This will use the new settings from optimizationManager
        } else {
            debugLog("[AUDIO-SYSTEM] Recording not active. New pipeline settings will be used on next start.");
            // Optionally, if WebSocket is connected but idle, could close and reopen it,
            // but typically it's better to do it on next recording start.
            if (webSocket && webSocket.readyState === WebSocket.OPEN) {
                debugLog("[AUDIO-SYSTEM] Closing idle WebSocket due to pipeline settings change.");
                await closeWebSocket(false);
            }
        }
    } else if (type === 'vad') {
        debugLog("[AUDIO-SYSTEM] VAD settings changed.");
        const pipelineOptions = window.optimizationManager.getCurrentPipelineOptions ? window.optimizationManager.getCurrentPipelineOptions() : { DisableVad: false };
        if (!pipelineOptions.DisableVad) {
            if (webSocket && webSocket.readyState === WebSocket.OPEN) {
                const vadSettings = window.optimizationManager.getCurrentVadSettings();
                if (vadSettings) {
                    sendWebSocketMessage({ type: 'vad_settings', payload: vadSettings });
                    debugLog("[AUDIO-SYSTEM] Sent updated VAD settings to server.", vadSettings);
                }
            } else {
                debugLog("[AUDIO-SYSTEM] WebSocket not open or VAD disabled. New VAD settings will be sent on next connection/enable.");
            }
        } else {
            debugLog("[AUDIO-SYSTEM] VAD is currently disabled by pipeline options. No VAD settings sent.");
        }
    }
}

// --- General Audio Playback (e.g., for non-progressive TTS if server sends full audio files) ---
// This is a simpler queue for playing single audio buffers if needed.
// Progressive TTS uses indexedAudioChunks and playLoop.
let generalAudioQueue = [];
let isGeneralAudioPlaying = false;

export async function playGeneralAudio(arrayBuffer) {
    if (!arrayBuffer || arrayBuffer.byteLength === 0) {
        debugLog("playGeneralAudio called with empty data.");
        return;
    }
    const ac = getAudioContext();
    if (!ac) {
        console.error("Cannot play general audio, AudioContext not available.");
        return;
    }
    try {
        const audioBuffer = await ac.decodeAudioData(arrayBuffer.slice(0)); // Use slice(0) to copy buffer for safety
        generalAudioQueue.push(audioBuffer);
        if (!isGeneralAudioPlaying) {
            processGeneralAudioQueue();
        }
    } catch (e) {
        console.error("Error decoding general audio data:", e);
    }
}

async function processGeneralAudioQueue() {
    if (generalAudioQueue.length === 0) {
        isGeneralAudioPlaying = false;
        return;
    }
    isGeneralAudioPlaying = true;
    const audioBuffer = generalAudioQueue.shift();
    const ac = getAudioContext();
    const sourceNode = ac.createBufferSource();
    sourceNode.buffer = audioBuffer;
    sourceNode.connect(ac.destination);
    sourceNode.onended = () => {
        processGeneralAudioQueue(); // Play next
    };
    sourceNode.start();
    debugLog("Playing general audio from queue.");
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