// --- AudioContext State & Functions ---
let audioContext = null; // Singleton AudioContext
let targetSampleRate;
let debugLogFunc;

export function initAudioContextModule(sampleRate, debugLog) {
    targetSampleRate = sampleRate;
    debugLogFunc = debugLog;
}

export function getAudioContext() {
    if (!audioContext) {
        const AudioContextGlobal = window.AudioContext || window.webkitAudioContext;
        if (AudioContextGlobal) {
            try {
                audioContext = new AudioContextGlobal({ sampleRate: targetSampleRate });
                if (debugLogFunc) debugLogFunc(`AudioContext created (state: ${audioContext.state}). Sample rate: ${audioContext.sampleRate}`);
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

export async function resumeAudioContext() {
    const ac = getAudioContext(); // Use the local getAudioContext
    if (ac && ac.state === 'suspended') {
        try {
            await ac.resume();
            if (debugLogFunc) debugLogFunc(`AudioContext resumed (state: ${ac.state}).`);
        } catch (e) {
            console.error("Error resuming AudioContext:", e);
            throw e; // Re-throw for caller to handle
        }
    }
    return ac && ac.state === 'running';
}

// Optional: Function to explicitly close/reset the context if needed for full cleanup scenarios
export function closeAudioContext() {
    if (audioContext && audioContext.state !== 'closed') {
        audioContext.close().then(() => {
            if (debugLogFunc) debugLogFunc('AudioContext closed.');
            audioContext = null;
        }).catch(e => {
            console.error('Error closing AudioContext:', e);
            // audioContext might be in an unusable state, nullify anyway
            audioContext = null;
        });
    } else {
        audioContext = null; // Ensure it's null if already closed or never created
    }
}
