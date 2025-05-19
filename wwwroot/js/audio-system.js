const FrameDurationMs = 20;
const TargetSampleRate = 16000;

// Audio system management
const audioSystem = {
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
      this.initServerASR();
    }, 300);
  },
  
  setupEventListeners: function() {
    // Global stop/start button for controlling recording
    stopBtn.addEventListener('click', function stopRecordingHandler() {
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
    optimizationManager.trackLatency('recordingStart');
    if (asrMode.value === 'browser' && (window.SpeechRecognition || window.webkitSpeechRecognition)) {
      this.initBrowserASR();
    } else {
      this.initServerASR();
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
  handleServerEvent: function(message) {
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
        // Track transcription received latency
        optimizationManager.trackLatency('transcriptionReceived');
        createUserMessage(data.prompt);
        break;
      case 'token':
        // Track LLM response start on first token
        optimizationManager.trackLatency('llmResponseStart');
        if (!window.currentBot) {
          window.currentBot = createBotMessage('');
        }
        window.currentBot.content.textContent += data.token;
        break;
      case 'audio-chunk':
        // Track TTS ready to play on first audio chunk
        optimizationManager.trackLatency('ttsEnd');
        {
          const bytes = Uint8Array.from(atob(data.chunk), c => c.charCodeAt(0));
          window.audioContext.decodeAudioData(bytes.buffer, buffer => {
            const src = window.audioContext.createBufferSource();
            src.buffer = buffer;
            src.connect(window.audioContext.destination);
            src.start();
          });
        }
        break;
      case 'audio-done':
        break;
      case 'done':
        window.currentBot = null;
        break;
      case 'error':
        console.error('Server error', data.error);
        break;
    }
  }
};