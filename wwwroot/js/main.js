// Main entry point for the application
document.addEventListener('DOMContentLoaded', function() {
  // Import and initialize modules
  // Initialize optimization settings before audio pipeline to avoid race conditions

  // Initialize optimization manager (from optimization-manager.js)
  if (window.optimizationManager && typeof window.optimizationManager.init === 'function') {
    window.optimizationManager.init();
  } else {
    console.error("Optimization manager not found or init function missing. VAD/Pipeline sliders might not work.");
  }

  // Initialize UI Manager before Audio System to ensure uiManager is available
  if (window.uiManager && typeof window.uiManager.init === 'function') {
    window.uiManager.init();
  } else {
    console.error("UI manager not found or init function missing.");
  }
  
  // Initialize Audio System
  if (window.audioSystem && typeof window.audioSystem.init === 'function') {
    window.audioSystem.init();
  } else {
    console.error("Audio system not found or init function missing.");
  }
  
  // Initial data loading
  loadModels();
  populateVoices();
  // Load persisted chat history from backend
  (async () => {
    try {
      // Load persisted chat history
      const resp = await fetch('/api/chatLog');
      if (resp.ok) {
        const logs = await resp.json();
        logs.forEach(msg => {
          // msg.role: 0 = User, 1 = Bot
          if (msg.role === 0) {
            createUserMessage(msg.content);
          } else {
            // include model and voice metadata in bot header
            createBotMessage(msg.content, msg.model || undefined, msg.voice || undefined);
          }
        });
      }
    } catch (err) {
      console.error('Error loading chat history:', err);
    }
  })();
  
  // Display ready status
  status.textContent = 'Bereit';
});