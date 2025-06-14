// Optimization Manager
const optimizationManager = {
  // Optimization settings references
  useProgressiveTTSCheckbox: null,
  useTokenStreamingCheckbox: null,
  disableVadCheckbox: null,
  disableTtsCheckbox: null, // Added
  useLegacyHttpCheckbox: null,
  ttsMinFirstChunkLengthInput: null, // Added
  ttsMaxFirstChunkLengthInput: null, // Added
  ttsSubsequentChunkLengthInput: null, // Added
  vadSpikeThresholdInput: null, // Added
  enableSpikeDetectionCheckbox: null, // Added
  enableThirdPartyVadCheckbox: null, // Added
  applyVadSettingsBtn: null, // Added
  // vadSampleInput: null, // Kept for now, might be removed if not used
  // calibrateVadBtn: null, // Kept for now
  applyOptimizationSettingsBtn: null,
  resetOptimizationSettingsBtn: null,
  resetLatencyStatsBtn: null,
  
  init: function() {
    // Initialize UI references
    this.useProgressiveTTSCheckbox = document.getElementById('useProgressiveTTS');
    this.useTokenStreamingCheckbox = document.getElementById('useTokenStreaming');
    this.disableVadCheckbox = document.getElementById('disableVad');
    this.disableTtsCheckbox = document.getElementById('disableTts'); // Added
    this.useLegacyHttpCheckbox = document.getElementById('useLegacyHttp');
    this.ttsMinFirstChunkLengthInput = document.getElementById('ttsMinFirstChunkLength'); // Added
    this.ttsMaxFirstChunkLengthInput = document.getElementById('ttsMaxFirstChunkLength'); // Added
    this.ttsSubsequentChunkLengthInput = document.getElementById('ttsSubsequentChunkLength'); // Added

    this.applyOptimizationSettingsBtn = document.getElementById('applyOptimizationSettings');
    this.resetOptimizationSettingsBtn = document.getElementById('resetOptimizationSettings');
    this.resetLatencyStatsBtn = document.getElementById('resetLatencyStats');
    
    // VAD specific UI elements from debugPanel
    this.vadSpikeThresholdInput = document.getElementById('vadSpikeThreshold');
    this.enableSpikeDetectionCheckbox = document.getElementById('enableSpikeDetection');
    this.enableThirdPartyVadCheckbox = document.getElementById('enableThirdPartyVad');
    this.applyVadSettingsBtn = document.getElementById('applyVadSettings');

    // VAD calibration input and button (still here, review if needed)
    this.vadSampleInput = document.getElementById('vadSampleInput');
    this.calibrateVadBtn = document.getElementById('calibrateVadBtn');
    
    // Create optimization settings object
    window.optimizationSettings = {
      // Pipeline Options (controlled by optimizationPanel)
      useProgressiveTTS: true,
      useTokenStreaming: true,
      disableVad: false,
      disableTts: false, // Added
      useLegacyHttp: false,
      ttsMinFirstChunkLength: 50, // Added
      ttsMaxFirstChunkLength: 100, // Added
      ttsSubsequentChunkLength: 150, // Added
      
      // VAD Settings (controlled by debugPanel, but stored here for consistency)
      vadSpikeThreshold: 0.15, // Added
      enableSpikeDetection: true, // Added
      enableThirdPartyVad: true, // Added

      latencyStats: {
        recordingStart: 0,
        transcriptionReceived: 0,
        llmResponseStart: 0,
        ttsStart: 0,
        ttsEnd: 0,
        
        recordingToTranscriptLatency: [],
        transcriptToLLMLatency: [],
        llmToTTSLatency: [],
        textLatency: [],
        audioLatency: [],
        totalLatency: []
      }
    };
    
    // Load optimization settings from server or localStorage
    try {
      const storedSettings = localStorage.getItem('optimizationSettings');
      if (storedSettings) {
        const parsed = JSON.parse(storedSettings);
        // Merge carefully to avoid breaking with new/old properties
        for (const key in window.optimizationSettings) {
          if (parsed.hasOwnProperty(key) && key !== 'latencyStats') { // Don't overwrite latencyStats structure directly
            if (typeof window.optimizationSettings[key] === 'object' && window.optimizationSettings[key] !== null) {
              // For nested objects like latencyStats, merge their properties
              for (const subKey in window.optimizationSettings[key]) {
                if (parsed[key].hasOwnProperty(subKey)) {
                  window.optimizationSettings[key][subKey] = parsed[key][subKey];
                }
              }
            } else {
              window.optimizationSettings[key] = parsed[key];
            }
          }
        }
      }
    } catch (e) {
      console.error('Error loading optimization settings from localStorage:', e);
    }
    this.updateOptimizationUIFromSettings(); // Ensure UI reflects loaded or default settings
    this.updateVadSettingsUIFromState(); // Ensure VAD UI reflects loaded or default settings

    this.setupEventListeners();
  },
  
  // Helper function to reset/initialize all latency stats
  resetLatencyStats: function() {
    if (!window.optimizationSettings.latencyStats) {
      window.optimizationSettings.latencyStats = {};
    }
    
    window.optimizationSettings.latencyStats.recordingToTranscriptLatency = [];
    window.optimizationSettings.latencyStats.transcriptToLLMLatency = [];
    window.optimizationSettings.latencyStats.llmToTTSLatency = [];
    window.optimizationSettings.latencyStats.textLatency = [];
    window.optimizationSettings.latencyStats.audioLatency = [];
    window.optimizationSettings.latencyStats.totalLatency = [];
    
    window.optimizationSettings.latencyStats.recordingStart = 0;
    window.optimizationSettings.latencyStats.transcriptionReceived = 0;
    window.optimizationSettings.latencyStats.llmResponseStart = 0;
    window.optimizationSettings.latencyStats.ttsStart = 0;
    window.optimizationSettings.latencyStats.ttsEnd = 0;
  },
  
  setupEventListeners: function() {
    // VAD calibration button handler
    if (this.calibrateVadBtn) { // Check if element exists
        this.calibrateVadBtn.addEventListener('click', async () => { console.warn('VAD Calibration button clicked - no backend endpoint defined yet.'); });
    }

    // Apply VAD Settings Button
    if (this.applyVadSettingsBtn) { // Check if element exists
        this.applyVadSettingsBtn.addEventListener('click', () => {
            this.updateVadStateFromUI();
            this.saveSettings(); // Save to localStorage
            if (window.wsAudioSocket && window.wsAudioSocket.readyState === WebSocket.OPEN) {
                const vadSettingsPayload = {
                    VadSpikeThreshold: window.optimizationSettings.vadSpikeThreshold,
                    EnableSpikeDetection: window.optimizationSettings.enableSpikeDetection,
                    EnableThirdPartyVad: window.optimizationSettings.enableThirdPartyVad
                    // Include other VAD settings from the UI if they are meant to be sent here
                };
                // Corrected: Changed 'data' to 'payload' to match backend expectation
                window.wsAudioSocket.send(JSON.stringify({ type: 'updateVadSettings', payload: vadSettingsPayload }));
                debugLog('Sent VAD settings update to backend.');
            } else {
                debugLog('WebSocket not connected. VAD settings saved locally, will be applied on next connection/segment.');
            }
        });
    }

    // Apply Pipeline/Optimization Settings Button
    if (this.applyOptimizationSettingsBtn) { // Check if element exists
        this.applyOptimizationSettingsBtn.addEventListener('click', () => {
            this.updatePipelineStateFromUI();
            this.saveSettings(); // Save to localStorage
            if (window.wsAudioSocket && window.wsAudioSocket.readyState === WebSocket.OPEN) {
                const pipelineOptionsPayload = {
                    DisableVad: window.optimizationSettings.disableVad,
                    DisableTts: window.optimizationSettings.disableTts,
                    DisableProgressiveTts: !window.optimizationSettings.useProgressiveTTS, 
                    TtsMinFirstChunkLength: window.optimizationSettings.ttsMinFirstChunkLength,
                    TtsMaxFirstChunkLength: window.optimizationSettings.ttsMaxFirstChunkLength,
                    TtsSubsequentChunkLength: window.optimizationSettings.ttsSubsequentChunkLength,
                    ChatModel: document.getElementById('model') ? document.getElementById('model').value : "gpt-3.5-turbo",
                    TtsVoice: document.getElementById('voice') ? document.getElementById('voice').value : "nova"
                };
                // Corrected: Changed 'data' to 'payload' to match backend expectation
                window.wsAudioSocket.send(JSON.stringify({ type: 'updatePipelineOptions', payload: pipelineOptionsPayload }));
                debugLog('Sent Pipeline options update to backend.');
            } else {
                debugLog('WebSocket not connected. Pipeline settings saved locally.');
            }
        });
    }

    // Reset Optimization/Pipeline Settings Button
    if (this.resetOptimizationSettingsBtn) { // Check if element exists
        this.resetOptimizationSettingsBtn.addEventListener('click', () => {
            // Reset to default values (hardcoded or from a default config object)
            window.optimizationSettings.useProgressiveTTS = true;
            window.optimizationSettings.useTokenStreaming = true;
            window.optimizationSettings.disableVad = false;
            window.optimizationSettings.disableTts = false;
            window.optimizationSettings.useLegacyHttp = false;
            window.optimizationSettings.ttsMinFirstChunkLength = 50;
            window.optimizationSettings.ttsMaxFirstChunkLength = 100;
            window.optimizationSettings.ttsSubsequentChunkLength = 150;
            this.updateOptimizationUIFromSettings();
            this.saveSettings();
            debugLog('Pipeline settings reset to defaults.');
        });
    }

    // Reset Latency Stats Button
    if (this.resetLatencyStatsBtn) { // Check if element exists
        this.resetLatencyStatsBtn.addEventListener('click', () => {
            this.resetLatencyStats();
            this.updateLatencyStatsUI();
            this.saveSettings(); // Save reset stats state
            debugLog('Latency stats reset.');
        });
    }

    // Add listeners for individual controls to update window.optimizationSettings dynamically (optional, good for real-time reflection)
    // Example for one checkbox:
    if (this.useProgressiveTTSCheckbox) {
        this.useProgressiveTTSCheckbox.addEventListener('change', (e) => {
            window.optimizationSettings.useProgressiveTTS = e.target.checked;
            // this.saveSettings(); // Optionally save on every individual change
        });
    }
    // Repeat for other checkboxes and inputs in optimizationPanel
    if (this.useTokenStreamingCheckbox) this.useTokenStreamingCheckbox.addEventListener('change', (e) => window.optimizationSettings.useTokenStreaming = e.target.checked);
    if (this.disableVadCheckbox) this.disableVadCheckbox.addEventListener('change', (e) => window.optimizationSettings.disableVad = e.target.checked);
    if (this.disableTtsCheckbox) this.disableTtsCheckbox.addEventListener('change', (e) => window.optimizationSettings.disableTts = e.target.checked);
    if (this.useLegacyHttpCheckbox) this.useLegacyHttpCheckbox.addEventListener('change', (e) => window.optimizationSettings.useLegacyHttp = e.target.checked);
    if (this.ttsMinFirstChunkLengthInput) this.ttsMinFirstChunkLengthInput.addEventListener('input', (e) => window.optimizationSettings.ttsMinFirstChunkLength = parseInt(e.target.value, 10) || 0);
    if (this.ttsMaxFirstChunkLengthInput) this.ttsMaxFirstChunkLengthInput.addEventListener('input', (e) => window.optimizationSettings.ttsMaxFirstChunkLength = parseInt(e.target.value, 10) || 0);
    if (this.ttsSubsequentChunkLengthInput) this.ttsSubsequentChunkLengthInput.addEventListener('input', (e) => window.optimizationSettings.ttsSubsequentChunkLength = parseInt(e.target.value, 10) || 0);

    // Listeners for VAD settings in debugPanel
    if (this.vadSpikeThresholdInput) {
        this.vadSpikeThresholdInput.addEventListener('input', (e) => {
            window.optimizationSettings.vadSpikeThreshold = parseFloat(e.target.value) || 0;
            const span = document.getElementById('vadSpikeThresholdValue');
            if(span) span.textContent = e.target.value;
        });
    }
    if (this.enableSpikeDetectionCheckbox) this.enableSpikeDetectionCheckbox.addEventListener('change', (e) => window.optimizationSettings.enableSpikeDetection = e.target.checked);
    if (this.enableThirdPartyVadCheckbox) this.enableThirdPartyVadCheckbox.addEventListener('change', (e) => window.optimizationSettings.enableThirdPartyVad = e.target.checked);

  },
  updateOptimizationUIFromSettings: function() {
    if (this.useProgressiveTTSCheckbox) this.useProgressiveTTSCheckbox.checked = window.optimizationSettings.useProgressiveTTS;
    if (this.useTokenStreamingCheckbox) this.useTokenStreamingCheckbox.checked = window.optimizationSettings.useTokenStreaming;
    if (this.disableVadCheckbox) this.disableVadCheckbox.checked = window.optimizationSettings.disableVad;
    if (this.disableTtsCheckbox) this.disableTtsCheckbox.checked = window.optimizationSettings.disableTts;
    if (this.useLegacyHttpCheckbox) this.useLegacyHttpCheckbox.checked = window.optimizationSettings.useLegacyHttp;
    if (this.ttsMinFirstChunkLengthInput) this.ttsMinFirstChunkLengthInput.value = window.optimizationSettings.ttsMinFirstChunkLength;
    if (this.ttsMaxFirstChunkLengthInput) this.ttsMaxFirstChunkLengthInput.value = window.optimizationSettings.ttsMaxFirstChunkLength;
    if (this.ttsSubsequentChunkLengthInput) this.ttsSubsequentChunkLengthInput.value = window.optimizationSettings.ttsSubsequentChunkLength;
  },

  updateVadSettingsUIFromState: function() { // New function for VAD panel
    if (this.vadSpikeThresholdInput) {
        this.vadSpikeThresholdInput.value = window.optimizationSettings.vadSpikeThreshold;
        const span = document.getElementById('vadSpikeThresholdValue');
        if(span) span.textContent = window.optimizationSettings.vadSpikeThreshold;
    }
    if (this.enableSpikeDetectionCheckbox) this.enableSpikeDetectionCheckbox.checked = window.optimizationSettings.enableSpikeDetection;
    if (this.enableThirdPartyVadCheckbox) this.enableThirdPartyVadCheckbox.checked = window.optimizationSettings.enableThirdPartyVad;
  },

  updatePipelineStateFromUI: function() { // Reads from Optimization Panel
    if (this.useProgressiveTTSCheckbox) window.optimizationSettings.useProgressiveTTS = this.useProgressiveTTSCheckbox.checked;
    if (this.useTokenStreamingCheckbox) window.optimizationSettings.useTokenStreaming = this.useTokenStreamingCheckbox.checked;
    if (this.disableVadCheckbox) window.optimizationSettings.disableVad = this.disableVadCheckbox.checked;
    if (this.disableTtsCheckbox) window.optimizationSettings.disableTts = this.disableTtsCheckbox.checked;
    if (this.useLegacyHttpCheckbox) window.optimizationSettings.useLegacyHttp = this.useLegacyHttpCheckbox.checked;
    if (this.ttsMinFirstChunkLengthInput) window.optimizationSettings.ttsMinFirstChunkLength = parseInt(this.ttsMinFirstChunkLengthInput.value, 10) || 0;
    if (this.ttsMaxFirstChunkLengthInput) window.optimizationSettings.ttsMaxFirstChunkLength = parseInt(this.ttsMaxFirstChunkLengthInput.value, 10) || 0;
    if (this.ttsSubsequentChunkLengthInput) window.optimizationSettings.ttsSubsequentChunkLength = parseInt(this.ttsSubsequentChunkLengthInput.value, 10) || 0;
  },

  updateVadStateFromUI: function() { // Reads from VAD Debug Panel
    if (this.vadSpikeThresholdInput) window.optimizationSettings.vadSpikeThreshold = parseFloat(this.vadSpikeThresholdInput.value) || 0;
    if (this.enableSpikeDetectionCheckbox) window.optimizationSettings.enableSpikeDetection = this.enableSpikeDetectionCheckbox.checked;
    if (this.enableThirdPartyVadCheckbox) window.optimizationSettings.enableThirdPartyVad = this.enableThirdPartyVadCheckbox.checked;
  },

  saveSettings: function() {
    try {
      localStorage.setItem('optimizationSettings', JSON.stringify(window.optimizationSettings));
    } catch (e) {
      console.error('Error saving optimization settings to localStorage:', e);
    }
  }
};

// Ensure optimizationManager is globally accessible
window.optimizationManager = optimizationManager;