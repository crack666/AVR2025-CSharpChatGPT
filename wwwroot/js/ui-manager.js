// UI Manager
const uiManager = {
  init: function() {
    // Setup event listeners
    this.setupEventListeners();
    
    // Load model select
    const loadPromise = loadModels();
    const currentModelEl = document.getElementById('currentModel');
    modelSel.addEventListener('change', () => { 
      if (currentModelEl) currentModelEl.textContent = modelSel.value; 
    });
    loadPromise.then(() => { 
      if (currentModelEl) currentModelEl.textContent = modelSel.value; 
    });
    
    // Initialize speech synthesis voices
    speechSynthesis.onvoiceschanged = populateVoices;
    populateVoices();
  },
  
  setupEventListeners: function() {
    // Clear chat button
    clearBtn.addEventListener('click', async () => {
      try {
        // Save reference to the original chatLog element
        const originalChatLog = document.getElementById('chatLog');
        
        // Create a completely new chatLog element
        const newChatLog = document.createElement('div');
        newChatLog.id = 'chatLog';
        newChatLog.className = originalChatLog.className;
        
        // Replace the old chatLog with the new one
        originalChatLog.parentNode.replaceChild(newChatLog, originalChatLog);
        
        // Update all global references to UI elements
        window.chatLog = newChatLog;
        window.refreshUIElements();
        
        // Update status
        window.status.textContent = 'Chat wird geleert...';

        // Reset audio playback system
        if (window.audioSystem && typeof window.audioSystem.resetAudioPlayback === 'function') {
          debugLog("Calling audioSystem.resetAudioPlayback().");
          window.audioSystem.resetAudioPlayback();
        }
        
        // Call API to clear backend chat history
        const response = await fetch('/api/clearChat', { 
          method: 'POST',
          headers: { 'Content-Type': 'application/json' }
        });
        
        if (response.ok) {
          // Reset global state
          window.currentLatencyElem = null;
          window.allAudioElements = [];
          window.chunks = [];
          
          // Close any open EventSource connections
          if (window.eventSource) {
            window.eventSource.close();
            window.eventSource = null;
          }
          
          // Make sure we're not blocking audio processing
          window.isProcessingOrPlayingAudio = false;
          window.speakingSegment = false;
          window.silenceStart = null;
          
          // Add a welcome message to the new chat
          const placeholderMessage = document.createElement('div');
          placeholderMessage.className = 'welcome-message';
          placeholderMessage.innerHTML = '<p>Chat wurde geleert. Beginnen Sie eine neue Konversation...</p>';
          newChatLog.appendChild(placeholderMessage);
          
          // Set a delayed ready status
          setTimeout(() => {
            window.status.textContent = 'Chat erfolgreich geleert';
          }, 300);
          
          debugLog("Chat history cleared in both frontend and backend");
          
          // Restart the audio capture system to make sure everything is fresh
          setTimeout(() => {
            audioSystem.restartAudioCapture();
          }, 500);
        } else {
          window.status.textContent = 'Frontend geleert, Backend-Fehler';
          debugLog(`Failed to clear backend chat: ${response.status} ${response.statusText}`);
        }
      } catch (error) {
        window.status.textContent = 'Frontend geleert, Backend-Fehler';
        debugLog(`Error clearing backend chat: ${error.message}`);
      }
    });
    
    // Debug button
    debugBtn.addEventListener('click', () => {
      debugPanel.style.display = debugPanel.style.display === 'none' ? 'block' : 'none';
      debugBtn.textContent = debugPanel.style.display === 'none' ? 'Debug-Modus' : 'Debug ausblenden';
      
      // Hide optimization panel if showing debug panel
      if (debugPanel.style.display !== 'none') {
        optimizationPanel.style.display = 'none';
        optimizationBtn.textContent = 'Optimierungen';
        
        // Reset noise statistics when enabling debug panel
        noiseValues = [];
        maxNoiseLevel = 0;
        averageNoiseLevel = 0;
      }
    });
    
    // Optimization button
    optimizationBtn.addEventListener('click', () => {
      optimizationPanel.style.display = optimizationPanel.style.display === 'none' ? 'block' : 'none';
      optimizationBtn.textContent = optimizationPanel.style.display === 'none' ? 'Optimierungen' : 'Optimierungen ausblenden';
      
      // Hide debug panel if showing optimization panel
      if (optimizationPanel.style.display !== 'none') {
        debugPanel.style.display = 'none';
        debugBtn.textContent = 'Debug-Modus';
        
        // Update UI with current settings
        optimizationManager.updateOptimizationUIFromSettings();
      }
    });
  },

  createBotMessage: function(text, model, voice) {
    const chatLog = document.getElementById('chatLog');
    if (!chatLog) {
        console.error("Chat log element not found!");
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
    contentP.textContent = text;

    const latencySpan = document.createElement('span');
    latencySpan.className = 'latency-info';
    latencySpan.innerHTML = 
      '<span class="latency-label">Latenz:</span> ' +
      '<span class="latency-text-label">Text:</span> <span class="latency-text-value">--</span> | ' +
      '<span class="latency-audio-label">Audio:</span> <span class="latency-audio-value">--</span>';

    messageDiv.appendChild(headerDiv);
    messageDiv.appendChild(contentP);
    messageDiv.appendChild(latencySpan);
    chatLog.appendChild(messageDiv);
    this.scrollToBottom();

    // Return the created messageDiv so it can be referenced
    return messageDiv; 
  },

  createUserMessage: function(text) {
    const chatLog = document.getElementById('chatLog');
    if (!chatLog) {
        console.error("Chat log element not found!");
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
    const chatLog = document.getElementById('chatLog');
    if (chatLog) {
        chatLog.scrollTop = chatLog.scrollHeight;
    }
  },
  
  refreshUIElements: function() {
    console.log("UI elements refreshed (if applicable).");
  },
  
  // Function for updating recognized text (interim speech recognition results)
  updateRecognizedText: function(text, isFinal = false) {
    if (!isFinal) {
      // For interim results, update status instead of creating messages
      if (window.status) {
        window.status.textContent = `Erkannt: ${text}`;
      }
    } else {
      // For final results, create a user message
      this.createUserMessage(text);
    }
  },
  
  // Function for updating latency information in bot messages
  updateMessageLatency: function(latencyInfo) {
    // Find the most recent bot message and update its latency span
    const botMessages = document.querySelectorAll('.bot-message');
    if (botMessages.length > 0) {
      const lastBotMessage = botMessages[botMessages.length - 1];
      const latencySpan = lastBotMessage.querySelector('.latency-info');
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
    if (botMessages.length > 0) {
      const lastBotMessage = botMessages[botMessages.length - 1];
      const latencySpan = lastBotMessage.querySelector('.latency-info');
      if (latencySpan) {
        latencySpan.innerHTML = `<span class="latency-label">Latenz:</span> ${latencyText}`;
      }
    }
  },
  
  // Function for updating bot message content
  updateBotMessage: function(text, isFinal = false) {
    // Find or create the current bot message
    let botMessageElement = window.currentBotMessageElement;
    if (!botMessageElement) {
      // Create a new bot message and store the DOM element directly
      botMessageElement = this.createBotMessage('');
      window.currentBotMessageElement = botMessageElement;
    }
    
    // Update the content - botMessageElement is now a DOM element
    const contentElement = botMessageElement.querySelector('.message-content');
    if (contentElement) {
      if (isFinal) {
        contentElement.textContent = text;
        window.currentBotMessageElement = null; // Reset for next message
      } else {
        contentElement.textContent = text;
      }
    }
  }
};

// Ensure uiManager is globally accessible
window.uiManager = uiManager;