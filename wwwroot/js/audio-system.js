const FrameDurationMs = 20;
const TargetSampleRate = 16000;
const SamplesPerChunk = TargetSampleRate * (FrameDurationMs / 1000); // 320 samples
let isLoopActive = false;   // Flag, ob die Audio-Wiedergabe aktiv ist

// Audio-Chunk-Verwaltung
const indexedAudioChunks = new Map();  // Map zur Speicherung von Audio-Chunks nach Index
let nextPlaybackIndex = 0;  // Der nächste zu spielende Chunk-Index
let isPlaying = false;      // Flag, ob gerade Audio abgespielt wird
let allAudioChunksReceived = false; // Flag to indicate if 'audio-done' has been received

// Spielt den nächsten Buffer, sobald keiner läuft
/*
function playNext() {
    if (audioQueue.length === 0) {
        isPlaying = false;
        return;
    }

    const buffer = audioQueue.shift();
    const src = window.audioContext.createBufferSource();
    src.buffer = buffer;
    src.connect(window.audioContext.destination);

    // Tracken fürs Stoppen
    currentSource = src;
    window.currentBot = window.currentBot || {};
    window.currentBot.audioSources = window.currentBot.audioSources || [];
    window.currentBot.audioSources.push(src);
    window.allAudioSources = window.allAudioSources || [];
    window.allAudioSources.push(src);

    isPlaying = true;
    src.onended = () => {
        currentSource = null;
        playNext();          // Chaining: wenn dieser Buffer fertig, kommt der nächste
    };

    src.start();
}*/
// Schedule playback of the next chunk in sequence
function scheduleNext() {
    // Überprüfe, ob der nächste zu spielende Chunk bereits verfügbar ist
    if (indexedAudioChunks.has(nextPlaybackIndex)) {
        // Hole den nächsten Chunk aus der Map und entferne ihn
        const buffer = indexedAudioChunks.get(nextPlaybackIndex);
        indexedAudioChunks.delete(nextPlaybackIndex);
        
        console.log(`%c[AUDIO-DEBUG] Playing chunk #${nextPlaybackIndex}, duration=${buffer.duration.toFixed(2)}s, remaining chunks=${indexedAudioChunks.size}`, 
            'background: #e74c3c; color: white; padding: 2px 5px; border-radius: 3px;');
        
        // Erstelle AudioBufferSourceNode und spiele ab
        const src = window.audioContext.createBufferSource();
        src.buffer = buffer;
        src.connect(window.audioContext.destination);
        
        // Referenzen für stopAllAudio speichern
        window.allAudioSources = window.allAudioSources || [];
        window.allAudioSources.push(src);
        
        // Pro-Nachricht-Tracking
        if (window.currentBot) {
            window.currentBot.audioSources = window.currentBot.audioSources || [];
            window.currentBot.audioSources.push(src);
        }
        
        isPlaying = true;
        nextPlaybackIndex++; // Increment for the next chunk

        src.onended = () => {
            console.log(`%c[AUDIO-DEBUG] Finished playing chunk #${nextPlaybackIndex - 1}`, 
                'background: #f39c12; color: white; padding: 2px 5px; border-radius: 3px;');
            isPlaying = false;
            // Remove from allAudioSources to prevent re-stopping
            const index = window.allAudioSources.indexOf(src);
            if (index > -1) {
                window.allAudioSources.splice(index, 1);
            }
            if (window.currentBot && window.currentBot.audioSources) {
                const botSrcIndex = window.currentBot.audioSources.indexOf(src);
                if (botSrcIndex > -1) {
                    window.currentBot.audioSources.splice(botSrcIndex, 1);
                }
            }

            playLoop(); // Attempt to play the next chunk

            // Check if all chunks have been received and played
            if (allAudioChunksReceived && indexedAudioChunks.size === 0 && !isPlaying) {
                console.log('%c[AUDIO-DEBUG] All received audio chunks have been played. Resetting state.', 'color: red; font-weight: bold;');
                resetPlaybackState();
            }
        };

        src.start();
    } else {
        // console.log(`%c[AUDIO-DEBUG] Chunk #${nextPlaybackIndex} not yet available. Waiting... Stored: ${indexedAudioChunks.size}`, 
        //     'background: #3498db; color: white; padding: 2px 5px; border-radius: 3px;');
        // isPlaying = false; // No, keep isPlaying true if we are in the loop, waiting for next chunk
        // If no chunk is available, the loop will pause until new chunks arrive or it's terminated.
    }
}

// Entry to start or resume playback loop
function playLoop() {
    if (!isLoopActive) {
        isLoopActive = true;
        console.log('%c[AUDIO-DEBUG] PlayLoop started.', 'color: blue; font-weight: bold;');
    }

    if (isPlaying) {
        // console.log('%c[AUDIO-DEBUG] PlayLoop: Already playing a chunk, returning.', 'color: orange;');
        return; // Another chunk is already in progress
    }

    if (indexedAudioChunks.has(nextPlaybackIndex)) {
        scheduleNext();
    } else {
        // console.log('%c[AUDIO-DEBUG] PlayLoop: Next chunk #${nextPlaybackIndex} not available. Pausing loop.', 'color: purple;');
        // isLoopActive = false; // No, loop remains active, just no current chunk to play
        // isPlaying is already false here
        // If audio-done has been received and queue is empty, then truly stop.
        // This check might be better placed in 'audio-done' or after src.onended
    }
}

function resetPlaybackState() {
    console.log('%c[AUDIO-DEBUG] Resetting playback state.', 'color: red; font-weight: bold;');
    indexedAudioChunks.clear();
    nextPlaybackIndex = 0;
    isPlaying = false;
    isLoopActive = false; // Stop the loop
    allAudioChunksReceived = false; // Reset this flag as well
    // stopAllAudio(); // This is usually called separately when user clicks stop or clears chat
}

