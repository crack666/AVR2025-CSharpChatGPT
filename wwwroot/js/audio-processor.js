class AudioProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this.samplesPerChunk = options.processorOptions.samplesPerChunk;
        this.audioBuffer = new Float32Array(0);
        this.port.onmessage = (event) => {
            // We can receive messages here if needed in the future
        };
    }

    process(inputs, outputs, parameters) {
        const input = inputs[0];
        if (input.length === 0 || !input[0]) {
            return true; // Keep processor alive
        }

        const inputData = input[0];

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
