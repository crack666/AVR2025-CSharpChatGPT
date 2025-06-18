// --- Microphone Processing State & Functions ---
let mediaStreamSource = null;
let audioWorkletNode = null;

// Configuration callbacks
let getAudioContext = null;
let onAudioChunkProcessed = null;
let debugLog = null;
let getIsRecordingActive = null;
let updateAudioVisualization = null;
let samplesPerChunk = 320; // Default

const WORKLET_PROCESSOR_NAME = 'audio-processor';
const WORKLET_URL = 'js/audio-processor.js';

export async function initMicrophone(config) {
    getAudioContext = config.getAudioContext;
    onAudioChunkProcessed = config.onAudioChunkProcessed;
    debugLog = config.debugLog;
    getIsRecordingActive = config.getIsRecordingActive;
    updateAudioVisualization = config.updateAudioVisualization;
    samplesPerChunk = config.samplesPerChunk || samplesPerChunk;

    try {
        const audioContext = getAudioContext();
        if (!audioContext) {
            throw new Error("AudioContext not available for worklet initialization.");
        }
        // Pre-load the worklet processor
        await audioContext.audioWorklet.addModule(WORKLET_URL);
        debugLog(`[Microphone] AudioWorklet processor '${WORKLET_PROCESSOR_NAME}' loaded from ${WORKLET_URL}.`);
    } catch (error) {
        console.error("[Microphone] Failed to load AudioWorklet module:", error);
        throw new Error("Could not initialize microphone worklet.");
    }

    debugLog("[Microphone] Initialized.");
}

export async function startMicrophoneProcessing(stream) {
    const audioContext = getAudioContext();
    if (!audioContext || audioContext.state === 'closed') {
        throw new Error("Microphone module requires an active AudioContext.");
    }
    if (!stream || !stream.active) {
        throw new Error("A valid, active MediaStream is required.");
    }

    mediaStreamSource = audioContext.createMediaStreamSource(stream);
    debugLog("[Microphone] MediaStreamSource created");

    // Create the AudioWorkletNode
    audioWorkletNode = new AudioWorkletNode(audioContext, WORKLET_PROCESSOR_NAME, {
        processorOptions: {
            samplesPerChunk: samplesPerChunk
        }
    });
    debugLog("[Microphone] AudioWorkletNode created.");

    // Set up the message listener to receive data from the worklet
    audioWorkletNode.port.onmessage = (event) => {
        if (!getIsRecordingActive()) return;

        if (event.data.type === 'audioData') {
            // Forward the processed audio chunk to the audio-system
            onAudioChunkProcessed(event.data.buffer);
        } else if (event.data.type === 'rmsUpdate') {
            // Forward the RMS value to the UI manager
            updateAudioVisualization(event.data.rms);
        }
    };

    // Connect the nodes: Microphone -> Worklet -> Destination (for potential local playback/monitoring if needed)
    mediaStreamSource.connect(audioWorkletNode);
    // We connect to the destination to ensure the graph is processed, but the worklet does not pass audio through.
    audioWorkletNode.connect(audioContext.destination);

    debugLog("[Microphone] Audio pipeline (Worklet) fully connected and processing started.");
}

export function stopMicrophoneProcessing() {
    if (mediaStreamSource) {
        mediaStreamSource.disconnect();
        mediaStreamSource = null;
        debugLog("[Microphone] MediaStreamSource disconnected.");
    }
    if (audioWorkletNode) {
        audioWorkletNode.port.onmessage = null; // Remove listener
        audioWorkletNode.disconnect();
        audioWorkletNode = null;
        debugLog("[Microphone] AudioWorkletNode disconnected.");
    }
    // The raw microphone stream tracks are stopped in audio-system.js
}
