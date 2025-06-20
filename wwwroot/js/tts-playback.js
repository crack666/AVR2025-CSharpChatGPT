// --- Progressive TTS Playback State ---
const indexedAudioChunks = new Map();
let nextPlaybackIndex = 0;
let currentIsCurrentlyPlayingTTS = false;
let currentAllAudioChunksReceived = false;
let currentIsTTSLoopActive = false;
export let lastReceivedAudioChunkIndex = -1;

// Dependencies that will be set by an init function
let getAudioContextFunc;
let debugLogFunc;
let updateIsTTSSpeakingFunc; // Callback to update isTTSSpeaking in audio-system.js
let getAllAudioSourcesFunc; // Callback to get allAudioSources array
let getCurrentBotFunc; // Callback to get currentBot object

export function initTtsPlayback(getAudioContext, debugLog, updateIsTTSSpeaking, getAllAudioSources, getCurrentBot) {
    getAudioContextFunc = getAudioContext;
    debugLogFunc = debugLog;
    updateIsTTSSpeakingFunc = updateIsTTSSpeaking;
    getAllAudioSourcesFunc = getAllAudioSources;
    getCurrentBotFunc = getCurrentBot;
}

export function scheduleTTSChunk() {
    if (indexedAudioChunks.has(nextPlaybackIndex)) {
        const buffer = indexedAudioChunks.get(nextPlaybackIndex);
        indexedAudioChunks.delete(nextPlaybackIndex);

        debugLogFunc(`Playing TTS chunk #${nextPlaybackIndex}, duration=${buffer.duration.toFixed(2)}s, remaining TTS chunks=${indexedAudioChunks.size}`);

        const audioContextRef = getAudioContextFunc();
        if (!audioContextRef) {
            console.error("Cannot play TTS chunk, AudioContext not available.");
            currentIsCurrentlyPlayingTTS = false;
            if (updateIsTTSSpeakingFunc) updateIsTTSSpeakingFunc(false);
            return;
        }

        const src = audioContextRef.createBufferSource();
        src.buffer = buffer;
        src.connect(audioContextRef.destination);

        const allAudioSources = getAllAudioSourcesFunc();
        if (allAudioSources) { // Ensure it exists
            allAudioSources.push(src);
        }
        const currentBot = getCurrentBotFunc();
        if (currentBot && currentBot.audioSources) {
            currentBot.audioSources.push(src);
        }

        currentIsCurrentlyPlayingTTS = true;
        if (updateIsTTSSpeakingFunc) updateIsTTSSpeakingFunc(true);
        const playedChunkIndex = nextPlaybackIndex;
        nextPlaybackIndex++;

        src.onended = () => {
            debugLogFunc(`Finished playing TTS chunk #${playedChunkIndex}`);
            currentIsCurrentlyPlayingTTS = false;
            if (updateIsTTSSpeakingFunc) updateIsTTSSpeakingFunc(false);
            
            const allSources = getAllAudioSourcesFunc();
            if (allSources) { // Ensure it exists
                const indexInAll = allSources.indexOf(src);
                if (indexInAll > -1) allSources.splice(indexInAll, 1);
            }
            
            const bot = getCurrentBotFunc();
            if (bot && bot.audioSources) {
                const indexInBot = bot.audioSources.indexOf(src);
                if (indexInBot > -1) bot.audioSources.splice(indexInBot, 1);
            }            // Continue playing next chunks
            ttsPlayLoop(); 

            if (currentAllAudioChunksReceived && indexedAudioChunks.size === 0 && !currentIsCurrentlyPlayingTTS) {
                debugLogFunc('All received TTS chunks have been played. Resetting TTS playback state.');
                resetTTSPlaybackState();
            }
        };
        src.start();
    }
}

export function ttsPlayLoop() {
    if (!currentIsTTSLoopActive) {
        currentIsTTSLoopActive = true;
        debugLogFunc('TTS PlayLoop started.');
    }
    
    // If already playing, wait for current chunk to finish
    if (currentIsCurrentlyPlayingTTS) {
        debugLogFunc(`TTS PlayLoop: Already playing chunk, waiting...`);
        return;
    }
    
    // Try to play the next expected chunk
    if (indexedAudioChunks.has(nextPlaybackIndex)) {
        debugLogFunc(`TTS PlayLoop: Found chunk #${nextPlaybackIndex}, scheduling playback.`);
        scheduleTTSChunk();
    } else {
        // Log what chunks we have available
        const availableChunks = Array.from(indexedAudioChunks.keys()).sort((a, b) => a - b);
        if (availableChunks.length > 0) {
            debugLogFunc(`TTS PlayLoop: Waiting for chunk #${nextPlaybackIndex}, available: [${availableChunks.join(', ')}]`);
        } else {
            debugLogFunc(`TTS PlayLoop: No chunks available, waiting for chunk #${nextPlaybackIndex}.`);
        }
    }
}

export function resetTTSPlaybackState() {
    debugLogFunc('Resetting TTS playback state for new conversation round.');
    indexedAudioChunks.clear();
    nextPlaybackIndex = 0;
    currentIsCurrentlyPlayingTTS = false;
    currentIsTTSLoopActive = false;
    currentAllAudioChunksReceived = false;
    lastReceivedAudioChunkIndex = -1;
    if (updateIsTTSSpeakingFunc) updateIsTTSSpeakingFunc(false);
}

export function addTTSAudioChunk(index, buffer) {
    indexedAudioChunks.set(index, buffer);
    if (index > lastReceivedAudioChunkIndex) {
        lastReceivedAudioChunkIndex = index;
    }
    debugLogFunc(`[TTS] Added audio chunk #${index}, buffer size: ${indexedAudioChunks.size}`);
    
    // Trigger playloop in case this chunk is what we were waiting for
    ttsPlayLoop();
}

export function signalAllTTSAudioChunksReceived() {
    currentAllAudioChunksReceived = true;
    // If the loop is active and nothing is playing, and all chunks are received,
    // but some chunks might still be pending, the onended of the last chunk will trigger reset.
    // If no chunks were ever played or are pending, and all are received, reset.
    if (currentIsTTSLoopActive && !currentIsCurrentlyPlayingTTS && indexedAudioChunks.size === 0) {
        debugLogFunc('All TTS chunks signaled as received, and none are playing or pending. Resetting.');
        resetTTSPlaybackState();
    } else if (!currentIsTTSLoopActive && indexedAudioChunks.size === 0 && currentAllAudioChunksReceived) {
        // This case handles if signalAllTTSAudioChunksReceived is called when no playback was active
        // and no chunks were in the queue (e.g. an empty TTS response)
        debugLogFunc('All TTS chunks signaled as received, loop not active, no chunks. Resetting.');
        resetTTSPlaybackState();
    }
}

export function isTtsCurrentlyPlaying() {
    return currentIsCurrentlyPlayingTTS;
}

export function getIndexedAudioChunks() {
    return indexedAudioChunks;
}

export function getNextExpectedIndex() {
    // Return the next expected chunk index for fallback scenarios
    // Always use lastReceivedAudioChunkIndex + 1 to ensure sequentiality
    return lastReceivedAudioChunkIndex + 1;
}
