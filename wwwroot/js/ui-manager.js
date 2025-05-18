// UI Manager
const uiManager = {
  init: function() {
    // Initialize values
    thresholdValue.textContent = silenceThreshold.toFixed(3);
    
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
    
    // Sync range and number input for silence seconds
    silenceSecRange.addEventListener('input', () => {
      silenceSecInput.value = silenceSecRange.value;
    });
    
    silenceSecInput.addEventListener('input', () => {
      silenceSecRange.value = silenceSecInput.value;
    });
    
    // Update threshold value on slider change
    silenceThresholdRange.addEventListener('input', () => {
      silenceThreshold = parseFloat(silenceThresholdRange.value);
      thresholdValue.textContent = silenceThreshold.toFixed(3);
      
      // Update threshold line in visualization
      if (document.getElementById('thresholdLine')) {
        document.getElementById('thresholdLine').style.top = `-${Math.min(silenceThreshold * 1000, 100)}%`;
      }
    });
    
    // Add button to set threshold to recommended value
    const useRecommendedBtn = document.createElement('button');
    useRecommendedBtn.textContent = 'Empfohlenen Schwellwert verwenden';
    useRecommendedBtn.className = 'secondary';
    useRecommendedBtn.style.marginLeft = '15px';
    useRecommendedBtn.addEventListener('click', () => {
      const recommendedValue = parseFloat(document.getElementById('recommendedThreshold').textContent || '0.02');
      silenceThresholdRange.value = recommendedValue;
      silenceThreshold = recommendedValue;
      thresholdValue.textContent = recommendedValue.toFixed(3);
      
      // Update threshold line
      if (document.getElementById('thresholdLine')) {
        document.getElementById('thresholdLine').style.top = `-${Math.min(silenceThreshold * 1000, 100)}%`;
      }
    });
    
    // Add button after the debug button
    document.querySelector('.button-group').appendChild(useRecommendedBtn);
  }
};