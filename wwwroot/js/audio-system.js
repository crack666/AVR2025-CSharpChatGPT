const FrameDurationMs = 20;
const TargetSampleRate = 16000;
let isLoopActive = false;   // Flag, ob die Audio-Wiedergabe aktiv ist

// Audio-Chunk-Verwaltung
const indexedAudioChunks = new Map();  // Map zur Speicherung von Audio-Chunks nach Index
let nextPlaybackIndex = 0;  // Der nächste zu spielende Chunk-Index
let isPlaying = false;      // Flag, ob gerade Audio abgespielt wird

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
        if (window.currentBot) window.currentBot.audioSource = src;
        
        // Save current index to use in log
        const currentIndex = nextPlaybackIndex;
        
        // Increment index before starting playback to prepare for the next chunk
        nextPlaybackIndex++;
        
        // Wenn der Chunk fertig ist, zum nächsten Chunk gehen
        src.onended = () => {
            console.log(`%c[AUDIO-DEBUG] Chunk #${currentIndex} finished playing, next is #${nextPlaybackIndex}`, 
                'background: #9b59b6; color: white; padding: 2px 5px; border-radius: 3px;');
            scheduleNext();      // Überprüfe, ob der nächste Chunk bereit ist
        };
        
        src.start();
        isLoopActive = true;
    } else {
        // Der nächste Chunk ist noch nicht verfügbar
        if (indexedAudioChunks.size === 0) {
            // Wenn überhaupt keine Chunks mehr in der Map sind, beenden wir den Loop
            isLoopActive = false;
            console.log(`[scheduleNext] all chunks processed (nextPlaybackIndex=${nextPlaybackIndex}) → stopping`);
            
            // If all chunks are done AND we've received the 'done' event, run final cleanup
            if (window._allAudioChunksReceived && typeof window._cleanupAfterPlayback === 'function') {
                console.log('[scheduleNext] Running final cleanup after all chunks played');
                window._cleanupAfterPlayback();
            }
        } else {
            // Es gibt noch Chunks, aber der nächste ist noch nicht verfügbar
            // Wir warten kurz und versuchen es dann erneut
            // Zeige alle wartenden Chunks
            const waitingIndices = Array.from(indexedAudioChunks.keys()).sort((a, b) => a - b);
            console.log(`%c[AUDIO-DEBUG] Waiting for chunk #${nextPlaybackIndex}, chunks in queue: [${waitingIndices.join(', ')}]`, 
                'background: #f39c12; color: white; padding: 2px 5px; border-radius: 3px;');
                
            // Check if the waiting chunks have higher indices than what we're waiting for
            // This would indicate we might have missed a chunk or it failed to process
            if (waitingIndices.length > 0 && waitingIndices[0] > nextPlaybackIndex) {
                console.log(`%c[AUDIO-DEBUG] Chunk #${nextPlaybackIndex} appears to be missing, skipping to #${waitingIndices[0]}`, 
                    'background: #e67e22; color: white; padding: 2px 5px; border-radius: 3px;');
                nextPlaybackIndex = waitingIndices[0];
                // Immediately try again with the new index
                scheduleNext();
                return;
            }
            
            setTimeout(scheduleNext, 50);
        }
    }
}

