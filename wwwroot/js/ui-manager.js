// Import functions from audio-system.js
import { startRecording, stopRecording, restartAudioSystemAndClearState, stopAllAudioPlayback } from './audio-system.js';
import { traceLog, debugLog } from './audio-utils.js';

// UI Manager
const uiManager = {
  init: function() {
    // 1. Assign DOM elements to instance properties FIRST
    this.stopBtn = document.getElementById('stopBtn');
    this.clearBtn = document.getElementById('clearBtn');
    this.debugBtn = document.getElementById('debugBtn');
    this.optimizationBtn = document.getElementById('optimizationBtn');
    this.chatLog = document.getElementById('chatLog');
    this.status = document.getElementById('status');
    this.modelSel = document.getElementById('model');
    this.voiceSel = document.getElementById('voice');
    this.debugPanel = document.getElementById('debugPanel');
    this.optimizationPanel = document.getElementById('optimizationPanel');
    this.currentModelEl = document.getElementById('currentModel');
    this.stopAudioPlaybackBtn = document.getElementById('stopAudioPlaybackBtn');
    this.restartAudioSysBtn = document.getElementById('restartAudioSysBtn');

    // 2. THEN setup event listeners that use these properties
    this.setupEventListeners();
    
    // 3. Other initializations like loading models and voices
    // Ensure modelSel is found before adding event listener or calling loadModels if it depends on it.
    if (this.modelSel) {
        const loadPromise = loadModels(); // Assuming loadModels is defined globally or imported
        const currentModelElRef = this.currentModelEl; // Use the already queried element

        this.modelSel.addEventListener('change', () => { 
          if (currentModelElRef) currentModelElRef.textContent = this.modelSel.value; 
        });
        loadPromise.then(() => { 
          if (currentModelElRef) currentModelElRef.textContent = this.modelSel.value; 
        });
    } else {
        console.warn("[UI-MGR] Model select element (#model) not found during init.");
    }
    
    // Initialize speech synthesis voices
    // Assuming populateVoices is defined globally or imported
    speechSynthesis.onvoiceschanged = populateVoices;
    populateVoices();

    console.log("[UI-MGR] UI Manager initialized.");
  },
  
  setupEventListeners: function() {
    // Main recording Start/Stop Button
    if (this.stopBtn) {
        this.stopBtn.addEventListener('click', () => {
            if (this.stopBtn.textContent === 'Aufnahme starten' || this.stopBtn.textContent === 'Neu verbinden') {
                console.log("[UI-MANAGER] Start recording button clicked.");
                startRecording();
            } else {
                console.log("[UI-MANAGER] Stop recording button clicked.");
                stopRecording(true); // true to send end_of_stream
            }
        });
    } else {
        console.warn("[UI-MGR] Main recording button (#stopBtn) not found during setupEventListeners.");
    }

    // Clear chat button
    if (this.clearBtn) {
      this.clearBtn.addEventListener('click', async () => {
        try {
          const originalChatLog = this.chatLog;
          if (!originalChatLog) {
            console.error("[UI-MGR] ChatLog element not found for clearing.");
            return;
          }
          
          const newChatLog = document.createElement('div');
          newChatLog.id = 'chatLog';
          newChatLog.className = originalChatLog.className;
          
          originalChatLog.parentNode.replaceChild(newChatLog, originalChatLog);
          
          this.chatLog = newChatLog; // Update internal reference
          
          if (typeof refreshUIElements === 'function') refreshUIElements();
          
          if (this.status) this.status.textContent = 'Chat wird geleert...';

          console.log("[UI-MANAGER] Clearing chat, stopping all audio playback.");
          stopAllAudioPlayback();
          
          const response = await fetch('/api/clearChat', { 
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
          });
          
          if (response.ok) {
            const placeholderMessage = document.createElement('div');
            placeholderMessage.className = 'welcome-message';
            placeholderMessage.innerHTML = '<p>Chat wurde geleert. Beginnen Sie eine neue Konversation...</p>';
            this.chatLog.appendChild(placeholderMessage);
            
            setTimeout(() => {
              if (this.status) this.status.textContent = 'Chat erfolgreich geleert';
            }, 300);
            
            console.log("[UI-MANAGER] Chat history cleared. Restarting audio system.");
            setTimeout(() => {
              restartAudioSystemAndClearState();
            }, 500);
          } else {
            if (this.status) this.status.textContent = 'Frontend geleert, Backend-Fehler';
            console.error(`[UI-MANAGER] Failed to clear backend chat: ${response.status} ${response.statusText}`);
          }
        } catch (error) {
          if (this.status) this.status.textContent = 'Frontend geleert, Backend-Fehler';
          console.error(`[UI-MANAGER] Error clearing backend chat: ${error.message}`);
        }
      });
    } else {
        console.warn("[UI-MGR] Clear chat button (#clearBtn) not found during setupEventListeners.");
    }
    
    // Stop Audio Playback Button
    if (this.stopAudioPlaybackBtn) {
        this.stopAudioPlaybackBtn.addEventListener('click', () => {
            console.log("[UI-MANAGER] Stop all audio playback button clicked.");
            stopAllAudioPlayback();
            if (this.status) this.status.textContent = 'Audio Wiedergabe gestoppt.';
            // Optionally hide the button again if it's only shown during playback
            // this.stopAudioPlaybackBtn.style.display = 'none'; 
        });
    } else {
        console.warn("[UI-MGR] Stop Audio Playback button (#stopAudioPlaybackBtn) not found by ui-manager during setupEventListeners.");
    }

    // Restart Audio System Button
    if (this.restartAudioSysBtn) {
        this.restartAudioSysBtn.addEventListener('click', () => {
            console.log("[UI-MANAGER] Restart audio system button clicked.");
            if (this.status) this.status.textContent = 'Audio-System wird neu gestartet...';
            setTimeout(() => {
                restartAudioSystemAndClearState();
            }, 100);
        });
    } else {
        console.warn("[UI-MGR] Restart Audio System button (#restartAudioSysBtn) not found by ui-manager during setupEventListeners.");
    }

    // Debug button
    if (this.debugBtn && this.debugPanel && this.optimizationPanel && this.optimizationBtn) {
      this.debugBtn.addEventListener('click', () => {
        this.debugPanel.style.display = this.debugPanel.style.display === 'none' ? 'block' : 'none';
        this.debugBtn.textContent = this.debugPanel.style.display === 'none' ? 'Debug-Modus' : 'Debug ausblenden';
        
        if (this.debugPanel.style.display !== 'none') {
          this.optimizationPanel.style.display = 'none';
          this.optimizationBtn.textContent = 'Optimierungen';
        }
      });
    } else {
        console.warn("[UI-MGR] Debug button or its associated panels not found during setupEventListeners.");
    }
    
    // Optimization button
    if (this.optimizationBtn && this.optimizationPanel && this.debugPanel && this.debugBtn) {
      this.optimizationBtn.addEventListener('click', () => {
        this.optimizationPanel.style.display = this.optimizationPanel.style.display === 'none' ? 'block' : 'none';
        this.optimizationBtn.textContent = this.optimizationPanel.style.display === 'none' ? 'Optimierungen' : 'Optimierungen ausblenden';
        
        if (this.optimizationPanel.style.display !== 'none') {
          this.debugPanel.style.display = 'none';
          this.debugBtn.textContent = 'Debug-Modus';
          
          // Assuming optimizationManager is globally available or imported and initialized
          if (window.optimizationManager && typeof window.optimizationManager.updateOptimizationUIFromSettings === 'function') {
            window.optimizationManager.updateOptimizationUIFromSettings();
          }
        }
      });
    } else {
        console.warn("[UI-MGR] Optimization button or its associated panels not found during setupEventListeners.");
    }
  },
  createBotMessage: function(text, model, voice) {
    console.log(`[UI-MGR] Creating bot message: text="${text}", model="${model}", voice="${voice}"`);
    const chatLog = document.getElementById('chatLog');
    if (!chatLog) {
        console.error("[UI-MGR] Chat log element not found for createBotMessage!");
        return null;
    }

    // Remove placeholder if it exists
    const placeholder = chatLog.querySelector('.welcome-message');
    if (placeholder) {
        placeholder.remove();
    }

    const messageDiv = document.createElement('div');
    messageDiv.className = 'message bot-message';

    const headerDiv = document.createElement('div');
    headerDiv.className = 'message-header';
    headerDiv.textContent = `Bot (${model || 'default'} | ${voice || 'default'}):`;

    const contentP = document.createElement('p');
    contentP.className = 'message-content';
    contentP.textContent = text;    const latencySpan = document.createElement('div');
    latencySpan.className = 'message-latency';
    latencySpan.innerHTML = 
      '<span class="latency-label">Latenz:</span> ' +
      '<span class="latency-text-label">Text:</span> <span class="latency-text-value">berechne...</span> | ' +
      '<span class="latency-audio-label">Audio:</span> <span class="latency-audio-value">berechne...</span>';

    messageDiv.appendChild(headerDiv);
    messageDiv.appendChild(contentP);
    messageDiv.appendChild(latencySpan);    chatLog.appendChild(messageDiv);
    this.scrollToBottom();

    // Store reference to current bot message for streaming updates
    this.currentBotMessageDiv = messageDiv;

    // Return the created messageDiv so it can be referenced
    return messageDiv;
  },

  createUserMessage: function(text) {
    const chatLog = document.getElementById('chatLog');
    if (!chatLog) {
        console.error("[UI-MGR] Chat log element not found for createUserMessage!");
        return null;
    }

    // Remove placeholder if it exists
    const placeholder = chatLog.querySelector('.welcome-message');
    if (placeholder) {
        placeholder.remove();
    }

    const messageDiv = document.createElement('div');
    messageDiv.className = 'message user-message';

    const headerDiv = document.createElement('div');
    headerDiv.className = 'message-header';
    headerDiv.textContent = 'User:';

    const contentP = document.createElement('p');
    contentP.className = 'message-content';
    contentP.textContent = text;

    messageDiv.appendChild(headerDiv);
    messageDiv.appendChild(contentP);
    chatLog.appendChild(messageDiv);
    this.scrollToBottom();
    
    return messageDiv;
  },

  scrollToBottom: function() {
    if (this.chatLog) { // Check if chatLog was found during init
        this.chatLog.scrollTop = this.chatLog.scrollHeight;
    }
  },
  
  refreshUIElements: function() {
    // This function might need to re-select elements if they can be dynamically replaced,
    // but for now, it's mostly a placeholder.
    // If chatLog is replaced (e.g., in clearChat), it's already updated in this.chatLog.
    console.log("[UI-MGR] UI elements refreshed (if applicable).");
  },
  
  // Function for updating recognized text (interim speech recognition results)
  updateRecognizedText: function(text, isFinal = false) {
    if (!isFinal) {
      // For interim results, update status instead of creating messages
      if (this.status) {
        this.status.textContent = `Erkannt: ${text}`;
      }
    } else {
      // For final results, create a user message
      this.createUserMessage(text);
    }
  },
  
  // Function for updating latency information in bot messages
  updateMessageLatency: function(latencyInfo) {    if (!this.chatLog) return; // Check if chatLog was found
    // Find the most recent bot message and update its latency span
    const botMessages = document.querySelectorAll('.bot-message');
    if (botMessages.length > 0) {
      const lastBotMessage = botMessages[botMessages.length - 1];
      const latencySpan = lastBotMessage.querySelector('.message-latency');
      if (latencySpan && latencyInfo) {
        const { transcriptionTime, llmTime, totalTime } = latencyInfo;
        latencySpan.innerHTML = 
          '<span class="latency-label">Latenz:</span> ' +
          `<span class="latency-text-label">Text:</span> <span class="latency-text-value">${llmTime || '--'} ms</span> | ` +
          `<span class="latency-audio-label">Audio:</span> <span class="latency-audio-value">${transcriptionTime || '--'} ms</span>`;
      }
    }
  },
  
  // Function for updating latency display
  updateLatencyDisplay: function(latencyText) {
    // Update the most recent bot message latency
    const botMessages = document.querySelectorAll('.bot-message');
    if (botMessages.length > 0) {      const lastBotMessage = botMessages[botMessages.length - 1];
      const latencySpan = lastBotMessage.querySelector('.message-latency');
      if (latencySpan) {
        latencySpan.innerHTML = `<span class="latency-label">Latenz:</span> ${latencyText}`;
      }
    }
  },
  
  // This function is called by audio-system.js to update button states
  updateButtonStates: function(isRecording) {
    if (this.stopBtn) {
        this.stopBtn.textContent = isRecording ? 'Aufnahme stoppen' : 'Aufnahme starten';
        this.stopBtn.disabled = false; // Always enable after an attempt
    } else {
        console.warn("Main recording button (e.g., #stopBtn) not found for UI update.");
    }
    // You might want to disable/enable other buttons based on recording state here
  },

  showStatus: function(message, isError = false) {
    if (this.status) {
        this.status.textContent = message;
        this.status.className = isError ? 'status-error' : 'status-info';
        this.status.style.display = 'block';
    } else {
        console.warn("Status element not found for showing message:", message);
    }
  },

  hideStatus: function() {
    if (this.status) {
        this.status.style.display = 'none';
    } else {
        console.warn("Status element not found for hiding.");
    }
  },

  updateAudioVisualization: function(rmsValue) {
    const currentAudioLevel = document.getElementById('currentAudioLevel');
    const currentAudioValue = document.getElementById('currentAudioValue');
    if (currentAudioLevel && currentAudioValue) {
        const percentage = Math.min(100, (rmsValue * 700)); // Adjusted multiplier for sensitivity
        currentAudioLevel.style.width = percentage + '%';
        currentAudioValue.textContent = rmsValue.toFixed(4);
    }  },

  // Functions for handling messages, tokens, latency - called by audio-system.js
  // These should largely remain the same as they are UI manipulation logic.
  // Ensure currentBotMessageElement is handled correctly if it was a window global.
  // Let's assume currentBotMessageElement is a property of uiManager if it needs to persist across calls.
  // currentBotMessageElement: null, // Add to uiManager properties if needed
  
  appendTokenToBotMessage: function(token) {
    // Using traceLog for verbose token-level logging
    traceLog(`[UI-MGR] Appending token: "${token}", currentBotMessageDiv exists: ${!!this.currentBotMessageDiv}`);
    
    if (!this.currentBotMessageDiv) {
        console.warn('[UI-MGR] No current bot message div, creating one');
        // Model and voice might need to be passed or retrieved from optimizationManager/global state
        const currentModel = window.optimizationSettings?.chatModel || this.modelSel?.value || 'default';
        const currentVoice = window.optimizationSettings?.ttsVoice || this.voiceSel?.value || 'default';
        this.currentBotMessageDiv = this.createBotMessage('', currentModel, currentVoice);
    }
    const contentElement = this.currentBotMessageDiv.querySelector('.message-content');
    if (contentElement) {
        const oldText = contentElement.textContent;
        contentElement.textContent += token;
        // Using traceLog for verbose token-level logging
        traceLog(`[UI-MGR] Token appended. Old: "${oldText}", New: "${contentElement.textContent}"`);
        this.scrollToBottom();    } else {
        console.error('[UI-MGR] Could not find message-content element in currentBotMessageDiv');
    }
  },
  
  // Call this when the LLM reply is complete (e.g., on 'llm_reply' or 'done' event from WebSocket)
  finalizeBotMessage: function(fullText, performanceMetrics) {
    debugLog("[UI-MGR] finalizeBotMessage called with:", {fullText, performanceMetrics});
    debugLog("[UI-MGR] currentBotMessageDiv exists:", !!this.currentBotMessageDiv);
    
    if (this.currentBotMessageDiv) {
        if (fullText) { // If full text is provided, update it
             const contentElement = this.currentBotMessageDiv.querySelector('.message-content');
             if (contentElement) {
               debugLog("[UI-MGR] Updating full text from:", contentElement.textContent, "to:", fullText);
               contentElement.textContent = fullText;
             }
        }
        
        // Update latency information if performance metrics are provided
        if (performanceMetrics) {
            debugLog("[UI-MGR] Updating latency info with metrics:", performanceMetrics);
            this.updateLatencyInfo(this.currentBotMessageDiv, performanceMetrics);
        } else {
            console.warn("[UI-MGR] No performance metrics provided to finalizeBotMessage");
        }
    } else {
        console.warn("[UI-MGR] finalizeBotMessage called but no currentBotMessageDiv");
    }
    this.currentBotMessageDiv = null; // Reset for the next message
    this.scrollToBottom();
  },  // Helper function to update latency information in a message
  updateLatencyInfo: function(messageDiv, metrics) {
    debugLog("[UI-MGR] updateLatencyInfo called with metrics:", metrics);
    if (!messageDiv || !metrics) {
      console.warn("[UI-MGR] updateLatencyInfo: missing messageDiv or metrics");
      return;
    }
    
    const latencyElement = messageDiv.querySelector('.message-latency');
    if (!latencyElement) {
      console.warn("[UI-MGR] updateLatencyInfo: latency element not found in messageDiv");
      return;
    }
    
    // Extract latency values from metrics (adapt to backend format)
    const textLatency = metrics.transcription_latency_ms || metrics.text_latency_ms || 'N/A';
    const audioLatency = metrics.tts_latency_ms || metrics.audio_latency_ms || metrics.llm_latency_ms || 'N/A';
    const totalLatency = metrics.total_latency_ms || 'N/A';
    
    debugLog("[UI-MGR] Extracted latencies - text:", textLatency, "audio:", audioLatency, "total:", totalLatency);
    
    // Update the latency display
    latencyElement.innerHTML = 
      '<span class="latency-label">Latenz:</span> ' +
      `<span class="latency-text-label">Text:</span> <span class="latency-text-value">${textLatency}ms</span> | ` +
      `<span class="latency-audio-label">Audio:</span> <span class="latency-audio-value">${audioLatency}ms</span> | ` +
      `<span class="latency-total-label">Total:</span> <span class="latency-total-value">${totalLatency}ms</span>`;
    
    debugLog("[UI-MGR] Latency display updated:", latencyElement.innerHTML);
  },

  // Original updateBotMessage might be for non-streaming updates or final updates.
  // Let's keep it if it serves a different purpose or is called by other parts of the old system.
  // If it's purely for streaming, appendTokenToBotMessage and finalizeBotMessage are better.
  updateBotMessage: function(text, isFinal = false) {
    // This function might be redundant now with appendTokenToBotMessage and finalizeBotMessage
    // Consider if it's still needed or if its calls should be migrated.
    // For now, let's assume it might be used for a complete, non-streamed message.
    let botMessageElement = this.currentBotMessageDiv; // Or find the last one if not streaming
    if (!botMessageElement && isFinal) { // If final and no current streaming message, create one
        const currentModel = window.optimizationSettings?.chatModel || this.modelSel?.value || 'default';
        const currentVoice = window.optimizationSettings?.ttsVoice || this.voiceSel?.value || 'default';
        botMessageElement = this.createBotMessage(text, currentModel, currentVoice);
        this.currentBotMessageDiv = null; // As it's final
        return;
    }
    if (!botMessageElement) { // If not final and no current message, start one for streaming
        const currentModel = window.optimizationSettings?.chatModel || this.modelSel?.value || 'default';
        const currentVoice = window.optimizationSettings?.ttsVoice || this.voiceSel?.value || 'default';
        this.currentBotMessageDiv = this.createBotMessage('', currentModel, currentVoice);
        botMessageElement = this.currentBotMessageDiv;
    }
    
    const contentElement = botMessageElement.querySelector('.message-content');
    if (contentElement) {
      if (isFinal) {
        contentElement.textContent = text;
        this.currentBotMessageDiv = null; // Reset for next message
      } else {
        // If called with isFinal=false, it implies an update to the existing stream
        contentElement.textContent = text; // Or append: += text, depending on desired behavior
      }
    }
  }
};

