// Import necessary functions from audio-system.js
import { initAudioSystem } from './audio-system.js';
// Import uiManager
import { uiManager } from './ui-manager.js';
// Import optimizationManager if it becomes a module
// import { optimizationManager } from './optimization-manager.js';

// Main entry point for the application
document.addEventListener('DOMContentLoaded', function() {
  // Initialize optimization manager (from optimization-manager.js)
  // If optimizationManager is refactored to an ES6 module, import and call its init method.
  if (window.optimizationManager && typeof window.optimizationManager.init === 'function') {
    window.optimizationManager.init();
  } else {
    console.error("Optimization manager not found or init function missing. VAD/Pipeline sliders might not work.");
  }

  // Initialize UI Manager using the imported module
  uiManager.init();
  
  // Initialize Audio System using the imported function
  initAudioSystem().then(() => {
    console.log("Audio system initialized successfully via import.");
  }).catch(error => {
    console.error("Failed to initialize audio system via import:", error);
  });
  
  // Initial data loading - these might need to be moved into uiManager.init or called after it
  // if they depend on UI elements uiManager initializes.
  // For now, assuming loadModels and populateVoices are global or correctly handled.
  if (typeof loadModels === 'function') loadModels(); else console.warn('loadModels function not found globally.');
  if (typeof populateVoices === 'function') populateVoices(); else console.warn('populateVoices function not found globally.');
  
  // Load persisted chat history from backend
  (async () => {
    try {
      const resp = await fetch('/api/chatLog');
      if (resp.ok) {
        const logs = await resp.json();
        logs.forEach(msg => {
          if (msg.role === 0) { // User message
            uiManager.createUserMessage(msg.content);
          } else { // Bot message
            uiManager.createBotMessage(msg.content, msg.model || undefined, msg.voice || undefined);
          }
        });
      }
    } catch (err) {
      console.error('Error loading chat history:', err);
    }
  })();
  
  // Display ready status - uiManager should handle this if 'status' is one of its elements
  const statusElement = document.getElementById('status'); // Or uiManager.status if already queried
  if (statusElement) {
    statusElement.textContent = 'Bereit';
  } else {
    console.error("Status element with ID 'status' not found in main.js after DOMContentLoaded.");
  }
});