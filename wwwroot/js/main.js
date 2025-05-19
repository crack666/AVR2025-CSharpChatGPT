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
  
  // Display ready status
  status.textContent = 'Bereit';
});