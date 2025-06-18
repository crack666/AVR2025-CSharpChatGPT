class AudioProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        // Using processorOptions to get the target chunk size
        this.samplesPerChunk = options.processorOptions.samplesPerChunk || 320;
        this.audioBuffer = new Float32Array(this.samplesPerChunk * 2); // Buffer to hold incoming audio
        this.bufferPosition = 0;
        this.lastRms = 0;
        this.rmsUpdateInterval = 5; // Send RMS update every 5 chunks
        this.chunkCounter = 0;
    }

    process(inputs, outputs, parameters) {
        // We expect a single input, with a single channel of audio data.
        const input = inputs[0];
        if (!input || input.length === 0) {
            return true; // Keep processor alive
        }
        const channelData = input[0];

        // If there's no data, do nothing.
        if (!channelData) {
            // Still send occasional RMS updates to show the meter is alive but silent
            if (this.chunkCounter++ % this.rmsUpdateInterval === 0 && this.lastRms > 0) {
                this.lastRms = 0;
                this.port.postMessage({ type: 'rmsUpdate', rms: 0 });
            }
            return true;
        }

        // Append new data to our buffer
        // Note: channelData can be 128 samples, the standard render quantum.
        this.audioBuffer.set(channelData, this.bufferPosition);
        this.bufferPosition += channelData.length;

        // Process full chunks from the buffer
        while (this.bufferPosition >= this.samplesPerChunk) {
            const chunkToSend = this.audioBuffer.slice(0, this.samplesPerChunk);

            // Calculate RMS for visualization
            let sumOfSquares = 0;
            for (let i = 0; i < chunkToSend.length; i++) {
                sumOfSquares += chunkToSend[i] * chunkToSend[i];
            }
            const rms = Math.sqrt(sumOfSquares / chunkToSend.length);
            this.lastRms = rms;

            // Send the audio data chunk back to the main thread
            this.port.postMessage({
                type: 'audioData',
                buffer: chunkToSend
            });

            // Send RMS update periodically
            if (this.chunkCounter++ % this.rmsUpdateInterval === 0) {
                 this.port.postMessage({ type: 'rmsUpdate', rms: this.lastRms });
            }

            // Shift the main buffer
            this.audioBuffer.copyWithin(0, this.samplesPerChunk, this.bufferPosition);
            this.bufferPosition -= this.samplesPerChunk;
        }

        return true; // Keep the processor alive
    }
}

registerProcessor('audio-processor', AudioProcessor);
