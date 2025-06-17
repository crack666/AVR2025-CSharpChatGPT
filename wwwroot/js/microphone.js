// --- Microphone Processing State & Functions ---
let mediaStreamSource = null;
let scriptProcessorNode = null;
let audioBufferForServer = new Float32Array(0);
let localMicrophoneStream = null; // Store the stream locally to manage its tracks

// Configuration and callbacks, to be set by initMicrophone
let getAudioContextCallback = null;
let onAudioChunkProcessedCallback = null;
let debugLogCallback = null;
let getIsRecordingActiveCallback = null;
let getPipelineOptionsCallback = null; // To get { DisableProgressiveTts, ... }
let getIsTTSSpeakingCallback = null;
let updateAudioVisualizationCallback = null;
let samplesPerChunk = 0;
let localAudioContext = null; // Store the audio context for node creation

export function initMicrophone(config) {
    getAudioContextCallback = config.getAudioContext;
    onAudioChunkProcessedCallback = config.onAudioChunkProcessed;
    debugLogCallback = config.debugLog;
    getIsRecordingActiveCallback = config.getIsRecordingActive;
    getPipelineOptionsCallback = config.getPipelineOptions;
    getIsTTSSpeakingCallback = config.getIsTTSSpeaking;
    updateAudioVisualizationCallback = config.updateAudioVisualization;
    samplesPerChunk = config.samplesPerChunk;

    if (debugLogCallback) debugLogCallback("[Microphone] Initialized.");
}

export async function startMicrophoneProcessing(stream) {
    if (!getAudioContextCallback || !onAudioChunkProcessedCallback || !debugLogCallback || samplesPerChunk === 0) {
        console.error("[Microphone] Not initialized properly. Call initMicrophone first.");
        throw new Error("Microphone module not initialized.");
    }

    localAudioContext = getAudioContextCallback();
    if (!localAudioContext || localAudioContext.state !== 'running') {
        throw new Error("[Microphone] AudioContext not ready or not running.");
    }

    localMicrophoneStream = stream; // Store the stream

    if (mediaStreamSource) mediaStreamSource.disconnect();
    if (scriptProcessorNode) scriptProcessorNode.disconnect();

    mediaStreamSource = localAudioContext.createMediaStreamSource(localMicrophoneStream);
    debugLogCallback('[Microphone] MediaStreamSource created');

    const bufferSize = 4096; // Standard buffer size, can be adjusted
    scriptProcessorNode = localAudioContext.createScriptProcessor(bufferSize, 1, 1);
    debugLogCallback(`[Microphone] ScriptProcessorNode created with buffer size: ${bufferSize}`);

    let audioProcessCallCount = 0;

    scriptProcessorNode.onaudioprocess = (audioProcessingEvent) => {
        audioProcessCallCount++;
        if (audioProcessCallCount === 1) {
            debugLogCallback("[Microphone] FIRST onaudioprocess callback executed!");
        }

        if (!getIsRecordingActiveCallback || !getIsRecordingActiveCallback()) {
            return;
        }

        const pipelineOptions = getPipelineOptionsCallback ? getPipelineOptionsCallback() : {};
        const progressiveTTSEnabled = !pipelineOptions.DisableProgressiveTts;
        const isTTSSpeaking = getIsTTSSpeakingCallback ? getIsTTSSpeakingCallback() : false;

        if (progressiveTTSEnabled && isTTSSpeaking) {
            return; // Pause sending mic data if progressive TTS is active and speaking
        }

        const inputData = audioProcessingEvent.inputBuffer.getChannelData(0);
        const currentServerBuffer = audioBufferForServer;
        const combinedBuffer = new Float32Array(currentServerBuffer.length + inputData.length);
        combinedBuffer.set(currentServerBuffer);
        combinedBuffer.set(inputData, currentServerBuffer.length);
        audioBufferForServer = combinedBuffer;

        while (audioBufferForServer.length >= samplesPerChunk) {
            const chunkToProcess = audioBufferForServer.slice(0, samplesPerChunk);
            audioBufferForServer = audioBufferForServer.slice(samplesPerChunk);
            if (onAudioChunkProcessedCallback) {
                onAudioChunkProcessedCallback(chunkToProcess);
            }
        }

        let sumSquares = 0.0;
        for (const sample of inputData) sumSquares += sample * sample;
        const rms = Math.sqrt(sumSquares / inputData.length);

        if (updateAudioVisualizationCallback) {
            updateAudioVisualizationCallback(rms);
        }
    };

    mediaStreamSource.connect(scriptProcessorNode);
    debugLogCallback('[Microphone] MediaStreamSource connected to ScriptProcessorNode');

    scriptProcessorNode.connect(localAudioContext.destination); // Connect to destination to keep processing alive
    debugLogCallback('[Microphone] ScriptProcessorNode connected to AudioContext destination');
    debugLogCallback('[Microphone] Audio pipeline fully connected and processing started.');
}

export function stopMicrophoneProcessing() {
    debugLogCallback("[Microphone] Stopping microphone processing...");
    if (scriptProcessorNode) {
        scriptProcessorNode.disconnect();
        scriptProcessorNode.onaudioprocess = null;
        // scriptProcessorNode = null; // Keep for potential reuse if context doesn't change
    }
    if (mediaStreamSource) {
        mediaStreamSource.disconnect();
        // mediaStreamSource = null;
    }

    if (localMicrophoneStream) {
        localMicrophoneStream.getTracks().forEach(track => track.stop());
        debugLogCallback('[Microphone] Microphone stream tracks stopped.');
        localMicrophoneStream = null;
    }
    
    audioBufferForServer = new Float32Array(0); // Clear buffer
    debugLogCallback("[Microphone] Processing stopped and resources cleaned up.");
}