// Make uiManager globally accessible if not an ES6 module itself yet.
// If main.js imports it as a module, this line is not strictly necessary for main.js
// but other non-module scripts might rely on it.
window.uiManager = uiManager;

// Expose methods for audio-system.js to call (if ui-manager is not a module that audio-system imports from)
// This is an alternative to audio-system directly calling window.uiManager.method()
// if we want to make ui-manager.js an ES6 module later and have explicit interface.
/*
export const updateButtonStates = uiManager.updateButtonStates.bind(uiManager);
export const showStatus = uiManager.showStatus.bind(uiManager);
export const hideStatus = uiManager.hideStatus.bind(uiManager);
export const updateAudioVisualization = uiManager.updateAudioVisualization.bind(uiManager);
export const appendTokenToBotMessage = uiManager.appendTokenToBotMessage.bind(uiManager);
export const finalizeBotMessage = uiManager.finalizeBotMessage.bind(uiManager);
export const updateRecognizedText = uiManager.updateRecognizedText.bind(uiManager);
export const updateLatencyDisplay = uiManager.updateLatencyDisplay.bind(uiManager);
*/
// Make sure loadModels and populateVoices are defined, possibly globally or imported
// For example, if they are in utils.js:
// import { loadModels, populateVoices } from './utils.js'; 
// Or if they are still global from a non-module script.

export { uiManager };