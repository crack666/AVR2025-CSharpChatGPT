// Text-to-Speech manager
const ttsManager = {
  // Helper to speak response with TTS or SpeechSynthesis
  async speakResponse(text, stopButton) {
    // Track TTS start for latency measurement
    optimizationManager.trackLatency('ttsStart');
    
    // Validate text input
    if (!text || text.trim().length === 0) {
      debugLog("Attempted to synthesize empty text");
      status.textContent = 'Kein Text zum Vorlesen';
      return;
    }

    // Make sure we're in a state where we can play audio
    if (!window.recordingEnabled) {
      debugLog("Cannot play audio - recording is disabled");
      status.textContent = 'Audio-Wiedergabe nicht möglich - Aufnahme deaktiviert';
      return;
    }

    // Abort any previous playback
    stopAllAudio();

    // Signal that we're processing audio to prevent audio capture from interfering
    window.isProcessingOrPlayingAudio = true;

    // Ensure text is properly trimmed
    text = text.trim();
    
    if (openaiVoices.includes(voiceSel.value)) {
      // Check if we should use progressive/streaming TTS based on user settings
      const useProgressiveTTS = window.optimizationSettings.useProgressiveTTS && text.length > 30;
      
      if (useProgressiveTTS) {
        await this.useProgressiveTTS(text, stopButton);
      } else {
        await this.useStandardTTS(text, stopButton);
      }
    } else {
      await this.useBrowserTTS(text, stopButton);
    }
  },
  
  async useProgressiveTTS(text, stopButton) {
    status.textContent = 'Progressive Sprachsynthese...';
    debugLog(`Progressive TTS für ${text.length} Zeichen mit Stimme ${voiceSel.value}`);
    
    try {
      // Configure variables for audio handling
      let chunkIndex = 0;
      let audioElements = [];
      let currentAudioElement = null;
      let playingIndex = 0;
      let allChunks = [];
      let isPlaying = false;
      
      // Store audioElements globally so we can clean them up from stopAllAudio
      window.allAudioElements = audioElements;
      
      // Unique request ID for this synthesis request
      const requestId = Date.now().toString();
      
      // Configure stop button early
      if (stopButton) {
        stopButton.style.display = 'inline-block';
        stopButton.onclick = () => {
          // Use stopAllAudio to ensure complete cleanup
          stopAllAudio();
          
          // Reset local state variables
          audioElements = [];
          currentAudioElement = null;
          allChunks = [];
          isPlaying = false;
          
          // Close EventSource explicitly
          if (window.eventSource) {
            window.eventSource.close();
            window.eventSource = null;
          }
          
          // CRITICAL: Make sure audio processing is always re-enabled
          window.isProcessingOrPlayingAudio = false;
          
          // Force debug log
          debugLog("Audio Chunk Button: Audio gestoppt, Audio-Processing wieder aktiviert");
          
          stopButton.style.display = 'none';
          status.textContent = 'Audio gestoppt - Aufnahme aktiv';
        };
      }
      
      // Start the streaming request
      fetch('/api/streamingSpeech', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ 
          Input: text, 
          Voice: voiceSel.value,
          UseSmartChunking: window.optimizationSettings.useSmartChunkSplitting,
          ChunkSize: window.optimizationSettings.ttsDynamicChunkSize 
        })
      }).then(response => {
        if (response.ok) {
          // Set up SSE connection with text and voice as query params
          // This allows the EventSource to know what text to synthesize
          const sseUrl = `/api/streamingSpeech?_=${requestId}&text=${encodeURIComponent(text)}&voice=${encodeURIComponent(voiceSel.value)}&useSmartChunking=${window.optimizationSettings.useSmartChunkSplitting}&chunkSize=${window.optimizationSettings.ttsDynamicChunkSize}`;
          const eventSource = new EventSource(sseUrl);
          
          // Store eventSource globally so we can close it manually from stopAllAudio
          window.eventSource = eventSource;
          
          // Event handler for the initial info event
          eventSource.addEventListener('info', (event) => {
            try {
              const data = JSON.parse(event.data);
              debugLog(`Progressive TTS gestartet: ${data.message}`);
              status.textContent = 'Synthesizing speech...';
            } catch (e) {
              debugLog(`Fehler beim Verarbeiten des Info-Events: ${e.toString()}`);
            }
          });
          
          // Event handler for each audio chunk
          eventSource.addEventListener('chunk', (event) => {
            try {
              const data = JSON.parse(event.data);
              chunkIndex++;
              
              debugLog(`Audio-Chunk ${data.index} empfangen`);
              
              // Convert Base64 to Blob
              const binaryString = atob(data.audio);
              const bytes = new Uint8Array(binaryString.length);
              for (let i = 0; i < binaryString.length; i++) {
                bytes[i] = binaryString.charCodeAt(i);
              }
              const audioBlob = new Blob([bytes], { type: 'audio/mpeg' });
              allChunks.push(audioBlob);
              
              // Create audio element
              const audioUrl = URL.createObjectURL(audioBlob);
              const audio = new Audio(audioUrl);
              audioElements.push(audio);
              
              // Configure audio events
              audio.onended = () => {
                // Play next chunk if available
                playingIndex++;
                if (playingIndex < audioElements.length) {
                  currentAudioElement = audioElements[playingIndex];
                  currentAudioElement.play().catch(e => {
                    debugLog(`Fehler beim Abspielen des Chunks ${playingIndex}: ${e}`);
                  });
                } else {
                  // All chunks played
                  isPlaying = false;
                  // Reset global flag when audio playback is complete
                  window.isProcessingOrPlayingAudio = false;
                  if (stopButton) stopButton.style.display = 'none';
                  status.textContent = 'Bereit';
                  // Track TTS end for latency measurement
                  optimizationManager.trackLatency('ttsEnd', window.currentLatencyElem);
                }
              };
              
              // Start playback of first chunk immediately
              if (!isPlaying && playingIndex === 0) {
                isPlaying = true;
                currentAudioElement = audio;
                // Set global flag to prevent recording restart during audio playback
                window.isProcessingOrPlayingAudio = true;
                
                audio.play().then(() => {
                  status.textContent = 'Spielt Audio...';
                  
                  // Measure audio latency at playback start
                  const stats = window.optimizationSettings.latencyStats;
                  if (stats && stats.recordingStart > 0) {
                    const audioLatencyValue = Date.now() - stats.recordingStart;
                    
                    // Ensure audioLatency array exists (backwards compatibility)
                    if (!stats.audioLatency) {
                      stats.audioLatency = [];
                    }
                    
                    // Save audio latency for statistics
                    stats.audioLatency.push(audioLatencyValue);
                    if (stats.audioLatency.length > 10) stats.audioLatency.shift();
                    
                    // Update latency in message bubble if element is provided
                    if (window.currentLatencyElem && window.currentLatencyElem.querySelector) {
                      const latencyAudioElem = window.currentLatencyElem.querySelector('.latency-audio-value');
                      if (latencyAudioElem) {
                        latencyAudioElem.textContent = `${audioLatencyValue} ms`;
                        
                        // Add color coding based on latency
                        if (audioLatencyValue < 2000) {
                          latencyAudioElem.style.color = '#4CAF50'; // Green for good
                        } else if (audioLatencyValue < 5000) {
                          latencyAudioElem.style.color = '#FF9800'; // Orange for medium
                        } else {
                          latencyAudioElem.style.color = '#F44336'; // Red for slow
                        }
                      }
                    }
                  }
                }).catch(e => {
                  debugLog(`Fehler beim Abspielen des ersten Chunks: ${e}`);
                  status.textContent = 'Fehler beim Abspielen';
                  window.isProcessingOrPlayingAudio = false; // Reset flag on error
                });
              }
            } catch (e) {
              debugLog(`Fehler beim Verarbeiten des Chunk-Events: ${e.toString()}`);
            }
          });
          
          // Event handler for completion
          eventSource.addEventListener('done', (event) => {
            try {
              const data = JSON.parse(event.data);
              debugLog(`Progressive TTS abgeschlossen: ${data.totalChunks} Chunks insgesamt`);
              eventSource.close();
            } catch (e) {
              debugLog(`Fehler beim Verarbeiten des Done-Events: ${e.toString()}`);
              eventSource.close();
            }
          });
          
          // Error handling for SSE
          eventSource.addEventListener('error', (event) => {
            debugLog(`SSE-Fehler: ${event.toString()}`);
            eventSource.close();
            if (!isPlaying && allChunks.length === 0) {
              status.textContent = 'Fehler bei der Sprachsynthese';
              if (stopButton) stopButton.style.display = 'none';
            }
          });
        } else {
          debugLog(`HTTP-Fehler bei progressiver TTS: ${response.status}`);
          status.textContent = `Fehler bei der TTS: ${response.status}`;
          if (stopButton) stopButton.style.display = 'none';
        }
      }).catch(e => {
        debugLog(`Fehler beim Starten der progressiven TTS: ${e.toString()}`);
        status.textContent = 'Fehler beim Starten der TTS';
        if (stopButton) stopButton.style.display = 'none';
      });
      
      // Weitere Logik siehe fetch.then() Callback oben
      
      // Return a promise that resolves when playback is done
      return new Promise((resolve) => {
        const checkInterval = setInterval(() => {
          if (!isPlaying && allChunks.length > 0) {
            clearInterval(checkInterval);
            resolve();
          }
        }, 100);
      });
    } catch (error) {
      debugLog(`Progressive TTS-Fehler: ${error.toString()}`);
      status.textContent = 'Fehler bei der progressiven TTS';
      if (stopButton) stopButton.style.display = 'none';
    }
  },
  
  async useStandardTTS(text, stopButton) {
    status.textContent = 'Synthetisiere Sprache...';
    try {
      debugLog(`Standard-TTS für ${text.length} Zeichen mit Stimme ${voiceSel.value}`);
      
      const resp2 = await fetch('/api/speech', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ 
          Input: text, 
          Voice: voiceSel.value,
          UseSmartChunking: window.optimizationSettings.useSmartChunkSplitting,
          ChunkSize: window.optimizationSettings.ttsDynamicChunkSize 
        })
      });
      
      if (!resp2.ok) {
        const errText = await resp2.text();
        debugLog(`Fehler bei der Sprachsynthese: ${resp2.status} ${errText}`);
        status.textContent = `Fehler bei der Sprachsynthese: ${resp2.status}`;
        return;
      }
      
      const audioBlob = await resp2.blob();
      debugLog(`Audiodaten empfangen: ${Math.round(audioBlob.size / 1024)} KB`);
      
      const audioUrl = URL.createObjectURL(audioBlob);
      const audio = new Audio(audioUrl);
      currentAudio = audio;
      
      // Configure stop button
      if (stopButton) {
        stopButton.style.display = 'inline-block';
        stopButton.onclick = () => {
          stopAudio(audio);
          stopButton.style.display = 'none';
          status.textContent = 'Audio gestoppt';
        };
      }
      
      // Play audio
      status.textContent = 'Spielt Audio...';
      
      await new Promise(resolve => {
        // Measure latency at audio play start
        audio.onplay = () => {
          const stats = window.optimizationSettings.latencyStats;
          if (stats && stats.recordingStart > 0) {
            const audioLatencyValue = Date.now() - stats.recordingStart;
            
            // Ensure audioLatency array exists (backwards compatibility)
            if (!stats.audioLatency) {
              stats.audioLatency = [];
            }
            
            // Save audio latency for statistics
            stats.audioLatency.push(audioLatencyValue);
            if (stats.audioLatency.length > 10) stats.audioLatency.shift();
            
            // Update latency in message bubble
            if (window.currentLatencyElem && window.currentLatencyElem.querySelector) {
              const latencyAudioElem = window.currentLatencyElem.querySelector('.latency-audio-value');
              if (latencyAudioElem) {
                latencyAudioElem.textContent = `${audioLatencyValue} ms`;
                
                // Add color coding based on latency
                if (audioLatencyValue < 2000) {
                  latencyAudioElem.style.color = '#4CAF50'; // Green for good
                } else if (audioLatencyValue < 5000) {
                  latencyAudioElem.style.color = '#FF9800'; // Orange for medium
                } else {
                  latencyAudioElem.style.color = '#F44336'; // Red for slow
                }
              }
            }
          }
        };
        
        audio.onended = () => { 
          URL.revokeObjectURL(audioUrl); 
          currentAudio = null;
          if (stopButton) stopButton.style.display = 'none';
          status.textContent = 'Bereit';
          // Track TTS end for latency measurement
          optimizationManager.trackLatency('ttsEnd', window.currentLatencyElem);
          resolve();
        };
        
        audio.onerror = (e) => {
          debugLog(`Fehler bei der Audiowiedergabe: ${e.toString()}`);
          URL.revokeObjectURL(audioUrl);
          currentAudio = null;
          if (stopButton) stopButton.style.display = 'none';
          status.textContent = 'Fehler bei der Audiowiedergabe';
          resolve();
        };
        
        audio.play().catch(e => {
          debugLog(`Fehler beim Starten der Audiowiedergabe: ${e.toString()}`);
          URL.revokeObjectURL(audioUrl);
          currentAudio = null;
          if (stopButton) stopButton.style.display = 'none';
          status.textContent = 'Fehler beim Starten der Audiowiedergabe';
          resolve();
        });
      });
    } catch (error) {
      debugLog(`TTS-Fehler: ${error.toString()}`);
      status.textContent = `Fehler bei der Sprachsynthese`;
    }
  },
  
  async useBrowserTTS(text, stopButton) {
    status.textContent = 'Spricht...';
    try {
      debugLog(`Verwende Browser-SpeechSynthesis mit Stimme ${voiceSel.value}`);
      
      await new Promise(resolve => {
        const utter = new SpeechSynthesisUtterance(text);
        currentUtterance = utter;
        utter.lang = langSel.value;
        const selectedVoice = speechSynthesis.getVoices().find(v => v.name === voiceSel.value);
        if (selectedVoice) utter.voice = selectedVoice;
        
        // Measure latency when speech starts
        utter.onstart = () => {
          const stats = window.optimizationSettings.latencyStats;
          if (stats && stats.recordingStart > 0) {
            const audioLatencyValue = Date.now() - stats.recordingStart;
            
            // Ensure audioLatency array exists (backwards compatibility)
            if (!stats.audioLatency) {
              stats.audioLatency = [];
            }
            
            // Save audio latency for statistics
            stats.audioLatency.push(audioLatencyValue);
            if (stats.audioLatency.length > 10) stats.audioLatency.shift();
            
            // Update latency in message bubble
            if (window.currentLatencyElem && window.currentLatencyElem.querySelector) {
              const latencyAudioElem = window.currentLatencyElem.querySelector('.latency-audio-value');
              if (latencyAudioElem) {
                latencyAudioElem.textContent = `${audioLatencyValue} ms`;
                
                // Add color coding based on latency
                if (audioLatencyValue < 2000) {
                  latencyAudioElem.style.color = '#4CAF50'; // Green for good
                } else if (audioLatencyValue < 5000) {
                  latencyAudioElem.style.color = '#FF9800'; // Orange for medium
                } else {
                  latencyAudioElem.style.color = '#F44336'; // Red for slow
                }
              }
            }
          }
        };
        
        // Configure stop button
        if (stopButton) {
          stopButton.style.display = 'inline-block';
          stopButton.onclick = () => {
            if (currentUtterance) {
              speechSynthesis.cancel();
              currentUtterance = null;
            }
            stopButton.style.display = 'none';
            status.textContent = 'Audio gestoppt';
            resolve();
          };
        }
        
        utter.onend = () => { 
          currentUtterance = null; 
          if (stopButton) stopButton.style.display = 'none';
          status.textContent = 'Bereit';
          // Track TTS end for latency measurement
          optimizationManager.trackLatency('ttsEnd', window.currentLatencyElem);
          resolve();
        };
        
        utter.onerror = (e) => { 
          debugLog(`SpeechSynthesis-Fehler: ${e.toString()}`);
          currentUtterance = null;
          if (stopButton) stopButton.style.display = 'none';
          status.textContent = 'Fehler bei der Sprachsynthese';
          resolve();
        };
        
        speechSynthesis.speak(utter);
      });
    } catch (error) {
      debugLog(`Browser-Sprachsynthese-Fehler: ${error.toString()}`);
      status.textContent = `Fehler bei der Browser-Sprachsynthese`;
    }
  }
};