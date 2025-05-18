// Main entry point for the application
document.addEventListener('DOMContentLoaded', function() {
  // Import and initialize modules
  audioSystem.init();
  uiManager.init();
  optimizationManager.init();
  
  // Initial data loading
  loadModels();
  populateVoices();
  
  // Display ready status
  status.textContent = 'Bereit';
});