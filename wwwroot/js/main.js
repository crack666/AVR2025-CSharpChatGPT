// Main entry point for the application
document.addEventListener('DOMContentLoaded', function() {
  // Import and initialize modules
  // Initialize optimization settings before audio pipeline to avoid race conditions
  optimizationManager.init();
  audioSystem.init();
  uiManager.init();
  
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