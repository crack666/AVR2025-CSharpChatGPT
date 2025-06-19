// AudioWorklet Logging System (separate from main thread)
// Log levels: TRACE = 0, DEBUG = 1, INFO = 2, WARN = 3, ERROR = 4
const LOG_LEVELS = {
    TRACE: 0,
    DEBUG: 1,
    INFO: 2,
    WARN: 3,
    ERROR: 4
};

// Current log level for AudioWorklet - change this to control what gets logged
const CURRENT_LOG_LEVEL = LOG_LEVELS.DEBUG; // Set to DEBUG by default

function logAtLevel(level, ...args) {
    if (level < CURRENT_LOG_LEVEL) {
        return; // Skip logging if level is below current threshold
    }
    
    const levelNames = ['TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR'];
    const levelColors = ['color: gray', 'color: green', 'color: blue', 'color: orange', 'color: red'];
    
    console.log(`%c[AudioProcessor ${levelNames[level]}]`, levelColors[level], ...args);
}

function traceLog(...args) {
    logAtLevel(LOG_LEVELS.TRACE, ...args);
}

function debugLog(...args) {
    logAtLevel(LOG_LEVELS.DEBUG, ...args);
}

class AudioProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this.samplesPerChunk = options.processorOptions.samplesPerChunk;
        this.audioBuffer = new Float32Array(0);
        this.sampleCount = 0;
        this.port.onmessage = (event) => {
            // We can receive messages here if needed in the future
        };
        
        // Log initialization info
        debugLog(`Initialized with samplesPerChunk: ${this.samplesPerChunk}, sampleRate: ${sampleRate}`);
    }

    process(inputs, outputs, parameters) {
        const input = inputs[0];
        if (input.length === 0 || !input[0]) {
            return true; // Keep processor alive
        }

        const inputData = input[0];
        this.sampleCount += inputData.length;

        // [TRACE LOG] Very verbose input stats logging - only for detailed debugging
        if (this.sampleCount % 1000 < inputData.length) {
            let min = 1.0, max = -1.0, sum = 0.0;
            for (let i = 0; i < inputData.length; i++) {
                const sample = inputData[i];
                if (sample < min) min = sample;
                if (sample > max) max = sample;
                sum += Math.abs(sample);
            }
            const avgAbsolute = sum / inputData.length;
            traceLog(`Input Stats: ${inputData.length} samples, min: ${min.toFixed(6)}, max: ${max.toFixed(6)}, avgAbs: ${avgAbsolute.toFixed(6)}, sampleRate: ${sampleRate}`);
        }

        // Append new data to our buffer
        const newBuffer = new Float32Array(this.audioBuffer.length + inputData.length);
        newBuffer.set(this.audioBuffer);
        newBuffer.set(inputData, this.audioBuffer.length);
        this.audioBuffer = newBuffer;

        // Process chunks
        while (this.audioBuffer.length >= this.samplesPerChunk) {
            const chunkToProcess = this.audioBuffer.slice(0, this.samplesPerChunk);
            this.audioBuffer = this.audioBuffer.slice(this.samplesPerChunk);
            
            // Calculate RMS for the chunk
            let sumSquares = 0.0;
            for (const sample of chunkToProcess) {
                sumSquares += sample * sample;
            }
            const rms = Math.sqrt(sumSquares / chunkToProcess.length);

            // [TRACE LOG] Very verbose RMS logging - only for detailed debugging
            if (Math.random() < 0.1) { // 10% chance to avoid spam
                traceLog(`Processed chunk: ${chunkToProcess.length} samples, RMS: ${rms.toFixed(6)}`);
            }

            // Send chunk and RMS to the main thread in separate messages
            this.port.postMessage({
                type: 'audioData',
                buffer: chunkToProcess
            });
            this.port.postMessage({
                type: 'rms',
                rms: rms
            });
        }

        return true; // Keep processor alive
    }
}

registerProcessor('audio-processor', AudioProcessor);
