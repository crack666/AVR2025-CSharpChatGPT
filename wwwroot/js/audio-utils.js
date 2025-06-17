// --- Audio Utility Functions ---

export const IS_DEBUG_MODE = true; // Enable/disable extensive logging

export function debugLog(...args) {
    if (IS_DEBUG_MODE) {
        // Check if the first argument is already a styled string for console
        if (typeof args[0] === 'string' && args[0].startsWith('%c')) {
            console.log(...args);
        } else {
            console.log('%c[AUDIO-UTILS]', 'color: green', ...args);
        }
    }
}

// Helper for Blob indexing (TTS audio chunks)
let _lastKnownChunkInfoIndex = -1;

export function setLastKnownAudioChunkInfoIndex(index) {
    _lastKnownChunkInfoIndex = index;
    // debugLog(`[Utils] LastKnownChunkInfoIndex set to: ${index}`);
}

export function getLastKnownAudioChunkInfoIndex() {
    return _lastKnownChunkInfoIndex;
}

export function resetLastKnownAudioChunkInfoIndex() {
    // debugLog(`[Utils] Resetting LastKnownChunkInfoIndex from: ${_lastKnownChunkInfoIndex}`);
    _lastKnownChunkInfoIndex = -1;
}

export function convertFloat32ToPcm16(buffer) {
    let l = buffer.length;
    const buf = new Int16Array(l);
    while (l--) {
        buf[l] = Math.min(1, buffer[l]) * 0x7FFF; // Clamp to [-1, 1] and scale
    }
    return buf.buffer;
}
