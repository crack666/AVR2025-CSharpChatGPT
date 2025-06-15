// Optimization Manager
// No direct imports from audio-system.js needed here, as audio-system.js will call getters from this manager.

const optimizationManager = {
  // UI References (initialized in init)
  // ... (keep all existing UI element references like useProgressiveTTSCheckbox, etc.)

  // Default settings
  defaultSettings: {
    useProgressiveTTS: true,
    useTokenStreaming: true,
    disableVad: false,
    disableTts: false,
    useLegacyHttp: false, // This should probably be phased out or always false
    ttsMinFirstChunkLength: 50,
    ttsMaxFirstChunkLength: 100,
    ttsSubsequentChunkLength: 150,
    chatModel: "gpt-3.5-turbo", // Default model
    ttsVoice: "nova",           // Default voice

    // VAD Settings (can also be part of a nested object if preferred)
    threshold: 0.5, // Main VAD threshold
    silenceTimeoutSec: 1.0,
    minSpeechDurationSec: 0.2,
    startThreshold: 0.5, // Separate start/end thresholds if used by backend VAD
    endThreshold: 0.3,
    rmsSmoothingWindowSec: 0.1,
    hangoverDurationSec: 0.5,
    vadSpikeThreshold: 0.15,
    enableSpikeDetection: true,
    enableThirdPartyVad: true, // If this refers to a specific VAD implementation
    
    // WebRTC VAD specific (if used, otherwise can be removed or kept for future)
    useWebRTCVAD: false, 
    webRTCVADMode: 1, // Example: 0-3 for aggressiveness
    vadThreshold: 0.5, // This might be redundant with 'threshold' above, clarify which one is primary
    vadSilenceTimeout: 1.0, // Redundant with 'silenceTimeoutSec'
    vadStartThreshold: 0.5, // Redundant
    vadEndThreshold: 0.3,   // Redundant
    vadRmsSmoothingWindow: 0.1, // Redundant
    vadSpikeDetectionEnabled: true, // Redundant
    vadSpikeThresholdFactor: 1.5,
    vadSpikeMinDurationMs: 60,
    vadConsecutiveSpikeThreshold: 3,
    vadUseDynamicThreshold: false,
    vadDynamicThresholdFactor: 1.2,
    vadDynamicThresholdDecay: 0.995,
    vadMinRMSForDynamicThreshold: 0.01,


    latencyStats: { // Keep latencyStats structure
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
  },

  currentSettings: {}, // Holds the current operational settings

  init: function() {
    // Initialize UI references
    this.useProgressiveTTSCheckbox = document.getElementById('useProgressiveTTS');
    this.useTokenStreamingCheckbox = document.getElementById('useTokenStreaming');
    this.disableVadCheckbox = document.getElementById('disableVad');
    this.disableTtsCheckbox = document.getElementById('disableTts');
    this.useLegacyHttpCheckbox = document.getElementById('useLegacyHttp'); // Should be hidden/removed if legacy HTTP is gone
    this.ttsMinFirstChunkLengthInput = document.getElementById('ttsMinFirstChunkLength');
    this.ttsMaxFirstChunkLengthInput = document.getElementById('ttsMaxFirstChunkLength');
    this.ttsSubsequentChunkLengthInput = document.getElementById('ttsSubsequentChunkLength');

    this.applyOptimizationSettingsBtn = document.getElementById('applyOptimizationSettings');
    this.resetOptimizationSettingsBtn = document.getElementById('resetOptimizationSettings');
    this.resetLatencyStatsBtn = document.getElementById('resetLatencyStats');
    
    // VAD specific UI elements from debugPanel (or wherever they are now)
    // These are the VAD settings that were previously in audio-system.js init
    this.thresholdSlider = document.getElementById('thresholdSlider');
    this.thresholdValueDisplay = document.getElementById('thresholdValue');
    this.silenceTimeoutSlider = document.getElementById('silenceTimeoutSlider');
    this.silenceTimeoutValueDisplay = document.getElementById('silenceTimeoutValue');
    this.minSpeechDurationSlider = document.getElementById('minSpeechDurationSlider');
    this.minSpeechDurationValueDisplay = document.getElementById('minSpeechDurationValue');
    this.startThresholdSlider = document.getElementById('startThresholdSlider');
    this.startThresholdValueDisplay = document.getElementById('startThresholdValue');
    this.endThresholdSlider = document.getElementById('endThresholdSlider');
    this.endThresholdValueDisplay = document.getElementById('endThresholdValue');
    this.smoothingWindowSlider = document.getElementById('smoothingWindowSlider');
    this.smoothingWindowValueDisplay = document.getElementById('smoothingWindowValue');
    this.hangoverSlider = document.getElementById('hangoverSlider');
    this.hangoverValueDisplay = document.getElementById('hangoverValue');
    
    // VAD settings from the original optimization panel (if different from above)
    this.vadSpikeThresholdInput = document.getElementById('vadSpikeThreshold'); // This was in optimizationManager before
    this.vadSpikeThresholdValueDisplay = document.getElementById('vadSpikeThresholdValue');
    this.enableSpikeDetectionCheckbox = document.getElementById('enableSpikeDetection');
    this.enableThirdPartyVadCheckbox = document.getElementById('enableThirdPartyVad');
    
    this.applyVadSettingsBtn = document.getElementById('applyVadSettings'); // Button to apply VAD settings from debug panel

    // Load settings from localStorage or use defaults
    this.loadSettings(); 
    this.updateAllUIToReflectCurrentSettings();
    this.setupEventListeners();
    console.log("[OPTIMIZATION-MGR] Initialized with settings:", JSON.parse(JSON.stringify(this.currentSettings)));
  },
  
  loadSettings: function() {
    let storedSettings = null;
    try {
      storedSettings = localStorage.getItem('optimizationSettings');
      if (storedSettings) {
        const parsed = JSON.parse(storedSettings);
        // Deep merge parsed settings with defaults to ensure all keys exist
        this.currentSettings = this.deepMerge(JSON.parse(JSON.stringify(this.defaultSettings)), parsed);
      } else {
        this.currentSettings = JSON.parse(JSON.stringify(this.defaultSettings)); // Deep copy
      }
    } catch (e) {
      console.error('[OPTIMIZATION-MGR] Error loading settings from localStorage, using defaults:', e);
      this.currentSettings = JSON.parse(JSON.stringify(this.defaultSettings)); // Deep copy
    }
    // Ensure latencyStats is always an object
    if (!this.currentSettings.latencyStats || typeof this.currentSettings.latencyStats !== 'object') {
        this.currentSettings.latencyStats = JSON.parse(JSON.stringify(this.defaultSettings.latencyStats));
    }
  },

  saveSettings: function() {
    try {
      localStorage.setItem('optimizationSettings', JSON.stringify(this.currentSettings));
      console.log("[OPTIMIZATION-MGR] Settings saved to localStorage.");
    } catch (e) {
      console.error('[OPTIMIZATION-MGR] Error saving settings to localStorage:', e);
    }
  },

  deepMerge: function(target, source) {
    for (const key in source) {
        if (source.hasOwnProperty(key)) {
            if (source[key] instanceof Object && source[key] !== null && !Array.isArray(source[key])) {
                if (!target[key]) Object.assign(target, { [key]: {} });
                this.deepMerge(target[key], source[key]);
            } else {
                Object.assign(target, { [key]: source[key] });
            }
        }
    }
    return target;
  },
  
  setupEventListeners: function() {
    // --- Event Listeners for Pipeline Optimization Panel ---
    if (this.applyOptimizationSettingsBtn) {
        this.applyOptimizationSettingsBtn.addEventListener('click', () => {
            console.log("[OPTIMIZATION-MGR] Apply Pipeline Settings button clicked.");
            this.updateCurrentSettingsFromPipelineUI();
            this.saveSettings();
            // Notify audio system to re-evaluate connection if necessary (e.g., if model/voice changed)
            // This could be via a custom event or a direct call if audio-system exposes a method
            if (window.audioSystem && typeof window.audioSystem.handleSettingsChange === 'function') {
                window.audioSystem.handleSettingsChange({ type: 'pipeline' });
            }
            alert("Pipeline settings applied and saved. May require audio restart if WebSocket parameters changed.");
        });
    }

    if (this.resetOptimizationSettingsBtn) {
        this.resetOptimizationSettingsBtn.addEventListener('click', () => {
            console.log("[OPTIMIZATION-MGR] Reset Pipeline Settings button clicked.");
            // Reset only the pipeline part of currentSettings to defaults
            const pipelineDefaults = (({ useProgressiveTTS, useTokenStreaming, disableVad, disableTts, useLegacyHttp, ttsMinFirstChunkLength, ttsMaxFirstChunkLength, ttsSubsequentChunkLength, chatModel, ttsVoice }) => 
                                    ({ useProgressiveTTS, useTokenStreaming, disableVad, disableTts, useLegacyHttp, ttsMinFirstChunkLength, ttsMaxFirstChunkLength, ttsSubsequentChunkLength, chatModel, ttsVoice }))(this.defaultSettings);
            for (const key in pipelineDefaults) {
                this.currentSettings[key] = pipelineDefaults[key];
            }
            this.updatePipelineOptimizationUI();
            this.saveSettings();
            alert("Pipeline settings reset to defaults. Saved.");
        });
    }

    // Individual pipeline controls update currentSettings directly (no save until "Apply")
    const pipelineControls = [
        { el: this.useProgressiveTTSCheckbox, key: 'useProgressiveTTS', type: 'checked' },
        { el: this.useTokenStreamingCheckbox, key: 'useTokenStreaming', type: 'checked' },
        { el: this.disableVadCheckbox, key: 'disableVad', type: 'checked' },
        { el: this.disableTtsCheckbox, key: 'disableTts', type: 'checked' },
        { el: this.useLegacyHttpCheckbox, key: 'useLegacyHttp', type: 'checked' },
        { el: this.ttsMinFirstChunkLengthInput, key: 'ttsMinFirstChunkLength', type: 'valueInt' },
        { el: this.ttsMaxFirstChunkLengthInput, key: 'ttsMaxFirstChunkLength', type: 'valueInt' },
        { el: this.ttsSubsequentChunkLengthInput, key: 'ttsSubsequentChunkLength', type: 'valueInt' }
    ];
    pipelineControls.forEach(ctrl => {
        if (ctrl.el) {
            ctrl.el.addEventListener('input', (e) => { // 'input' for text, 'change' for checkbox
                this.currentSettings[ctrl.key] = ctrl.type === 'checked' ? e.target.checked : parseInt(e.target.value, 10);
                // No save here, only on "Apply"
            });
        }
    });

    // --- Event Listeners for VAD Settings Panel (formerly in audio-system.js) ---
    const vadSliders = [
        { slider: this.thresholdSlider, valueDisplay: this.thresholdValueDisplay, key: 'threshold', type: 'float' },
        { slider: this.silenceTimeoutSlider, valueDisplay: this.silenceTimeoutValueDisplay, key: 'silenceTimeoutSec', type: 'float' },
        { slider: this.minSpeechDurationSlider, valueDisplay: this.minSpeechDurationValueDisplay, key: 'minSpeechDurationSec', type: 'float' },
        { slider: this.startThresholdSlider, valueDisplay: this.startThresholdValueDisplay, key: 'startThreshold', type: 'float' },
        { slider: this.endThresholdSlider, valueDisplay: this.endThresholdValueDisplay, key: 'endThreshold', type: 'float' },
        { slider: this.smoothingWindowSlider, valueDisplay: this.smoothingWindowValueDisplay, key: 'rmsSmoothingWindowSec', type: 'float' },
        { slider: this.hangoverSlider, valueDisplay: this.hangoverValueDisplay, key: 'hangoverDurationSec', type: 'float' },
        { slider: this.vadSpikeThresholdInput, valueDisplay: this.vadSpikeThresholdValueDisplay, key: 'vadSpikeThreshold', type: 'float' } // This was an input, not slider
    ];

    vadSliders.forEach(item => {
        if (item.slider) {
            item.slider.addEventListener('input', (e) => {
                const value = item.type === 'float' ? parseFloat(e.target.value) : parseInt(e.target.value, 10);
                this.currentSettings[item.key] = value;
                if (item.valueDisplay) item.valueDisplay.textContent = value.toFixed(item.type === 'float' ? (item.key === 'vadSpikeThreshold' ? 2 : 1) : 0);
                 // No save here, only on "Apply VAD Settings"
            });
        }
    });
    
    // VAD Checkboxes
    const vadCheckboxes = [
        { el: this.enableSpikeDetectionCheckbox, key: 'enableSpikeDetection' },
        { el: this.enableThirdPartyVadCheckbox, key: 'enableThirdPartyVad' }
    ];
    vadCheckboxes.forEach(ctrl => {
        if (ctrl.el) {
            ctrl.el.addEventListener('change', (e) => {
                this.currentSettings[ctrl.key] = e.target.checked;
                 // No save here, only on "Apply VAD Settings"
            });
        }
    });

    if (this.applyVadSettingsBtn) {
        this.applyVadSettingsBtn.addEventListener('click', () => {
            console.log("[OPTIMIZATION-MGR] Apply VAD Settings button clicked.");
            // No need to call updateCurrentSettingsFromVadUI if individual listeners are already updating currentSettings
            this.saveSettings();
            // Notify audio system that VAD settings have changed
            if (window.audioSystem && typeof window.audioSystem.handleSettingsChange === 'function') {
                window.audioSystem.handleSettingsChange({ type: 'vad' });
            }
            alert("VAD settings applied and saved. Will be used on next audio segment or WebSocket (re)connection.");
        });
    }
    
    // Latency Stats Reset Button
    if (this.resetLatencyStatsBtn) {
        this.resetLatencyStatsBtn.addEventListener('click', () => {
            console.log("[OPTIMIZATION-MGR] Reset Latency Stats button clicked.");
            this.currentSettings.latencyStats = JSON.parse(JSON.stringify(this.defaultSettings.latencyStats));
            this.updateLatencyStatsUI(); // This function would update the debugPanel latency display
            this.saveSettings();
            alert("Latency stats reset and saved.");
        });
    }
  },

  // --- UI Update Functions ---
  updateAllUIToReflectCurrentSettings: function() {
    this.updatePipelineOptimizationUI();
    this.updateVadSettingsUI();
    this.updateLatencyStatsUI();
  },

  updatePipelineOptimizationUI: function() {
    if (this.useProgressiveTTSCheckbox) this.useProgressiveTTSCheckbox.checked = this.currentSettings.useProgressiveTTS;
    if (this.useTokenStreamingCheckbox) this.useTokenStreamingCheckbox.checked = this.currentSettings.useTokenStreaming;
    if (this.disableVadCheckbox) this.disableVadCheckbox.checked = this.currentSettings.disableVad;
    if (this.disableTtsCheckbox) this.disableTtsCheckbox.checked = this.currentSettings.disableTts;
    if (this.useLegacyHttpCheckbox) this.useLegacyHttpCheckbox.checked = this.currentSettings.useLegacyHttp;
    if (this.ttsMinFirstChunkLengthInput) this.ttsMinFirstChunkLengthInput.value = this.currentSettings.ttsMinFirstChunkLength;
    if (this.ttsMaxFirstChunkLengthInput) this.ttsMaxFirstChunkLengthInput.value = this.currentSettings.ttsMaxFirstChunkLength;
    if (this.ttsSubsequentChunkLengthInput) this.ttsSubsequentChunkLengthInput.value = this.currentSettings.ttsSubsequentChunkLength;
    // Update model and voice dropdowns if they are part of this panel
    const modelSel = document.getElementById('model');
    if (modelSel) modelSel.value = this.currentSettings.chatModel;
    const voiceSel = document.getElementById('voice');
    if (voiceSel) voiceSel.value = this.currentSettings.ttsVoice;
  },

  updateVadSettingsUI: function() {
    const vadSlidersMap = [
        { slider: this.thresholdSlider, valueDisplay: this.thresholdValueDisplay, key: 'threshold', type: 'float' },
        { slider: this.silenceTimeoutSlider, valueDisplay: this.silenceTimeoutValueDisplay, key: 'silenceTimeoutSec', type: 'float' },
        { slider: this.minSpeechDurationSlider, valueDisplay: this.minSpeechDurationValueDisplay, key: 'minSpeechDurationSec', type: 'float' },
        { slider: this.startThresholdSlider, valueDisplay: this.startThresholdValueDisplay, key: 'startThreshold', type: 'float' },
        { slider: this.endThresholdSlider, valueDisplay: this.endThresholdValueDisplay, key: 'endThreshold', type: 'float' },
        { slider: this.smoothingWindowSlider, valueDisplay: this.smoothingWindowValueDisplay, key: 'rmsSmoothingWindowSec', type: 'float' },
        { slider: this.hangoverSlider, valueDisplay: this.hangoverValueDisplay, key: 'hangoverDurationSec', type: 'float' },
        { slider: this.vadSpikeThresholdInput, valueDisplay: this.vadSpikeThresholdValueDisplay, key: 'vadSpikeThreshold', type: 'float' }
    ];
    vadSlidersMap.forEach(item => {
        if (item.slider) item.slider.value = this.currentSettings[item.key];
        if (item.valueDisplay) item.valueDisplay.textContent = parseFloat(this.currentSettings[item.key]).toFixed(item.key === 'vadSpikeThreshold' ? 2 : 1);
    });

    if (this.enableSpikeDetectionCheckbox) this.enableSpikeDetectionCheckbox.checked = this.currentSettings.enableSpikeDetection;
    if (this.enableThirdPartyVadCheckbox) this.enableThirdPartyVadCheckbox.checked = this.currentSettings.enableThirdPartyVad;
  },
  
  updateLatencyStatsUI: function() {
    // This function would populate the latency statistics in the debugPanel
    // Example:
    // const avgTextLatencyEl = document.getElementById('avgTextLatency');
    // if (avgTextLatencyEl && this.currentSettings.latencyStats.textLatency.length > 0) {
    //   const avg = this.currentSettings.latencyStats.textLatency.reduce((a, b) => a + b, 0) / this.currentSettings.latencyStats.textLatency.length;
    //   avgTextLatencyEl.textContent = `${avg.toFixed(0)} ms`;
    // }
    console.log("[OPTIMIZATION-MGR] Latency stats UI update requested (implementation pending).");
  },

  // --- Settings Update from UI (called by "Apply" buttons) ---
  updateCurrentSettingsFromPipelineUI: function() {
    if (this.useProgressiveTTSCheckbox) this.currentSettings.useProgressiveTTS = this.useProgressiveTTSCheckbox.checked;
    if (this.useTokenStreamingCheckbox) this.currentSettings.useTokenStreaming = this.useTokenStreamingCheckbox.checked;
    if (this.disableVadCheckbox) this.currentSettings.disableVad = this.disableVadCheckbox.checked;
    if (this.disableTtsCheckbox) this.currentSettings.disableTts = this.disableTtsCheckbox.checked;
    if (this.useLegacyHttpCheckbox) this.currentSettings.useLegacyHttp = this.useLegacyHttpCheckbox.checked;
    if (this.ttsMinFirstChunkLengthInput) this.currentSettings.ttsMinFirstChunkLength = parseInt(this.ttsMinFirstChunkLengthInput.value, 10) || this.defaultSettings.ttsMinFirstChunkLength;
    if (this.ttsMaxFirstChunkLengthInput) this.currentSettings.ttsMaxFirstChunkLength = parseInt(this.ttsMaxFirstChunkLengthInput.value, 10) || this.defaultSettings.ttsMaxFirstChunkLength;
    if (this.ttsSubsequentChunkLengthInput) this.currentSettings.ttsSubsequentChunkLength = parseInt(this.ttsSubsequentChunkLengthInput.value, 10) || this.defaultSettings.ttsSubsequentChunkLength;
    
    const modelSel = document.getElementById('model');
    if (modelSel) this.currentSettings.chatModel = modelSel.value;
    const voiceSel = document.getElementById('voice');
    if (voiceSel) this.currentSettings.ttsVoice = voiceSel.value;
  },

  // updateCurrentSettingsFromVadUI: function() { // Not strictly needed if individual listeners update currentSettings
  //   // This would read all VAD sliders/checkboxes and update currentSettings
  //   // Example:
  //   // if (this.thresholdSlider) this.currentSettings.threshold = parseFloat(this.thresholdSlider.value);
  //   // ... for all VAD controls
  // },

  // --- Getters for audio-system.js ---
  getCurrentPipelineOptions: function() {
    // These are the options relevant for the WebSocket query string or initial setup messages
    return {
      Language: document.getElementById('language')?.value || 'en', // Assuming language is still a top-level selector
      ChatModel: this.currentSettings.chatModel,
      TtsVoice: this.currentSettings.ttsVoice,
      DisableVad: this.currentSettings.disableVad,
      DisableTts: this.currentSettings.disableTts,
      DisableProgressiveTts: !this.currentSettings.useProgressiveTTS,
      TtsMinFirstChunkLength: this.currentSettings.ttsMinFirstChunkLength,
      TtsMaxFirstChunkLength: this.currentSettings.ttsMaxFirstChunkLength,
      TtsSubsequentChunkLength: this.currentSettings.ttsSubsequentChunkLength,
      // Add any other pipeline-related settings the server expects
    };
  },

  getCurrentVadSettings: function() {
    // These are the VAD settings the server/audio-system might need
    // Consolidate VAD settings here. Choose primary names if there were redundancies.
    return {
      Threshold: this.currentSettings.threshold,
      SilenceTimeoutSec: this.currentSettings.silenceTimeoutSec,
      MinSpeechDurationSec: this.currentSettings.minSpeechDurationSec,
      StartThreshold: this.currentSettings.startThreshold,
      EndThreshold: this.currentSettings.endThreshold,
      RmsSmoothingWindowSec: this.currentSettings.rmsSmoothingWindowSec,
      HangoverDurationSec: this.currentSettings.hangoverDurationSec,
      
      VadSpikeThreshold: this.currentSettings.vadSpikeThreshold, // From original optimization panel
      EnableSpikeDetection: this.currentSettings.enableSpikeDetection, // From original optimization panel
      EnableThirdPartyVad: this.currentSettings.enableThirdPartyVad, // From original optimization panel

      // Include WebRTC VAD specific settings if they are actively used and sent to server
      UseWebRTCVAD: this.currentSettings.useWebRTCVAD,
      WebRTCVADMode: this.currentSettings.webRTCVADMode,
      // ... other WebRTC VAD params if needed by backend
    };
  },
  
  // --- Latency Tracking (if still managed here) ---
  trackLatency: function(point) {
    if (!this.currentSettings.latencyStats) this.resetLatencyStats(); // Ensure structure exists
    this.currentSettings.latencyStats[point] = Date.now();

    // Example calculations (can be expanded)
    if (point === 'transcriptionReceived' && this.currentSettings.latencyStats.recordingStart) {
        const lat = this.currentSettings.latencyStats.transcriptionReceived - this.currentSettings.latencyStats.recordingStart;
        this.currentSettings.latencyStats.recordingToTranscriptLatency.push(lat);
    }
    // Add other latency calculations as points are hit
    this.updateLatencyStatsUI(); // Update display
    // this.saveSettings(); // Optionally save stats frequently, or only with other settings
  },
  
  updateSettingsFromServer: function(serverSettings) {
    console.log("[OPTIMIZATION-MGR] Received settings update from server:", serverSettings);
    // Carefully merge server settings into currentSettings
    // This is important if the server can override client-side preferences
    for (const key in serverSettings) {
        if (this.currentSettings.hasOwnProperty(key) && typeof this.currentSettings[key] !== 'object') { // Avoid overwriting nested objects like latencyStats directly
            this.currentSettings[key] = serverSettings[key];
        } else if (this.currentSettings.hasOwnProperty(key) && typeof this.currentSettings[key] === 'object' && this.currentSettings[key] !== null) {
            // For nested objects, merge them (e.g., if server sends partial VAD settings)
            this.deepMerge(this.currentSettings[key], serverSettings[key]);
        }
    }
    this.updateAllUIToReflectCurrentSettings();
    this.saveSettings(); // Save the server-updated settings
    console.log("[OPTIMIZATION-MGR] Applied and saved server settings. Current settings:", JSON.parse(JSON.stringify(this.currentSettings)));
  }
};

// Make optimizationManager globally accessible
// If main.js imports it as a module, this line is not strictly necessary for main.js
// but audio-system.js might rely on it if it doesn't import optimizationManager.
window.optimizationManager = optimizationManager;

// Example of how audio-system.js would get settings:
// const pipelineOpts = window.optimizationManager.getCurrentPipelineOptions();
// const vadSettings = window.optimizationManager.getCurrentVadSettings();