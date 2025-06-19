// Import necessary functions from audio-system.js
import { initAudioSystem } from './audio-system.js';
// Import uiManager
import { uiManager } from './ui-manager.js';
// Import optimizationManager if it becomes a module
// import { optimizationManager } from './optimization-manager.js';

// Main entry point for the application
document.addEventListener('DOMContentLoaded', function() {
  // Initialize optimization manager FIRST (before audio system needs it)
  if (window.optimizationManager && typeof window.optimizationManager.init === 'function') {
    window.optimizationManager.init();
    console.log("[MAIN] Optimization manager initialized");
  } else {
    console.warn("Optimization manager not found. VAD/Pipeline sliders might not work.");
  }

  // Initialize UI Manager using the imported module
  uiManager.init();
  console.log("[MAIN] UI manager initialized");
  
  // Initialize the audio system LAST (needs optimizationManager to be ready)
  initAudioSystem();
  console.log("[MAIN] Audio system initialized");
});