// const MinTextLength = 40; // Already defined in ProgressiveTTSSynthesizer

// Audio system management
// Create audio system object and expose it to the window
window.audioSystem = {
  init: function() {
    // Declare global variables for recording state
    window.recordingEnabled = true;     // Controls if recording is enabled
    window.isListening = true;          // Controls if we're listening for audio
    window.recorder = null;             // Global reference to MediaRecorder
    window.audioStream = null;          // Global reference to audio stream
    window.audioContext = null;         // Global reference to audio context
    window.audioAnalyser = null;        // Global reference to audio analyser
    window.chunks = [];                 // Global array for recording chunks
    window.speakingSegment = false;     // Global flag for speaking detection
    window.silenceStart = null;         // Global timestamp for silence detection
    window.isProcessingOrPlayingAudio = false;  // Global flag for processing state
    window.currentBotMessageElement = null; // Used to track the current bot message div for updates
    
    // VAD-Einstellungen: laden und an Backend weitergeben
    // Ensure these elements exist before trying to use them
    const thresholdSlider = document.getElementById('thresholdSlider');
    const thresholdValue = document.getElementById('thresholdValue');
    const silenceTimeoutSlider = document.getElementById('silenceTimeoutSlider');
    const silenceTimeoutValue = document.getElementById('silenceTimeoutValue');
    const minSpeechDurationSlider = document.getElementById('minSpeechDurationSlider');
    const minSpeechDurationValue = document.getElementById('minSpeechDurationValue');
    const startThresholdSlider = document.getElementById('startThresholdSlider');
    const startThresholdValue = document.getElementById('startThresholdValue');
    const endThresholdSlider = document.getElementById('endThresholdSlider');
    const endThresholdValue = document.getElementById('endThresholdValue');
    const smoothingWindowSlider = document.getElementById('smoothingWindowSlider');
    const smoothingWindowValue = document.getElementById('smoothingWindowValue');
    const hangoverSlider = document.getElementById('hangoverSlider');
    const hangoverValue = document.getElementById('hangoverValue');

    // Check if elements exist
    if (!thresholdSlider) console.error("Element with ID 'thresholdSlider' not found.");
    if (!silenceTimeoutSlider) console.error("Element with ID 'silenceTimeoutSlider' not found.");
    if (!minSpeechDurationSlider) console.error("Element with ID 'minSpeechDurationSlider' not found.");
    if (!startThresholdSlider) console.error("Element with ID 'startThresholdSlider' not found.");
    if (!endThresholdSlider) console.error("Element with ID 'endThresholdSlider' not found.");
    if (!smoothingWindowSlider) console.error("Element with ID 'smoothingWindowSlider' not found.");
    if (!hangoverSlider) console.error("Element with ID 'hangoverSlider' not found.");


    // Lade initiale VAD-Einstellungen vom Backend
    (async () => {
      try {
        const resp = await fetch('/api/settings');
        if (resp.ok) {
          const settings = await resp.json();
          // Add null checks before accessing properties
          if (thresholdSlider) thresholdSlider.value = settings.threshold;
          if (thresholdValue) thresholdValue.textContent = settings.threshold;
          window.silenceThreshold = settings.threshold; // This is a global, so no element check needed directly
          
          if (silenceTimeoutSlider) silenceTimeoutSlider.value = settings.silenceTimeoutSec;
          if (silenceTimeoutValue) silenceTimeoutValue.textContent = settings.silenceTimeoutSec;
          
          if (minSpeechDurationSlider) minSpeechDurationSlider.value = settings.minSpeechDurationSec;
          if (minSpeechDurationValue) minSpeechDurationValue.textContent = settings.minSpeechDurationSec;
          
          if (startThresholdSlider) startThresholdSlider.value = settings.startThreshold;
          if (startThresholdValue) startThresholdValue.textContent = settings.startThreshold;
          window.startThreshold = settings.startThreshold; // Global
          
          if (endThresholdSlider) endThresholdSlider.value = settings.endThreshold;
          if (endThresholdValue) endThresholdValue.textContent = settings.endThreshold;
          window.endThreshold = settings.endThreshold; // Global
          
          if (smoothingWindowSlider) smoothingWindowSlider.value = settings.rmsSmoothingWindowSec;
          if (smoothingWindowValue) smoothingWindowValue.textContent = settings.rmsSmoothingWindowSec;
          window.rmsSmoothingWindowSec = settings.rmsSmoothingWindowSec; // Global
          
          if (hangoverSlider) hangoverSlider.value = settings.hangoverDurationSec;
          if (hangoverValue) hangoverValue.textContent = settings.hangoverDurationSec;
          window.hangoverDurationSec = settings.hangoverDurationSec; // Global
          
        } else {
          console.error(`Failed to load VAD settings: ${resp.status}`);
          if (thresholdSlider) window.silenceThreshold = parseFloat(thresholdSlider.value);
        }
      } catch (err) {
        console.error('Error loading VAD settings', err);
        if (thresholdSlider) window.silenceThreshold = parseFloat(thresholdSlider.value);
      }
    })();

    // Aktualisiere UI und lokale Parameter bei Änderung (sofort)
    if (thresholdSlider && thresholdValue) {
        thresholdValue.textContent = thresholdSlider.value;
        thresholdSlider.addEventListener('input', () => {
            window.silenceThreshold = parseFloat(thresholdSlider.value);
            thresholdValue.textContent = thresholdSlider.value;
        });
    }
    if (silenceTimeoutSlider && silenceTimeoutValue) {
        silenceTimeoutValue.textContent = silenceTimeoutSlider.value;
        silenceTimeoutSlider.addEventListener('input', () => {
            silenceTimeoutValue.textContent = silenceTimeoutSlider.value;
        });
    }
    if (minSpeechDurationSlider && minSpeechDurationValue) {
        minSpeechDurationValue.textContent = minSpeechDurationSlider.value;
        minSpeechDurationSlider.addEventListener('input', () => {
            minSpeechDurationValue.textContent = minSpeechDurationSlider.value;
        });
    }
    if (startThresholdSlider && startThresholdValue) {
        startThresholdValue.textContent = startThresholdSlider.value;
        startThresholdSlider.addEventListener('input', () => {
            window.startThreshold = parseFloat(startThresholdSlider.value);
            startThresholdValue.textContent = startThresholdSlider.value;
        });
    }
    if (endThresholdSlider && endThresholdValue) {
        endThresholdValue.textContent = endThresholdSlider.value;
        endThresholdSlider.addEventListener('input', () => {
            window.endThreshold = parseFloat(endThresholdSlider.value);
            endThresholdValue.textContent = endThresholdSlider.value;
        });
    }
    if (smoothingWindowSlider && smoothingWindowValue) {
        smoothingWindowValue.textContent = smoothingWindowSlider.value;
        smoothingWindowSlider.addEventListener('input', () => {
            window.rmsSmoothingWindowSec = parseFloat(smoothingWindowSlider.value);
            smoothingWindowValue.textContent = smoothingWindowSlider.value;
        });
    }
    if (hangoverSlider && hangoverValue) {
        hangoverValue.textContent = hangoverSlider.value;
        hangoverSlider.addEventListener('input', () => {
            window.hangoverDurationSec = parseFloat(hangoverSlider.value);
            hangoverValue.textContent = hangoverSlider.value;
        });
    }

    // Sende geänderte VAD-Einstellungen beim Loslassen des Sliders ans Backend
    function updateVadSettings() {
      // Ensure all sliders exist before trying to read their values
      const payload = {
        threshold: thresholdSlider ? parseFloat(thresholdSlider.value) : 0.5, // Default if null
        silenceTimeoutSec: silenceTimeoutSlider ? parseFloat(silenceTimeoutSlider.value) : 2.0,
        minSpeechDurationSec: minSpeechDurationSlider ? parseFloat(minSpeechDurationSlider.value) : 0.2,
        startThreshold: startThresholdSlider ? parseFloat(startThresholdSlider.value) : 0.5,
        endThreshold: endThresholdSlider ? parseFloat(endThresholdSlider.value) : 0.3,
        rmsSmoothingWindowSec: smoothingWindowSlider ? parseFloat(smoothingWindowSlider.value) : 0.1,
        hangoverDurationSec: hangoverSlider ? parseFloat(hangoverSlider.value) : 0.5
      };
      fetch('/api/settings', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      }).then(resp => {
        if (!resp.ok) console.error(`Error updating VAD settings: ${resp.status}`);
      }).catch(err => console.error('Error updating VAD settings', err));
    }
    if (thresholdSlider) thresholdSlider.addEventListener('change', updateVadSettings);
    if (silenceTimeoutSlider) silenceTimeoutSlider.addEventListener('change', updateVadSettings);
    if (minSpeechDurationSlider) minSpeechDurationSlider.addEventListener('change', updateVadSettings);
    if (startThresholdSlider) startThresholdSlider.addEventListener('change', updateVadSettings);
    if (endThresholdSlider) endThresholdSlider.addEventListener('change', updateVadSettings);
    if (smoothingWindowSlider) smoothingWindowSlider.addEventListener('change', updateVadSettings);
    if (hangoverSlider) hangoverSlider.addEventListener('change', updateVadSettings);

    this.initCapture();
    this.setupEventListeners();
  },

  // Function to completely reset and restart audio recording
  restartAudioCapture: function() {
    if (window.wsAudioSocket) {
      debugLog("Closing existing WebSocketAudioService connection");
      window.wsAudioSocket.close();
      window.wsAudioSocket = null;
    }
    debugLog("Restarting audio capture system");
    // Prevent onstop handler from triggering processing during restart
    window._ignoreNextStop = true;
    
    try {
      // Explicitly stop any ongoing media recorder
      if (window.recorder && window.recorder.state === "recording") {
        window.recorder.stop();
        debugLog("Stopped ongoing recording during restart");
      }
      
      // Cancel any ongoing SSE connections
      if (window.eventSource) {
        window.eventSource.close();
        window.eventSource = null;
        debugLog("Closed event source during restart");
      }
      
      // Stop all audio playback
      stopAllAudio();
      
      // First, clean up existing audio capture if any
      if (window.audioContext && !window.optimizationSettings.useCachedAudioContext) {
        window.audioContext.close().catch(e => console.error("Error closing AudioContext:", e));
        window.audioContext = null;
      }
      
      if (window.audioStream) {
        window.audioStream.getTracks().forEach(track => {
          track.stop();
          debugLog("Stopped track: " + track.id);
        });
        window.audioStream = null;
      }
      
      // Reset all global components
      window.recorder = null;
      window.audioAnalyser = null;
      
      // Reset flags and states
      window.isProcessingOrPlayingAudio = false;
      window.speakingSegment = false;
      window.silenceStart = null;
      window.recordingStartTime = null;
      
      // Enable flags
      window.recordingEnabled = true;
      window.isListening = true;
    } catch (e) {
      debugLog("Error during cleanup phase of audio restart: " + e.toString());
    }
    
    // Small delay to ensure previous resources are cleaned up
    setTimeout(() => {
      // Re-initialize capture based on current pipeline mode (HTTP or WebSocket)
      this.initCapture();
      // If in HTTP legacy mode, auto-start HTTP pipeline
      if (window.optimizationSettings.useLegacyHttp) {
        debugLog('Auto-starting HTTP pipeline after restart');
        this.startHttpPipeline();
      }
    }, 300);
  },
  
  setupEventListeners: function() {
    // Global stop/start button for controlling recording
    stopBtn.addEventListener('click', function stopRecordingHandler() {
      // HTTP-Post legacy pipeline fallback
      if (window.optimizationSettings.useLegacyHttp) {
        // Toggle recording state
      if (!window.httpRecording) {
        // Start HTTP recording
        window.httpRecording = true;
        navigator.mediaDevices.getUserMedia({ audio: true })
          .then(stream => {
            window.httpMediaStream = stream;
            window.httpChunks = [];
            const recorder = new MediaRecorder(stream);
            window.httpRecorder = recorder;
            recorder.ondataavailable = e => window.httpChunks.push(e.data);
            recorder.start();
            status.textContent = 'Recording (HTTP)...';
            stopBtn.textContent = 'Stop HTTP Recording';
          })
            .catch(err => console.error('Error acquiring media for HTTP:', err));
        } else {
          // Stop HTTP recording and process
          window.httpRecording = false;
          status.textContent = 'Processing (HTTP)...';
          stopBtn.textContent = 'Aufnahme starten';
          const recorder = window.httpRecorder;
          if (recorder && recorder.state === 'recording') {
            recorder.onstop = async () => {
              // Track recording end latency
              optimizationManager.trackLatency('recordingStop');
              try {
                const blob = new Blob(window.httpChunks, { type: 'audio/webm' });
                const fd = new FormData();
                fd.append('file', blob, 'audio.webm');
                // Send transcription request
                const resp = await fetch('/api/processAudio', { method: 'POST', body: fd });
                const transcriptionTime = Date.now();
                optimizationManager.trackLatency('transcriptionReceived');
                const data = await resp.json();
                // Display messages and instrument latencies
                createUserMessage(data.prompt);
                const botObj = createBotMessage(data.response);
                // Text latency
                const textLat = transcriptionTime - (window.recordingStopTime || transcriptionTime);
                if (botObj.textSpan) botObj.textSpan.textContent = textLat + ' ms';
                optimizationManager.trackLatency('llmResponseStart');
                // Send TTS request
                const resp2 = await fetch('/api/speech', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ Input: data.response, Voice: voiceSel.value })
                });
                const audioBlob = await resp2.blob();
                const ttsTime = Date.now();
                optimizationManager.trackLatency('ttsEnd');
                // Audio latency
                const audioLat = ttsTime - transcriptionTime;
                if (botObj.audioSpan) botObj.audioSpan.textContent = audioLat + ' ms';
                // Play audio
                const url = URL.createObjectURL(audioBlob);
                const audio = new Audio(url);
                audio.oncanplaythrough = () => audio.play();
                audio.onended = () => URL.revokeObjectURL(url);
                status.textContent = 'Listening...';
                // Cleanup media stream
                window.httpMediaStream.getTracks().forEach(t => t.stop());
              } catch (err) {
                console.error('HTTP pipeline error:', err);
                status.textContent = 'Error in HTTP pipeline';
              }
            };
            // Store recording stop time
            window.recordingStopTime = Date.now();
            recorder.stop();
          }
        }
        return;
      }
      // Store reference to this handler for later restoration
      window.stopRecordingHandler = stopRecordingHandler;
      
      if (window.isListening) {
        // Currently listening, so stop
        debugLog("Stop button clicked - Stopping recording");
        
        // We need to access these variables from within the recorder context
        // so we use window scope to ensure they're available everywhere
        window.isListening = false;
        window.recordingEnabled = false;
        
        // Force stop any active recording
        if (window.audioStream) {
          window.audioStream.getTracks().forEach(track => { 
            debugLog("Stopping audio track: " + track.id);
            track.enabled = false; 
          });
        }
        
        status.textContent = 'Aufnahme gestoppt - Klicke erneut zum Fortsetzen';
        stopBtn.textContent = 'Aufnahme starten';
      } else {
        // Currently stopped, so restart
        debugLog("Start button clicked - Restarting recording");
        
        // Force complete restart of audio system
        audioSystem.restartAudioCapture();
        
        status.textContent = 'Zuhören...';
        stopBtn.textContent = 'Aufnahme stoppen';
      }
    });
    
    // Store original handler reference
    stopBtn._originalClickHandler = stopBtn.onclick;
    
    // Add separate button for stopping audio playback
    const stopAudioBtn = document.createElement('button');
    stopAudioBtn.textContent = 'Audio stoppen';
    stopAudioBtn.className = 'stop-button';
    stopAudioBtn.style.marginLeft = '10px';
    stopAudioBtn.addEventListener('click', () => {
      stopAllAudio();
      
      // Explicit audio debug message
      debugLog("Audio gestoppt, Audio-Processing wieder aktiviert");
      
      status.textContent = 'Audio gestoppt';
      
      // Force immediate re-enabling of audio processing when manually stopping
      window.isProcessingOrPlayingAudio = false;
      
      // Make sure we don't interfere with recording state
      // Only affect audio processing, not recording permission
    });
    document.querySelector('.button-group').appendChild(stopAudioBtn);
    
    // Add restart audio system button
    const restartAudioBtn = document.createElement('button');
    restartAudioBtn.textContent = 'Audio-System Neustart';
    restartAudioBtn.className = 'secondary';
    restartAudioBtn.style.marginLeft = '10px';
    restartAudioBtn.addEventListener('click', () => {
      // First, stop any ongoing audio playback
      stopAllAudio();
      
      // Make sure any processing is canceled
      window.isProcessingOrPlayingAudio = false;
      
      // Only restart the audio system if we're not in the middle of processing
      debugLog("Manual audio system restart requested");
      
      // Notify the user
      status.textContent = 'Audio-System wird neu gestartet...';
      
      // Use a small timeout to allow UI to update
      setTimeout(() => {
        // Do the actual restart
        audioSystem.restartAudioCapture();
      }, 100);
    });
    document.querySelector('.button-group').appendChild(restartAudioBtn);
  },
  
  // Track the moment the recording/capture starts
  // Track recording start for WebSocket, but HTTP mode handles start in manual handler
  initCapture: async function() {
    if (!window.optimizationSettings.useLegacyHttp) {
      // WebSocket mode: start streaming without recording latency start (server does VAD)
      if (asrMode.value === 'browser' && (window.SpeechRecognition || window.webkitSpeechRecognition)) {
        this.initBrowserASR();
      } else {
        this.initServerASR();
      }
    } else {
      // HTTP mode: await manual control via stopBtn
      status.textContent = 'Bereit (HTTP-Modus)';
      stopBtn.textContent = 'Aufnahme starten';
    }
  },
  
  initBrowserASR: function() {
    // Streaming ASR via Web Speech API
    const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    const recognition = new SpeechRecognition();
    recognition.continuous = true;
    recognition.interimResults = true;
    recognition.lang = langSel.value;
    recognition.onstart = () => { status.textContent = 'Listening (ASR)...'; };
    recognition.onerror = (e) => { console.error('Speech recognition error', e); status.textContent = 'Error in recognition'; };
    recognition.onresult = async (event) => {
      let interimTranscript = '';
      let finalTranscript = '';
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const result = event.results[i];
        const transcript = result[0].transcript;
        if (result.isFinal) finalTranscript += transcript;
        else interimTranscript += transcript;
      }
      if (interimTranscript) {
        status.textContent = interimTranscript;
      }
      if (finalTranscript) {
        status.textContent = 'Processing...';
        await this.sendChat(finalTranscript.trim());
        status.textContent = 'Listening (ASR)...';
      }
    };
    recognition.onend = () => recognition.start();
    recognition.start();
  },
  
  initServerASR: async function() {
    // Reset playback state before new connection
    resetPlaybackState();
    window.audioBufferForChunking = new Float32Array(0); // Initialize/reset the persistent buffer

    // Dynamically construct WebSocket URL with current optimization settings
    const queryParams = new URLSearchParams({
        DisableVad: window.optimizationSettings.disableVad,
        DisableTts: window.optimizationSettings.disableTts,
        DisableProgressiveTts: !window.optimizationSettings.useProgressiveTTS, // Inverted logic for this param
        TtsVoice: document.getElementById('voice') ? document.getElementById('voice').value : 'nova',
        TtsMinFirstChunkLength: window.optimizationSettings.ttsMinFirstChunkLength,
        TtsMaxFirstChunkLength: window.optimizationSettings.ttsMaxFirstChunkLength,
        TtsSubsequentChunkLength: window.optimizationSettings.ttsSubsequentChunkLength,
        ChatModel: document.getElementById('model') ? document.getElementById('model').value : "gpt-3.5-turbo", // Add ChatModel
        // Add VAD settings from the sliders in debugPanel that are also in VadSettings.cs
        Threshold: document.getElementById('thresholdSlider') ? parseFloat(document.getElementById('thresholdSlider').value) : 0.5,
        SilenceTimeoutSec: document.getElementById('silenceTimeoutSlider') ? parseFloat(document.getElementById('silenceTimeoutSlider').value) : 2.0,
        MinSpeechDurationSec: document.getElementById('minSpeechDurationSlider') ? parseFloat(document.getElementById('minSpeechDurationSlider').value) : 0.2,
        StartThreshold: document.getElementById('startThresholdSlider') ? parseFloat(document.getElementById('startThresholdSlider').value) : 0.5,
        EndThreshold: document.getElementById('endThresholdSlider') ? parseFloat(document.getElementById('endThresholdSlider').value) : 0.3,
        RmsSmoothingWindowSec: document.getElementById('smoothingWindowSlider') ? parseFloat(document.getElementById('smoothingWindowSlider').value) : 0.1,
        HangoverDurationSec: document.getElementById('hangoverSlider') ? parseFloat(document.getElementById('hangoverSlider').value) : 0.5,
        // VAD Spike/3rd party settings from optimizationManager's state
        VadSpikeThreshold: window.optimizationSettings.vadSpikeThreshold,
        EnableSpikeDetection: window.optimizationSettings.enableSpikeDetection,
        EnableThirdPartyVad: window.optimizationSettings.enableThirdPartyVad
    }).toString();

    const wsUrl = `wss://${window.location.host}/ws/audio?${queryParams}`;
    debugLog(`Connecting to WebSocket: ${wsUrl}`);

    window.wsAudioSocket = new WebSocket(wsUrl);
    window.wsAudioSocket.binaryType = 'arraybuffer'; // Ensure binary type is set for audio data

    window.wsAudioSocket.onopen = () => {
      console.log("WebSocket connection established for audio.");
      // Send initial VAD settings
      // These settings are also passed via URL, but sending them as a message ensures
      // the backend uses them if URL parsing fails or for future flexibility.
      const initialVadSettings = {
        UseWebRTCVAD: optimizationSettings.useWebRTCVAD,
        WebRTCVADMode: optimizationSettings.webRTCVADMode,
        Threshold: optimizationSettings.vadThreshold, // Use the one from optimizationSettings
        SilenceTimeoutSec: optimizationSettings.vadSilenceTimeout, // Use the one from optimizationSettings
        MinSpeechDurationSec: 0.2, // Corrected: Use appropriate float for seconds
        StartThreshold: optimizationSettings.vadStartThreshold,
        EndThreshold: optimizationSettings.vadEndThreshold,
        RmsSmoothingWindowSec: optimizationSettings.vadRmsSmoothingWindow,
        HangoverDurationSec: 0.5, // Corrected: Use appropriate float for seconds
        SpikeDetectionEnabled: optimizationSettings.vadSpikeDetectionEnabled,
        SpikeThresholdFactor: optimizationSettings.vadSpikeThresholdFactor,
        SpikeMinDurationMs: optimizationSettings.vadSpikeMinDurationMs,
        ConsecutiveSpikeThreshold: optimizationSettings.vadConsecutiveSpikeThreshold,
        UseDynamicThreshold: optimizationSettings.vadUseDynamicThreshold,
        DynamicThresholdFactor: optimizationSettings.vadDynamicThresholdFactor,
        DynamicThresholdDecay: optimizationSettings.vadDynamicThresholdDecay,
        MinRMSForDynamicThreshold: optimizationSettings.vadMinRMSForDynamicThreshold
      };
      if (window.wsAudioSocket && window.wsAudioSocket.readyState === WebSocket.OPEN) {
        window.wsAudioSocket.send(JSON.stringify({
          type: "initialVadSettings",
          payload: initialVadSettings
        }));
        console.log("Sent initial VAD settings via WebSocket message:", initialVadSettings);
      }

      // Start sending audio data if recording is enabled
      if (window.recordingEnabled && window.audioStream) {
        // This part is handled by the ScriptProcessorNode's onaudioprocess
        console.log("WebSocket open, audio processing will send data.");
      }
    };

    window.wsAudioSocket.onmessage = async (event) => {
      if (typeof event.data === 'string') {
        try {
          const eventObject = JSON.parse(event.data);
          window.audioSystem.handleServerEvent(eventObject); // MODIFIED CALL
        } catch (e) {
          console.error("[WebSocket] Error parsing JSON from server:", e, event.data);
        }
      } else if (event.data instanceof ArrayBuffer) {
        if (!window.audioContext) {
            window.audioContext = new (window.AudioContext || window.webkitAudioContext)();
        }
        try {
            const audioBuffer = await window.audioContext.decodeAudioData(event.data);
            const actualChunkIndex = window.lastReceivedAudioChunkIndex;

            if (actualChunkIndex === undefined) {
                console.error("%c[AUDIO-ERROR] Received binary audio data but lastReceivedAudioChunkIndex is undefined. Audio-chunk-info might be missing or out of order.", 'color: red; font-weight: bold;');
                return;
            }

            indexedAudioChunks.set(actualChunkIndex, audioBuffer); // global
            console.log(`%c[AUDIO-DEBUG] Queued audio chunk #${actualChunkIndex} for playback. Total queued: ${indexedAudioChunks.size}`,
                'background: #2ecc71; color: white; padding: 2px 5px; border-radius: 3px;');

            if (typeof playLoop === 'function') {
              playLoop(); // global
            }
        } catch (e) {
            console.error('%c[AUDIO-ERROR] Error decoding audio data:', 'color: red; font-weight: bold;', e, event.data);
        }
      } else {
        console.warn("[WebSocket] Received unknown message type:", event.data);
      }
    };

    window.wsAudioSocket.onerror = (error) => {
      console.error('WebSocket Error:', error);
      debugLog(`WebSocket Error: ${error.message || 'Unknown error'}`);
      status.textContent = 'WebSocket Fehler';
      stopBtn.textContent = 'Neu verbinden'; // Or similar
      stopBtn.disabled = false;
    };

    window.wsAudioSocket.onclose = (event) => {
      debugLog(`WebSocket connection closed. Code: ${event.code}, Reason: ${event.reason}`);
      status.textContent = 'Getrennt. Neu verbinden?';
      if (!event.wasClean) {
        // Handle unclean closure, maybe attempt reconnect or notify user
      }
      stopBtn.textContent = 'Neu verbinden';
      stopBtn.disabled = false;
      window.isListening = false; // Ensure listening is false on close
      window.recordingEnabled = false; 
      window.wsAudioSocket = null; // Clear reference
    };

    // Initialize audio processing script processor
    if (!window.audioContext) {
        const contextOptions = { sampleRate: TargetSampleRate };
        try {
            window.audioContext = new (window.AudioContext || window.webkitAudioContext)(contextOptions);
            if (window.audioContext.sampleRate !== TargetSampleRate) {
                console.warn(`[AUDIO-SYSTEM] Could not create AudioContext with ${TargetSampleRate}Hz. Actual: ${window.audioContext.sampleRate}Hz. This may affect ASR/VAD quality if resampling is not performed.`);
                // Attempt to recreate with default if specific rate failed or was not met.
                try { await window.audioContext.close(); } catch(e) { console.error("Error closing audio context", e); }
                window.audioContext = new (window.AudioContext || window.webkitAudioContext)();
                console.warn(`[AUDIO-SYSTEM] Reverted to default AudioContext. Sample rate: ${window.audioContext.sampleRate}Hz.`);
            } else {
                console.log(`[AUDIO-SYSTEM] AudioContext created with ${window.audioContext.sampleRate}Hz sample rate.`);
            }
        } catch (e) {
            console.warn(`[AUDIO-SYSTEM] Error creating AudioContext with preferred sample rate: ${e}. Falling back to default.`);
            window.audioContext = new (window.AudioContext || window.webkitAudioContext)();
            console.log(`[AUDIO-SYSTEM] AudioContext created with default sample rate: ${window.audioContext.sampleRate}Hz.`);
        }
    }
    
    if (window.audioContext.state === 'suspended') {
        await window.audioContext.resume();
    }

    // The scriptNode buffer size (e.g., 4096) determines how often onaudioprocess is called.
    // It does not need to be SamplesPerChunk.
    window.scriptNode = window.audioContext.createScriptProcessor(4096, 1, 1);
    window.scriptNode.onaudioprocess = (audioProcessingEvent) => {
      if (!window.isListening || !window.wsAudioSocket || window.wsAudioSocket.readyState !== WebSocket.OPEN) {
        return;
      }

      const inputData = audioProcessingEvent.inputBuffer.getChannelData(0); // Float32Array

      // Append new data to the persistent buffer
      const currentPersistentBuffer = window.audioBufferForChunking;
      const combinedBuffer = new Float32Array(currentPersistentBuffer.length + inputData.length);
      combinedBuffer.set(currentPersistentBuffer);
      combinedBuffer.set(inputData, currentPersistentBuffer.length);
      window.audioBufferForChunking = combinedBuffer;

      // Process and send chunks
      while (window.audioBufferForChunking.length >= SamplesPerChunk) {
        const chunkToProcess = window.audioBufferForChunking.slice(0, SamplesPerChunk);
        window.audioBufferForChunking = window.audioBufferForChunking.slice(SamplesPerChunk);

        const pcmData = new Int16Array(SamplesPerChunk);
        for (let i = 0; i < SamplesPerChunk; i++) {
          pcmData[i] = Math.max(-32768, Math.min(32767, chunkToProcess[i] * 32768));
        }

        if (window.wsAudioSocket && window.wsAudioSocket.readyState === WebSocket.OPEN) {
          window.wsAudioSocket.send(pcmData.buffer); // Sends 320 samples * 2 bytes/sample = 640 bytes
        }
      }
      
      // RMS calculation for visualization (using the raw inputData from the event for responsiveness)
      let sumSquares = 0.0;
      for (const sample of inputData) {
          sumSquares += sample * sample;
      }
      const rms = Math.sqrt(sumSquares / inputData.length);
      this.updateAudioVisualization(rms);
    };

    // Get microphone access and connect to the audio processing pipeline
    if (navigator.mediaDevices && navigator.mediaDevices.getUserMedia) {
        navigator.mediaDevices.getUserMedia({ audio: true })
            .then(stream => {
                window.audioStream = stream; // Store the stream globally
                window.sourceNode = window.audioContext.createMediaStreamSource(stream);
                window.sourceNode.connect(window.scriptNode);
                window.scriptNode.connect(window.audioContext.destination); // Important: connect to destination
                
                debugLog('Microphone access granted and audio pipeline connected.');
                status.textContent = 'Verbunden. Aufnahme aktiv.';
                stopBtn.textContent = 'Aufnahme stoppen';
                window.isListening = true; 
                window.recordingEnabled = true;
            })
            .catch(err => {
                console.error('Error acquiring media for WebSocket ASR:', err);
                status.textContent = 'Fehler beim Mikrofonzugriff.';
                debugLog(`Error acquiring media: ${err.toString()}`);
                stopBtn.textContent = 'Mikrofonzugriff erneut versuchen';
                window.isListening = false;
                window.recordingEnabled = false;
            });
    } else {
        console.error('getUserMedia not supported on this browser.');
        status.textContent = 'getUserMedia nicht unterstützt.';
        debugLog('getUserMedia not supported.');
        window.isListening = false;
        window.recordingEnabled = false;
    }
  },
  startHttpPipeline: function() {
    if (!window.audioStream || !window.webSocket || window.webSocket.readyState !== WebSocket.OPEN) {
      debugLog('Cannot start HTTP pipeline: Audio stream or WebSocket not ready.');
      if (window.webSocket && window.webSocket.readyState !== WebSocket.OPEN) {
          status.textContent = 'Verbindung verloren. Neu verbinden.';
          stopBtn.textContent = 'Neu verbinden';
      }
      return;
    }
    window.isListening = true;
    window.sourceNode = window.audioContext.createMediaStreamSource(window.audioStream);
    window.sourceNode.connect(window.scriptNode);
    window.scriptNode.connect(window.audioContext.destination); // Connect to destination to keep processing alive
    debugLog('Audio pipeline started. Listening...');
    status.textContent = 'Höre zu...';
    stopBtn.textContent = 'Aufnahme stoppen';
    // Reset latency tracking for new interaction
    if (window.optimizationManager) window.optimizationManager.resetLatencyStats(); 
    if (window.optimizationManager) window.optimizationManager.trackLatency('recordingStart');
  },
  stopHttpPipeline: function() {
    window.isListening = false;
    if (window.sourceNode) {
      window.sourceNode.disconnect();
      window.sourceNode = null;
    }
    if (window.scriptNode) {
      window.scriptNode.disconnect();
      // scriptNode is not nulled here, as it might be reused by initServerASR
    }
    // Don't close WebSocket here if we want to send a final message or keep it for next recording
    // webSocket.close(); 
    debugLog('Audio pipeline stopped.');
    status.textContent = 'Bereit für nächste Aufnahme';
    stopBtn.textContent = 'Aufnahme starten';
  },
  updateAudioVisualization: function(rms) {
    const currentAudioLevel = document.getElementById('currentAudioLevel');
    const currentAudioValue = document.getElementById('currentAudioValue');
    if (currentAudioLevel && currentAudioValue) {
        const percentage = Math.min(100, (rms * 500)); // Adjust multiplier for sensitivity
        currentAudioLevel.style.width = percentage + '%';
        currentAudioValue.textContent = rms.toFixed(3);
    }
    // Update noise stats if debug panel is open
    // This part might be better if VAD events from backend provide noise floor and dynamic threshold
  },
  handleServerEvent: function(eventData) {
    debugLog(`[WebSocket] Received from server:`, eventData);
    switch (eventData.type) {
        case 'vad_settings_updated':
            debugLog('VAD settings updated by server:', eventData.payload);
            if (window.optimizationManager && typeof window.optimizationManager.updateVadSettingsUIFromState === 'function') {
                // Update the global settings object first
                if (eventData.payload) {
                    for (const key in eventData.payload) {
                        if (window.optimizationSettings.hasOwnProperty(key)) {
                            // Basic type conversion based on existing type in window.optimizationSettings
                            if (typeof window.optimizationSettings[key] === 'boolean') {
                                window.optimizationSettings[key] = Boolean(eventData.payload[key]);
                            } else if (typeof window.optimizationSettings[key] === 'number') {
                                window.optimizationSettings[key] = Number(eventData.payload[key]);
                            } else {
                                window.optimizationSettings[key] = eventData.payload[key];
                            }
                        }
                    }
                }
                window.optimizationManager.updateVadSettingsUIFromState();
            } else {
                console.warn('optimizationManager not found or updateVadSettingsUIFromState is not a function when trying to update VAD sliders from server event.');
            }
            break;
        case 'pipeline_options_updated':
            debugLog('Pipeline options updated by server:', eventData.payload);
            if (window.optimizationManager && typeof window.optimizationManager.updateOptimizationUIFromSettings === 'function') {
                 // Update the global settings object first
                if (eventData.payload) {
                    for (const key in eventData.payload) {
                        if (window.optimizationSettings.hasOwnProperty(key)) {
                             // Basic type conversion based on existing type in window.optimizationSettings
                            if (typeof window.optimizationSettings[key] === 'boolean') {
                                window.optimizationSettings[key] = Boolean(eventData.payload[key]);
                            } else if (typeof window.optimizationSettings[key] === 'number') {
                                window.optimizationSettings[key] = Number(eventData.payload[key]);
                            } else {
                                window.optimizationSettings[key] = eventData.payload[key];
                            }
                        }
                    }
                }
                window.optimizationManager.updateOptimizationUIFromSettings();
            } else {
                console.warn('optimizationManager not found or updateOptimizationUIFromSettings is not a function when trying to update pipeline UI from server event.');
            }
            break;
        case 'prompt':
            if (window.uiManager && typeof window.uiManager.updateRecognizedText === 'function') {
                window.uiManager.updateRecognizedText(eventData.payload.prompt, false);
            } else {
                console.error('uiManager.updateRecognizedText is not available');
            }
            break;
        case 'reply':
            debugLog('Received final reply from server:', eventData.payload);
            const replyText = eventData.payload && eventData.payload.reply ? eventData.payload.reply : '';
            if (window.uiManager && typeof window.uiManager.updateBotMessage === 'function') {
                window.uiManager.updateBotMessage(replyText, true); // true for final
            } else {
                console.error('uiManager.updateBotMessage is not available');
            }
            let latencyDisplay = "N/A";
            if (eventData.payload && eventData.payload.latency_info) {
                const { transcriptionTime, llmTime, totalTime } = eventData.payload.latency_info;
                latencyDisplay = `Trans: ${transcriptionTime}ms, LLM: ${llmTime}ms, Total: ${totalTime}ms`;
            }
            if (window.uiManager && typeof window.uiManager.updateLatencyDisplay === 'function') {
                window.uiManager.updateLatencyDisplay(latencyDisplay);
            } else {
                console.error('uiManager.updateLatencyDisplay is not available');
            }
            break;
        case 'token':
          if (!window.currentBotMessageElement) {
              if (window.uiManager && typeof window.uiManager.createBotMessage === 'function') {
                window.currentBotMessageElement = window.uiManager.createBotMessage('', window.optimizationSettings.chatModel, window.optimizationSettings.ttsVoice);
              } else {
                console.error('uiManager.createBotMessage is not available');
              }
          }
          if (window.currentBotMessageElement) {
              const textElement = window.currentBotMessageElement.querySelector('.message-content');
              if (textElement) {
                  if (textElement.textContent === '') { // Check for empty if that's the initial state
                      textElement.textContent = eventData.payload.token;
                  } else {
                      textElement.textContent += eventData.payload.token;
                  }
                  if (window.uiManager && typeof window.uiManager.scrollToBottom === 'function') {
                    window.uiManager.scrollToBottom();
                  } else {
                    console.error('uiManager.scrollToBottom is not available');
                  }
              }
          }
          break;
        case 'audio-chunk-info':
          // Corrected: Use eventData.payload instead of undefined 'payload'
          if (eventData.payload && eventData.payload.index !== undefined) {
              window.lastReceivedAudioChunkIndex = eventData.payload.index; 
              console.log(`%c[AUDIO-DEBUG] Received audio-chunk-info for index: ${eventData.payload.index}. Duration: ${eventData.payload.durationMs}ms. IsFinal: ${eventData.payload.isFinal}. Next binary data will be for this chunk.`, 'color: #8e44ad; font-weight: bold;');
          } else {
              console.warn('[AUDIO-DEBUG] Received audio-chunk-info with missing index or payload:', eventData);
          }
          break;
        case 'audio-done':
          console.log('%c[AUDIO-SYSTEM] Explicit Audio-done JSON event received from server.', 'color: #3498db; font-weight: bold;');
          allAudioChunksReceived = true; 
          if (indexedAudioChunks.size === 0 && !isPlaying) { 
              console.log('%c[AUDIO-DEBUG] All received audio chunks have been played (audio-done received). Resetting state.', 'color: red; font-weight: bold;');
              if (typeof resetPlaybackState === 'function') {
                resetPlaybackState();
              }
          }
          break;
        case 'done':
          console.log('Received done event from server:', eventData.payload);
          window.currentBotMessageElement = null; 
          break;
        default:
          // Corrected: Use eventData.type and eventData.payload for logging unhandled events
          console.warn(`Received unhandled event type: ${eventData.type}`, eventData.payload);
    }
  }
};