// Entry to start or resume playback loop
function playLoop() {
    if (!isLoopActive) {
        const loopIndices = Array.from(indexedAudioChunks.keys()).sort((a, b) => a - b);
        
        if (loopIndices.length === 0) {
            console.log(`%c[AUDIO-DEBUG] No chunks to play, skipping playback loop`, 
                'background: #7f8c8d; color: white; padding: 2px 5px; border-radius: 3px;');
            return;
        }
        
        console.log(`%c[AUDIO-DEBUG] Starting playback loop: next=${nextPlaybackIndex}, chunks=[${loopIndices.join(', ')}]`, 
            'background: #27ae60; color: white; padding: 2px 5px; border-radius: 3px;');
            
        // If our nextPlaybackIndex is too high (might have been reset incorrectly),
        // reset it to the lowest available chunk index
        if (loopIndices.length > 0 && !loopIndices.includes(nextPlaybackIndex)) {
            const oldIndex = nextPlaybackIndex;
            nextPlaybackIndex = loopIndices[0];
            console.log(`%c[AUDIO-DEBUG] Reset playback index from ${oldIndex} to ${nextPlaybackIndex} to match available chunks`, 
                'background: #16a085; color: white; padding: 2px 5px; border-radius: 3px;');
        }
        
        scheduleNext();
    }
}

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
    
    // VAD-Einstellungen: laden und an Backend weitergeben
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

    // Lade initiale VAD-Einstellungen vom Backend
    (async () => {
      try {
        const resp = await fetch('/api/settings');
        if (resp.ok) {
          const settings = await resp.json();
          thresholdSlider.value = settings.threshold;
          thresholdValue.textContent = settings.threshold;
          window.silenceThreshold = settings.threshold;
          silenceTimeoutSlider.value = settings.silenceTimeoutSec;
          silenceTimeoutValue.textContent = settings.silenceTimeoutSec;
          minSpeechDurationSlider.value = settings.minSpeechDurationSec;
          minSpeechDurationValue.textContent = settings.minSpeechDurationSec;
          startThresholdSlider.value = settings.startThreshold;
          startThresholdValue.textContent = settings.startThreshold;
          window.startThreshold = settings.startThreshold;
          endThresholdSlider.value = settings.endThreshold;
          endThresholdValue.textContent = settings.endThreshold;
          window.endThreshold = settings.endThreshold;
          smoothingWindowSlider.value = settings.rmsSmoothingWindowSec;
          smoothingWindowValue.textContent = settings.rmsSmoothingWindowSec;
          window.rmsSmoothingWindowSec = settings.rmsSmoothingWindowSec;
          hangoverSlider.value = settings.hangoverDurationSec;
          hangoverValue.textContent = settings.hangoverDurationSec;
          window.hangoverDurationSec = settings.hangoverDurationSec;
        } else {
          console.error(`Failed to load VAD settings: ${resp.status}`);
          window.silenceThreshold = parseFloat(thresholdSlider.value);
        }
      } catch (err) {
        console.error('Error loading VAD settings', err);
        window.silenceThreshold = parseFloat(thresholdSlider.value);
      }
    })();

    // Aktualisiere UI und lokale Parameter bei Änderung (sofort)
    thresholdValue.textContent = thresholdSlider.value;
    thresholdSlider.addEventListener('input', () => {
      window.silenceThreshold = parseFloat(thresholdSlider.value);
      thresholdValue.textContent = thresholdSlider.value;
    });
    silenceTimeoutValue.textContent = silenceTimeoutSlider.value;
    silenceTimeoutSlider.addEventListener('input', () => {
      silenceTimeoutValue.textContent = silenceTimeoutSlider.value;
    });
    minSpeechDurationValue.textContent = minSpeechDurationSlider.value;
    minSpeechDurationSlider.addEventListener('input', () => {
      minSpeechDurationValue.textContent = minSpeechDurationSlider.value;
    });
    startThresholdValue.textContent = startThresholdSlider.value;
    startThresholdSlider.addEventListener('input', () => {
      window.startThreshold = parseFloat(startThresholdSlider.value);
      startThresholdValue.textContent = startThresholdSlider.value;
    });
    endThresholdValue.textContent = endThresholdSlider.value;
    endThresholdSlider.addEventListener('input', () => {
      window.endThreshold = parseFloat(endThresholdSlider.value);
      endThresholdValue.textContent = endThresholdSlider.value;
    });
    smoothingWindowValue.textContent = smoothingWindowSlider.value;
    smoothingWindowSlider.addEventListener('input', () => {
      window.rmsSmoothingWindowSec = parseFloat(smoothingWindowSlider.value);
      smoothingWindowValue.textContent = smoothingWindowSlider.value;
    });
    hangoverValue.textContent = hangoverSlider.value;
    hangoverSlider.addEventListener('input', () => {
      window.hangoverDurationSec = parseFloat(hangoverSlider.value);
      hangoverValue.textContent = hangoverSlider.value;
    });

    // Sende geänderte VAD-Einstellungen beim Loslassen des Sliders ans Backend
    function updateVadSettings() {
      const payload = {
        threshold: parseFloat(thresholdSlider.value),
        silenceTimeoutSec: parseFloat(silenceTimeoutSlider.value),
        minSpeechDurationSec: parseFloat(minSpeechDurationSlider.value),
        startThreshold: parseFloat(startThresholdSlider.value),
        endThreshold: parseFloat(endThresholdSlider.value),
        rmsSmoothingWindowSec: parseFloat(smoothingWindowSlider.value),
        hangoverDurationSec: parseFloat(hangoverSlider.value)
      };
      fetch('/api/settings', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      }).then(resp => {
        if (!resp.ok) console.error(`Error updating VAD settings: ${resp.status}`);
      }).catch(err => console.error('Error updating VAD settings', err));
    }
    thresholdSlider.addEventListener('change', updateVadSettings);
    silenceTimeoutSlider.addEventListener('change', updateVadSettings);
    minSpeechDurationSlider.addEventListener('change', updateVadSettings);
    startThresholdSlider.addEventListener('change', updateVadSettings);
    endThresholdSlider.addEventListener('change', updateVadSettings);
    smoothingWindowSlider.addEventListener('change', updateVadSettings);
    hangoverSlider.addEventListener('change', updateVadSettings);

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
  
  initCapture: async function() {
    // Track the moment the recording/capture starts
    // Track recording start for WebSocket, but HTTP mode handles start in manual handler
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
    window.audioStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    window.audioContext = new (window.AudioContext || window.webkitAudioContext)();
    const source = window.audioContext.createMediaStreamSource(window.audioStream);
    const scriptNode = window.audioContext.createScriptProcessor(4096, 1, 1);
    source.connect(scriptNode);
    scriptNode.connect(window.audioContext.destination);

    const ratio = window.audioContext.sampleRate / TargetSampleRate;
    let sampleBuffer = [];

    const wsProtocol = location.protocol === 'https:' ? 'wss' : 'ws';
    // Connect to WebSocket endpoint (server will use settings from SettingsController)
    const socket = new WebSocket(`${wsProtocol}://${location.host}/ws/audio`);
    socket.binaryType = 'arraybuffer';
    socket.onopen = () => debugLog('WebSocketAudioService connected');
    socket.onerror = err => console.error('WebSocket error', err);
    socket.onclose = () => debugLog('WebSocketAudioService closed');
    socket.onmessage = e => this.handleServerEvent(e.data);
    // Keep socket global for restart cleanup
    window.wsAudioSocket = socket;

    scriptNode.onaudioprocess = (event) => {
      const input = event.inputBuffer.getChannelData(0);
      // visualize audio level (RMS)
      const rms = Math.sqrt(input.reduce((sum, v) => sum + v * v, 0) / input.length);
      this.updateAudioVisualization(rms);
      // downsampling to 16kHz and framing
      for (let i = 0; i < input.length; i += ratio) {
        sampleBuffer.push(input[Math.floor(i)]);
      }
      const frameSize = TargetSampleRate * FrameDurationMs / 1000;
      while (sampleBuffer.length >= frameSize) {
        const frame = sampleBuffer.splice(0, frameSize);
        const pcm16 = new Int16Array(frame.length);
        for (let i = 0; i < frame.length; i++) {
          const s = Math.max(-1, Math.min(1, frame[i]));
          pcm16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
        }
        if (socket.readyState === WebSocket.OPEN) {
          socket.send(pcm16.buffer);
        }
      }
    };

    status.textContent = 'Zuhören...';
  },
  // Start the HTTP (legacy) audio processing pipeline
  startHttpPipeline: function() {
    if (window.httpRecording) return;
    debugLog('Starting HTTP pipeline');
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
        window.httpRecording = true;
      })
      .catch(err => console.error('Error acquiring media for HTTP:', err));
  },
  // Stop the HTTP pipeline and send audio to server
  stopHttpPipeline: function() {
    if (!window.httpRecording) return;
    debugLog('Stopping HTTP pipeline');
    window.httpRecording = false;
    status.textContent = 'Processing (HTTP)...';
    stopBtn.textContent = 'Aufnahme starten';
    const recorder = window.httpRecorder;
    if (recorder && recorder.state === 'recording') {
      recorder.onstop = async () => {
        const stopTime = Date.now();
        window.recordingStopTime = stopTime;
        try {
          const blob = new Blob(window.httpChunks, { type: 'audio/webm' });
          const fd = new FormData();
          fd.append('file', blob, 'audio.webm');
          // transcription
          const resp = await fetch('/api/processAudio', { method: 'POST', body: fd });
          optimizationManager.trackLatency('transcriptionReceived');
          const data = await resp.json();
          const userMsg = createUserMessage(data.prompt);
          // record text latency
          const textTime = Date.now();
          const botObj = createBotMessage(data.response);
          // Show stop button and attach handler for HTTP audio
          botObj.stopButton.style.display = 'inline-block';
          botObj._audioStopped = false;
          botObj.stopButton.onclick = () => {
            botObj._audioStopped = true;
            if (botObj.audioElement) botObj.audioElement.pause();
            botObj.stopButton.style.display = 'none';
            stopAllAudio();
          };
          if (botObj.textSpan) {
            const delta = textTime - stopTime;
            botObj.textSpan.textContent = delta + ' ms';
          }
          optimizationManager.trackLatency('llmResponseStart');
          // TTS
          const resp2 = await fetch('/api/speech', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ Input: data.response, Voice: voiceSel.value })
          });
          const audioBlob = await resp2.blob();
          optimizationManager.trackLatency('ttsEnd');
          const audioUrl = URL.createObjectURL(audioBlob);
          const audio = new Audio(audioUrl);
          // Track HTMLAudioElement for stops
          window.allAudioElements = window.allAudioElements || [];
          window.allAudioElements.push(audio);
          botObj.audioElement = audio;
          audio.oncanplaythrough = () => audio.play();
          audio.onended = () => {
            URL.revokeObjectURL(audioUrl);
            // Hide stop button when done
            if (botObj.stopButton) botObj.stopButton.style.display = 'none';
          };
          status.textContent = 'Bereit (HTTP-Modus)';
          // cleanup
          window.httpMediaStream.getTracks().forEach(t => t.stop());
        } catch (err) {
          console.error('HTTP pipeline error:', err);
          status.textContent = 'Error in HTTP pipeline';
        }
      };
      recorder.stop();
    }
  },
  
  updateAudioVisualization: function(rms) {
    // Audio level history: separate container for bars + numeric values
    if (window.audioLevelHistory) {
      const recordDiv = document.createElement('div');
      recordDiv.style.display = 'flex';
      recordDiv.style.alignItems = 'center';
      recordDiv.style.marginBottom = '2px';
      
      // Bar representation
      const bar = document.createElement('div');
      bar.style.width = `${Math.min(rms * 1000, 100)}%`;
      bar.style.height = '10px';
      bar.style.backgroundColor = rms > window.silenceThreshold ? '#4CAF50' : '#F44336';
      bar.style.marginRight = '8px';
      
      // Numeric value
      const text = document.createElement('span');
      text.textContent = rms.toFixed(4);
      
      recordDiv.appendChild(bar);
      recordDiv.appendChild(text);
      window.audioLevelHistory.appendChild(recordDiv);
      
      // Keep only most recent entries
      while (window.audioLevelHistory.children.length > 51) {
        // first child is the <h4> header, preserve it
        window.audioLevelHistory.removeChild(window.audioLevelHistory.children[1]);
      }
    }
    
    // Track noise levels for statistics
    currentNoiseLevel = rms;
    noiseValues.push(rms);
    if (noiseValues.length > 100) { // Keep last 100 values (5 seconds at 50ms intervals)
      noiseValues.shift();
    }
    
    // Update statistics
    if (rms > maxNoiseLevel) {
      maxNoiseLevel = rms;
    }
    
    // Calculate average noise level
    const sum = noiseValues.reduce((a, b) => a + b, 0);
    averageNoiseLevel = sum / noiseValues.length;
    
    // Update visualization in debug panel
    if (debugPanel.style.display !== 'none') {
      // Update current level bar
      const levelBar = document.getElementById('currentAudioLevel');
      levelBar.style.width = `${Math.min(rms * 1000, 100)}%`;
      levelBar.style.backgroundColor = rms > window.silenceThreshold ? '#4CAF50' : '#F44336';
      
      // Update threshold line position
      const thresholdLine = document.getElementById('thresholdLine');
      thresholdLine.style.top = `-${Math.min(window.silenceThreshold * 1000, 100)}%`;
      
      // Update stats
      document.getElementById('currentNoise').textContent = rms.toFixed(4);
      document.getElementById('averageNoise').textContent = averageNoiseLevel.toFixed(4);
      document.getElementById('maxNoise').textContent = maxNoiseLevel.toFixed(4);
      document.getElementById('currentAudioValue').textContent = rms.toFixed(3);
      
      // Calculate recommended threshold - typically slightly above the average
      const recommendedThreshold = Math.min(averageNoiseLevel * 1.5, (averageNoiseLevel + maxNoiseLevel) / 4);
      document.getElementById('recommendedThreshold').textContent = recommendedThreshold.toFixed(4);
    }
  },
  handleServerEvent: async function(message) {
    let ev;
    try {
      ev = JSON.parse(message);
    } catch (err) {
      console.error('Failed to parse WS message', err);
      return;
    }
    const { event, data } = ev;
    // Log only meaningful events (prompt, errors, done), skip per-token and audio-chunk logs
    if (event === 'prompt' || event === 'error' || event === 'done') {
      eventLog(`${event}${data ? ': ' + (data.prompt || data.error) : ''}`);
    }
    switch (event) {
      case 'prompt':
        // Record transcription received time
        const nowT = Date.now();
        window.lastTranscriptionTime = nowT;
        optimizationManager.trackLatency('transcriptionReceived');
        createUserMessage(data.prompt);
        break;
      case 'token':
        // On first token of a new response, stop previous audio and create new bot message
        if (!window.currentBot) {
          stopAllAudio();
          const botObj = createBotMessage('');
          botObj._audioStopped = false;
          window.currentBot = botObj;
          // Track LLM response start and text latency
          const nowL = Date.now();
          optimizationManager.trackLatency('llmResponseStart');
          if (window.lastTranscriptionTime) {
            const textLat = nowL - window.lastTranscriptionTime;
            botObj.textSpan.textContent = textLat + ' ms';
          }
          // Show stop button and attach handler
          botObj.stopButton.style.display = 'inline-block';
          botObj.stopButton.onclick = () => {
            // Mark stopped to ignore further chunks
            botObj._audioStopped = true;
            // Stop WebSocket AudioContext source if present
            if (botObj.audioSource) {
              try { botObj.audioSource.stop(); } catch {};
            }
            // Stop HTML AudioElement if present
            if (botObj.audioElement) {
              botObj.audioElement.pause();
            }
            botObj.stopButton.style.display = 'none';
            stopAllAudio();
          };
        }
        window.currentBot.content.textContent += data.token;
        break;
        case 'audio-chunk':
        // Wenn vom Backend ein Index mitgeschickt wird, verwenden wir diesen
        const chunkIndex = data.index !== undefined ? data.index : 0;
        console.log(`%c[AUDIO-DEBUG] Chunk arrived: index=${chunkIndex}, ${data.chunk.length} bytes, timestamp=${Date.now()}`, 'background: #3498db; color: white; padding: 2px 5px; border-radius: 3px;');
        
        // If user stopped this message's audio, skip further chunks
        if (window.currentBot && window.currentBot._audioStopped) {
          break;
        }
        
        // On the first audio chunk, reset the playback system
        if (chunkIndex === 0) {
          // Stop any current playback
          window.allAudioSources?.forEach(s => { try { s.stop(); } catch { } });
          // Clear any existing chunks
          indexedAudioChunks.clear();
          // Reset playback index
          nextPlaybackIndex = 0;
          console.log("%c[AUDIO-DEBUG] First chunk detected, reset audio playback system", 
            'background: #1abc9c; color: white; padding: 2px 5px; border-radius: 3px;');
        }
        
        // Record audio latency on first chunk
        const nowA = Date.now();
        optimizationManager.trackLatency('ttsEnd');
        if (window.currentBot && !window.currentBot._audioLatencyRecorded) {
          window.currentBot._audioLatencyRecorded = true;
          if (window.lastTranscriptionTime) {
            const audioLat = nowA - window.lastTranscriptionTime;
            window.currentBot.audioSpan.textContent = audioLat + ' ms';
          }
        }

        // 1. Bytes in ArrayBuffer umwandeln
        const bytes = Uint8Array.from(atob(data.chunk), c => c.charCodeAt(0));
        const arrayBuffer = bytes.buffer;

        try {
            // 2. Promise‐Decode
            const audioBuffer = await new Promise((resolve, reject) =>
                window.audioContext.decodeAudioData(arrayBuffer, resolve, reject)
            );

            // 3. In die Map mit dem richtigen Index speichern
            indexedAudioChunks.set(chunkIndex, audioBuffer);
            console.log(`%c[AUDIO-DEBUG] Chunk stored: index=${chunkIndex}, duration=${audioBuffer.duration.toFixed(2)}s, chunks in map=${indexedAudioChunks.size}`, 
                'background: #2ecc71; color: white; padding: 2px 5px; border-radius: 3px;');
                
            // Debug-Ausgabe aller vorhandenen Chunks
            const allIndices = Array.from(indexedAudioChunks.keys()).sort((a, b) => a - b);
            console.log(`%c[AUDIO-DEBUG] Current chunks in map: ${allIndices.join(', ')}`, 
                'background: #95a5a6; color: white; padding: 2px 5px; border-radius: 3px;');
            
                // Now we handle the chunk reset at the arrival stage, just store this buffer
            
            // Start playback loop if not already active
            playLoop();
        } catch (err) {
            console.error(`Fehler beim Dekodieren des Audio-Chunks ${chunkIndex}:`, err);
        }

        break;
      case 'audio-done':
        break;
      case 'done':
        // DON'T reset the audio yet - let all chunks finish playing naturally
        // Just mark the bot as completed and show a log
        console.log("%c[AUDIO-DEBUG] Done event received from server - letting audio continue playing", 
            'background: #e74c3c; color: white; padding: 2px 5px; border-radius: 3px;');
            
        // We'll set a flag indicating no more chunks will arrive, but existing ones should play
        window._allAudioChunksReceived = true;
        
        // Schedule cleanup for the NEXT message, not this one
        window._cleanupAfterPlayback = () => {
          // Only clear if we're not already in another message
          if (window._allAudioChunksReceived) {
            console.log("[done] Final audio cleanup after playback");
            window.currentBot = null;
            isPlaying = false;
            window._allAudioChunksReceived = false;
          }
        };
        
        // Check if all chunks have already been processed, and if so, mark playback as complete
        if (indexedAudioChunks.size === 0 && !isLoopActive) {
          console.log("[done] All audio chunks already processed - marking playback as completed");
          window._cleanupAfterPlayback();
        }
        break;
      case 'error':
        console.error('Server error', data.error);
        break;
    }
  }
};