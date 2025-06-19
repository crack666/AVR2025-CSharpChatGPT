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
    console.warn("Optimization manager not found. VAD/Pipeline sliders might not work.");
  }

  // Initialize UI Manager using the imported module
  uiManager.init();
  
  // Initialize the audio system
  initAudioSystem();
});