// --- Audio Utility Functions ---

// Log levels: TRACE = 0, DEBUG = 1, INFO = 2, WARN = 3, ERROR = 4
export const LOG_LEVELS = {
    TRACE: 0,
    DEBUG: 1,
    INFO: 2,
    WARN: 3,
    ERROR: 4
};

// Current log level - change this to control what gets logged
export const CURRENT_LOG_LEVEL = LOG_LEVELS.DEBUG; // Set to DEBUG by default

export function debugLog(...args) {
    logAtLevel(LOG_LEVELS.DEBUG, ...args);
}

export function traceLog(...args) {
    logAtLevel(LOG_LEVELS.TRACE, ...args);
}

export function infoLog(...args) {
    logAtLevel(LOG_LEVELS.INFO, ...args);
}

export function warnLog(...args) {
    logAtLevel(LOG_LEVELS.WARN, ...args);
}

export function errorLog(...args) {
    logAtLevel(LOG_LEVELS.ERROR, ...args);
}

function logAtLevel(level, ...args) {
    if (level < CURRENT_LOG_LEVEL) {
        return; // Skip logging if level is below current threshold
    }
    
    const levelNames = ['TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR'];
    const levelColors = ['color: gray', 'color: green', 'color: blue', 'color: orange', 'color: red'];
    
    // Check if the first argument is already a styled string for console
    if (typeof args[0] === 'string' && args[0].startsWith('%c')) {
        console.log(...args);
    } else {
        console.log(`%c[AUDIO-UTILS ${levelNames[level]}]`, levelColors[level], ...args);
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
