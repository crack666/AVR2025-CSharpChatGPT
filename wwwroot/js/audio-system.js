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
    
    // Initialize VAD debug sliders and global parameters
    const thresholdSlider = document.getElementById('thresholdSlider');
    const thresholdValue = document.getElementById('thresholdValue');
    window.silenceThreshold = parseFloat(thresholdSlider.value);
    thresholdValue.textContent = thresholdSlider.value;
    thresholdSlider.addEventListener('input', () => {
      window.silenceThreshold = parseFloat(thresholdSlider.value);
      thresholdValue.textContent = thresholdSlider.value;
    });

    window.silenceSecInput = document.getElementById('silenceTimeoutSlider');
    document.getElementById('silenceTimeoutValue').textContent = window.silenceSecInput.value;
    window.silenceSecInput.addEventListener('input', () => {
      document.getElementById('silenceTimeoutValue').textContent = window.silenceSecInput.value;
    });

    const minSpeechDurationSlider = document.getElementById('minSpeechDurationSlider');
    document.getElementById('minSpeechDurationValue').textContent = minSpeechDurationSlider.value;
    minSpeechDurationSlider.addEventListener('input', () => {
      document.getElementById('minSpeechDurationValue').textContent = minSpeechDurationSlider.value;
    });

    this.initCapture();
    this.setupEventListeners();
  },

  // Function to completely reset and restart audio recording
  restartAudioCapture: function() {
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
      // Now restart the audio capture
      navigator.mediaDevices.getUserMedia({ audio: true })
        .then(stream => {
          window.audioStream = stream;
          // Create new context only if needed or reuse existing one
          if (!window.audioContext || !window.optimizationSettings.useCachedAudioContext) {
            window.audioContext = new AudioContext();
          }
          const source = window.audioContext.createMediaStreamSource(stream);
          window.audioAnalyser = window.audioContext.createAnalyser();
          window.audioAnalyser.fftSize = 1024;
          source.connect(window.audioAnalyser);
          
          // Initialize recorder and re-use original processing logic
          window.recorder = new MediaRecorder(stream);
          // Use global chunks array for recording data
          window.chunks = [];
          window.recorder.ondataavailable = e => window.chunks.push(e.data);
          // Delegate stop handler to the standard processing function
          window.recorder.onstop = audioSystem.processSpeechRecording;
          
          debugLog("Audio capture system successfully restarted");
          status.textContent = 'Zuhören...';
          stopBtn.textContent = 'Aufnahme stoppen';
        })
        .catch(err => {
          debugLog("Error restarting audio capture: " + err);
          status.textContent = 'Fehler beim Neustart der Aufnahme';
        });
    }, 300); // Small delay to ensure clean restart
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
    // Use global references for all audio components
    window.audioStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    window.audioContext = new AudioContext();
    const source = window.audioContext.createMediaStreamSource(window.audioStream);
    window.audioAnalyser = window.audioContext.createAnalyser();
    // Reduce FFT size for lower-latency voice activity detection
    window.audioAnalyser.fftSize = 1024;
    source.connect(window.audioAnalyser);
    const dataArray = new Uint8Array(window.audioAnalyser.fftSize);
    window.recorder = new MediaRecorder(window.audioStream);
    window.chunks = []; // Use global variable for chunks
    window.recorder.ondataavailable = e => window.chunks.push(e.data);
    window.recorder.onstop = this.processSpeechRecording;
    
    status.textContent = 'Zuhören...';
    
    // Poll more frequently for quicker reaction (ms)
    setInterval(() => {
      // Skip processing completely if we don't have audio system initialized
      if (!window.audioAnalyser || !window.audioContext) {
        return;
      }
      
      // Skip audio analysis and recording if recording is disabled
      if (!window.recordingEnabled || !window.isListening) {
        // Stop recording if it's active and we've disabled recording
        if (window.speakingSegment) {
          debugLog("Recording stopped due to global flag change");
          window.speakingSegment = false;
          window.silenceStart = null;
        }
        return;
      }
      
      // Skip voice activity detection if we're currently processing a recording
      // (but keep monitoring so we catch actual words once processing is done)
      if (window.isProcessingOrPlayingAudio && !window.optimizationSettings.useEarlyAudioProcessing) {
        return;
      }
      
      window.audioAnalyser.getByteTimeDomainData(dataArray);
      let sumSquares = 0;
      for (let i = 0; i < dataArray.length; i++) {
        const v = (dataArray[i] - 128) / 128;
        sumSquares += v * v;
      }
      const rms = Math.sqrt(sumSquares / dataArray.length);
      
      // Update visualization and statistics
      this.updateAudioVisualization(rms);
      
      // Check if we should start/stop recording
      if (rms > silenceThreshold) {
        if (!window.speakingSegment && window.recorder) {
          window.speakingSegment = true;
          window.chunks = [];
          try {
            // Store the exact recording start time for duration calculation
            window.recordingStartTime = Date.now();
            
            window.recorder.start();
            status.textContent = 'Recording...';
            debugLog("Recording started - audio level above threshold: " + rms.toFixed(4));
            // Track recording start for latency measurement
            optimizationManager.trackLatency('recordingStart');
          } catch (e) {
            debugLog("Error starting recorder: " + e.toString());
            // Try to restart audio system if starting fails
            if (e.name === "InvalidStateError") {
              debugLog("Recorder in invalid state, attempting restart");
              this.restartAudioCapture();
            }
          }
        }
        window.silenceStart = null;
      } else {
        if (window.speakingSegment && window.recorder) {
          // Track recording duration to prevent too short recordings
          const recordingDuration = Date.now() - (window.recordingStartTime || Date.now());
          
          if (!window.silenceStart) {
            window.silenceStart = Date.now();
            debugLog("Silence detected, starting silence timer");
          }
          else if (Date.now() - window.silenceStart > parseFloat(silenceSecInput.value) * 1000) {
            // Calculate the average noise level during the recording
            const avgNoise = noiseValues.length > 0 ? noiseValues.reduce((a, b) => a + b, 0) / noiseValues.length : 0;
            const significantSpeech = avgNoise > silenceThreshold * 1.2; // Ensure we had meaningful audio input
            
            // Only stop if recording meets quality criteria:
            // 1. At least 1 second long to prevent accidental short recordings
            // 2. Average noise level significantly above threshold
            if (recordingDuration >= 1000 && significantSpeech) {
              window.speakingSegment = false;
              debugLog(`Stopping recording after ${parseFloat(silenceSecInput.value)} seconds of silence, duration: ${recordingDuration}ms, avg noise: ${avgNoise.toFixed(4)}`);
              
              // Force a more obvious visual feedback
              status.textContent = 'Processing...';
              //status.style.fontWeight = 'bold';
              
              try {
                if (window.recorder.state === "recording") {
                  window.recorder.stop();
                } else {
                  debugLog("Cannot stop recorder: not in recording state");
                }
              } catch (e) {
                debugLog("Error stopping recorder: " + e.toString());
              }
            } else if (!significantSpeech) {
              debugLog(`Ignoring recording with insufficient audio level (avg: ${avgNoise.toFixed(4)}, threshold: ${silenceThreshold})`);
              // Reset and continue recording
              window.silenceStart = null;
              window.speakingSegment = false;
              
              // Give visual feedback to the user
              status.textContent = 'Zu leise Aufnahme - bitte lauter sprechen';
              setTimeout(() => {
                if (window.recordingEnabled && window.isListening) {
                  status.textContent = 'Zuhören...';
                }
              }, 2000);
            } else {
              debugLog(`Ignoring too short recording (${recordingDuration}ms) - need at least 1000ms`);
              // Reset silence detection but continue recording
              window.silenceStart = null;
            }
          }
        }
      }
    }, 50);
  },
  
  processSpeechRecording: async function() {
    debugLog("Speech processing started (onstop handler)");
    // If stop was triggered by restartAudioCapture, skip processing
    if (window._ignoreNextStop) {
      debugLog("Skipping processSpeechRecording due to audio system restart");
      window._ignoreNextStop = false;
      return;
    }
    debugLog("Speech processing started (onstop handler)");
    // Set flag to avoid auto-restarting recording during processing
    window.isProcessingOrPlayingAudio = true;
    
    status.textContent = 'Uploading...';
    debugLog(`Uploading audio for processing (${(window.chunks||[]).length} chunks)`);
    const blob = new Blob(window.chunks || [], { type: 'audio/webm' });
    const fd = new FormData();
    fd.append('file', blob, 'audio.webm');
    fd.append('model', modelSel.value);
    fd.append('language', langSel.value);
    
    try {
      debugLog("Sending fetch to /api/processAudioStream");
      status.textContent = 'Connecting...';
      const resp = await fetch('/api/processAudioStream', { method: 'POST', body: fd });
      debugLog(`Fetch response status: ${resp.status}`);
      
      if (!resp.ok) { 
        const errorText = await resp.text();
        console.error(`Server error: ${resp.status}, ${errorText}`);
        status.textContent = `Error: ${resp.status}`; 
        return; 
      }
      
      debugLog("Connection established, processing response stream (SSE)");
      const reader = resp.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      let content = '';
      
      // We'll create the bot message bubble after we receive the user's prompt
      let contentElem = null;
      let stopButton = null;
      
      // Flag to track if we've received any tokens
      let receivedAnyTokens = false;
      
      // Parse SSE stream
      while (true) {
        const { value, done: doneReading } = await reader.read();
        if (doneReading) {
          console.log("Stream reading done");
          break;
        }
        
        const chunk = decoder.decode(value, { stream: true });
        console.log(`Received chunk: ${chunk.length} bytes`);
        buffer += chunk;
        
        const parts = buffer.split('\n\n');
        buffer = parts.pop();
        
        console.log(`Processing ${parts.length} SSE parts`);
        
        for (const part of parts) {
          const lines = part.split('\n');
          let eventType = '';
          let dataLine = '';
          
          for (const line of lines) {
            if (line.startsWith('event: ')) {
              eventType = line.slice(7);
              console.log(`Event type: ${eventType}`);
            }
            else if (line.startsWith('data: ')) {
              dataLine = line.slice(6);
            }
          }
          
          if (eventType === 'prompt') {
            try {
              const obj = JSON.parse(dataLine);
              debugLog(`Prompt event received: "${obj.prompt}"`);
              // Track transcription received for latency measurement
              optimizationManager.trackLatency('transcriptionReceived');
              // Check if we have a non-empty prompt
              const trimmedPrompt = obj.prompt ? obj.prompt.trim() : "";
              if (trimmedPrompt.length > 0) {
                // Add user message first
                createUserMessage(obj.prompt);
                
                // Now create bot message bubble (AFTER the user message)
                const botMessage = createBotMessage('');
                contentElem = botMessage.content;
                stopButton = botMessage.stopButton;
                const latencyInfoElem = botMessage.latencyInfo;
                
                // Store reference to latencyInfoElem in global scope
                window.currentLatencyElem = latencyInfoElem;
              } else {
                // Log empty prompts
                debugLog("Empty prompt received, ignoring");
                
                // Reset processing to allow new recordings
                window.isProcessingOrPlayingAudio = false;
                window.speakingSegment = false;
                window.silenceStart = null;
                
                // Ensure to cancel any existing event sources
                if (window.eventSource) {
                  window.eventSource.close();
                  window.eventSource = null;
                  debugLog("Closed event source due to empty prompt");
                }
                
                status.textContent = 'Zuhören...';
                return; // Exit early
              }
            } catch (e) {
              console.error(`Error parsing prompt data: ${e.message}`);
            }
          } else if (eventType === 'token') {
            try {
              const obj = JSON.parse(dataLine);
              if (obj.token !== undefined) debugLog(`Token event received: "${obj.token}"`);
              if (obj.token !== undefined) {
                // Track first token received (LLM response start)
                if (!receivedAnyTokens) {
                  optimizationManager.trackLatency('llmResponseStart');
                  
                  // Measure text latency (from recording start to first token)
                  const stats = window.optimizationSettings.latencyStats;
                  if (stats && stats.recordingStart > 0) {
                    const textLatencyValue = Date.now() - stats.recordingStart;
                    
                    // Ensure textLatency array exists (backwards compatibility)
                    if (!stats.textLatency) {
                      stats.textLatency = [];
                    }
                    
                    stats.textLatency.push(textLatencyValue);
                    if (stats.textLatency.length > 10) stats.textLatency.shift();
                    
                    // Update latency in message bubble
                    if (window.currentLatencyElem && window.currentLatencyElem.querySelector) {
                      const latencyTextElem = window.currentLatencyElem.querySelector('.latency-text-value');
                      if (latencyTextElem) {
                        latencyTextElem.textContent = `${textLatencyValue} ms`;
                        
                        // Add color coding based on latency
                        if (textLatencyValue < 2000) {
                          latencyTextElem.style.color = '#4CAF50'; // Green for good
                        } else if (textLatencyValue < 5000) {
                          latencyTextElem.style.color = '#FF9800'; // Orange for medium
                        } else {
                          latencyTextElem.style.color = '#F44336'; // Red for slow
                        }
                      }
                    }
                  }
                }
                receivedAnyTokens = true;
                
                // Always collect the content, even if not displaying immediately
                content += obj.token;
                
                // Only update UI immediately if token streaming is enabled
                // Otherwise, collect tokens but don't update UI until the end
                if (window.optimizationSettings.useTokenStreaming) {
                  requestAnimationFrame(() => {
                    if (contentElem) {
                      contentElem.textContent = content;
                      chatLog.scrollTop = chatLog.scrollHeight;
                    }
                  });
                }
              }
            } catch (e) {
              console.error(`Error parsing token data: ${e.message}`);
              debugLog(`Error parsing token data: ${e.message}`);
            }
          } else if (!eventType || eventType === 'message') {
            try {
              const obj = JSON.parse(dataLine);
              debugLog(`Message event received: ${dataLine}`);
              
              if (obj.token !== undefined) {
                receivedAnyTokens = true;
                // Token-by-token streaming (optimized)
                content += obj.token;
                // Update UI more efficiently for token streaming
                requestAnimationFrame(() => {
                  if (contentElem) {
                    contentElem.textContent = content;
                    chatLog.scrollTop = chatLog.scrollHeight;
                  }
                });
              } else if (obj.message !== undefined) {
                receivedAnyTokens = true;
                content += obj.message;
                if (contentElem) {
                  contentElem.textContent = content;
                  chatLog.scrollTop = chatLog.scrollHeight;
                }
              } else if (obj.response !== undefined) {
                receivedAnyTokens = true;
                content += obj.response;
                if (contentElem) {
                  contentElem.textContent = content;
                  chatLog.scrollTop = chatLog.scrollHeight;
                }
              }
            } catch (e) {
              console.error(`Error parsing message data: ${e.message}, raw data: ${dataLine}`);
              debugLog(`Error parsing message data: ${e.message}, raw data: ${dataLine}`);
            }
          } else if (eventType === 'done') {
            debugLog(`Stream complete, received ${content.length} characters`);
            
            if (content && content.length > 0) {
              // Create bot message if it doesn't exist yet (failsafe)
              if (!contentElem) {
                const botMessage = createBotMessage(content);
                contentElem = botMessage.content;
                stopButton = botMessage.stopButton;
              } else {
                // Make sure content is displayed in any case
                contentElem.textContent = content;
              }
              
              await ttsManager.speakResponse(content, stopButton);
            } else {
              debugLog("Empty response content, nothing to speak");
              // Show a helpful message
              if (contentElem) {
                contentElem.textContent = "Keine Antwort generiert. Bitte versuchen Sie es erneut.";
              }
            }
            
            // Reset processing flag when done
            window.isProcessingOrPlayingAudio = false;
            status.textContent = 'Zuhören...';
            //status.style.fontWeight = 'normal';
            return;
          } else if (eventType === 'error') {
            try {
              const obj = JSON.parse(dataLine);
              console.error("Server error:", obj.error);
              status.textContent = `Error: ${obj.error}`;
            } catch (e) {
              console.error(`Error parsing error event: ${e.message}`);
            }
            return;
          }
        }
      }
      
      // If we reached end of stream without 'done' event
      debugLog("Stream ended without done event");
      if (receivedAnyTokens) {
        // Create bot message if it doesn't exist yet (failsafe)
        if (!contentElem) {
          const botMessage = createBotMessage(content);
          contentElem = botMessage.content;
          stopButton = botMessage.stopButton;
        }
        
        await ttsManager.speakResponse(content, stopButton);
      } else {
        debugLog("No tokens received in the response");
        if (contentElem) {
          contentElem.textContent = "Keine Antwort empfangen. Bitte versuchen Sie es erneut.";
        }
      }
      
      // Reset processing flag when done or on error
      window.isProcessingOrPlayingAudio = false;
      
      // Only update status if recording is still enabled
      if (window.recordingEnabled && window.isListening) {
        status.textContent = 'Zuhören...';
        //status.style.fontWeight = 'normal';
      }
    } catch (err) {
      console.error(err);
      status.textContent = 'Error in processing';
      // Reset processing flag on error too
      window.isProcessingOrPlayingAudio = false;
      
      // Only update status if recording is still enabled
      if (window.recordingEnabled && window.isListening) {
        status.textContent = 'Zuhören...';
      }
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
      bar.style.backgroundColor = rms > silenceThreshold ? '#4CAF50' : '#F44336';
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
      levelBar.style.backgroundColor = rms > silenceThreshold ? '#4CAF50' : '#F44336';
      
      // Update threshold line position
      const thresholdLine = document.getElementById('thresholdLine');
      thresholdLine.style.top = `-${Math.min(silenceThreshold * 1000, 100)}%`;
      
      // Update stats
      document.getElementById('currentNoise').textContent = rms.toFixed(4);
      document.getElementById('averageNoise').textContent = averageNoiseLevel.toFixed(4);
      document.getElementById('maxNoise').textContent = maxNoiseLevel.toFixed(4);
      document.getElementById('currentAudioValue').textContent = rms.toFixed(3);
      
      // Calculate recommended threshold - typically slightly above the average
      const recommendedThreshold = Math.min(averageNoiseLevel * 1.5, (averageNoiseLevel + maxNoiseLevel) / 4);
      document.getElementById('recommendedThreshold').textContent = recommendedThreshold.toFixed(4);
    }
  }
};