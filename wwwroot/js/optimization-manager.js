// Optimization Manager
const optimizationManager = {
  // Optimization settings references
  useProgressiveTTSCheckbox: null,
  useTokenStreamingCheckbox: null,
  disableVadCheckbox: null,
  useLegacyHttpCheckbox: null,
  vadSampleInput: null,
  calibrateVadBtn: null,
  applyOptimizationSettingsBtn: null,
  resetOptimizationSettingsBtn: null,
  resetLatencyStatsBtn: null,
  
  init: function() {
    // Initialize UI references
    this.useProgressiveTTSCheckbox = document.getElementById('useProgressiveTTS');
    this.useTokenStreamingCheckbox = document.getElementById('useTokenStreaming');
    // Removed: useChunkBasedAudioCheckbox not used
    // Removed: useEarlyAudioProcessingCheckbox not used
    // Removed: useCachedAudioContextCheckbox not used
    // Removed: useSmartChunkSplittingCheckbox not used
    this.disableVadCheckbox = document.getElementById('disableVad');
    this.useLegacyHttpCheckbox = document.getElementById('useLegacyHttp');
    // Removed: ttsDynamicChunkSizeSlider not used
    // Removed: ttsDynamicChunkSizeValue not used
    this.applyOptimizationSettingsBtn = document.getElementById('applyOptimizationSettings');
    this.resetOptimizationSettingsBtn = document.getElementById('resetOptimizationSettings');
    this.resetLatencyStatsBtn = document.getElementById('resetLatencyStats');
    // VAD calibration input and button
    this.vadSampleInput = document.getElementById('vadSampleInput');
    this.calibrateVadBtn = document.getElementById('calibrateVadBtn');
    
    // Create optimization settings object
    window.optimizationSettings = {
      useProgressiveTTS: true,
      useTokenStreaming: true,
      // Core pipeline flags
      disableVad: false,
      useLegacyHttp: false,
      
      // Latency tracking
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
      // Try fetch pipeline options from server
      fetch('/api/settings/pipeline')
        .then(res => res.json())
        .then(dto => {
          // Map server flags to UI settings
          window.optimizationSettings.useProgressiveTTS = !dto.DisableProgressiveTts;
          window.optimizationSettings.useTokenStreaming = !dto.DisableTokenStreaming;
          window.optimizationSettings.disableVad = dto.DisableVad;
          window.optimizationSettings.useLegacyHttp = dto.UseLegacyHttp;
          // Model and voice
          modelSel.value = dto.ChatModel;
          voiceSel.value = dto.TtsVoice;
          // Update local storage and UI
          localStorage.setItem('optimizationSettings', JSON.stringify(window.optimizationSettings));
          this.updateOptimizationUIFromSettings();
        })
        .catch(_ => {
          // Fallback to localStorage
          const savedSettings = localStorage.getItem('optimizationSettings');
          if (savedSettings) {
            const parsed = JSON.parse(savedSettings);
            window.optimizationSettings = {...window.optimizationSettings, ...parsed};
            this.updateOptimizationUIFromSettings();
          }
        });
        
        // Ensure new arrays exist (backwards compatibility)
        if (!window.optimizationSettings.latencyStats.textLatency) {
          window.optimizationSettings.latencyStats.textLatency = [];
        }
        if (!window.optimizationSettings.latencyStats.audioLatency) {
          window.optimizationSettings.latencyStats.audioLatency = [];
        }
        if (!window.optimizationSettings.latencyStats.totalLatency) {
          window.optimizationSettings.latencyStats.totalLatency = [];
        }
        
        this.updateOptimizationUIFromSettings();
    } catch (e) {
      console.error("Error loading saved optimization settings:", e);
    }
    
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
    this.calibrateVadBtn.addEventListener('click', async () => {
      if (!this.vadSampleInput.files.length) {
        alert('Bitte eine Audiodatei zum Kalibrieren auswählen.');
        return;
      }
      const file = this.vadSampleInput.files[0];
      const form = new FormData();
      form.append('file', file, file.name);
      status.textContent = 'VAD kalibrieren...';
      try {
        const resp = await fetch('/api/settings/vad/calibrate', { method: 'POST', body: form });
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        const settings = await resp.json();
        // Update sliders
        document.getElementById('thresholdSlider').value = settings.StartThreshold;
        document.getElementById('startThresholdSlider').value = settings.StartThreshold;
        document.getElementById('endThresholdSlider').value = settings.EndThreshold;
        document.getElementById('smoothingWindowSlider').value = settings.RmsSmoothingWindowSec;
        document.getElementById('hangoverSlider').value = settings.HangoverDurationSec;
        // Reflect values
        document.getElementById('thresholdValue').textContent = settings.StartThreshold;
        document.getElementById('startThresholdValue').textContent = settings.StartThreshold;
        document.getElementById('endThresholdValue').textContent = settings.EndThreshold;
        document.getElementById('smoothingWindowValue').textContent = settings.RmsSmoothingWindowSec;
        document.getElementById('hangoverValue').textContent = settings.HangoverDurationSec;
        status.textContent = 'VAD-Kalibrierung abgeschlossen';
      } catch (err) {
        console.error('VAD calibration error', err);
        status.textContent = 'Fehler bei VAD-Kalibrierung';
      }
    });
    
    // Apply optimization settings button
    this.applyOptimizationSettingsBtn.addEventListener('click', () => {
      // Capture old settings to detect changes requiring restart
      const oldUseCachedAudioContext = window.optimizationSettings.useCachedAudioContext;
      // Update settings from UI
      window.optimizationSettings.useProgressiveTTS = this.useProgressiveTTSCheckbox.checked;
      window.optimizationSettings.useTokenStreaming = this.useTokenStreamingCheckbox.checked;
      window.optimizationSettings.disableVad = this.disableVadCheckbox.checked;
      window.optimizationSettings.useLegacyHttp = this.useLegacyHttpCheckbox.checked;
      
      // Apply settings to dropdown
      if (!window.optimizationSettings.useProgressiveTTS && !window.optimizationSettings.useTokenStreaming) {
        optimizationMode.value = 'none';
      } else if (window.optimizationSettings.useProgressiveTTS && window.optimizationSettings.useTokenStreaming) {
        optimizationMode.value = 'advanced';
      } else {
        optimizationMode.value = 'progressive';
      }
      
      // Save settings to localStorage for persistence
      localStorage.setItem('optimizationSettings', JSON.stringify(window.optimizationSettings));
      // Push pipeline options to server SettingsController
      const pipelineDto = {
        UseLegacyHttp: window.optimizationSettings.useLegacyHttp,
        DisableVad: window.optimizationSettings.disableVad,
        DisableTokenStreaming: !window.optimizationSettings.useTokenStreaming,
        DisableProgressiveTts: !window.optimizationSettings.useProgressiveTTS,
        ChatModel: modelSel.value,
        TtsVoice: voiceSel.value
      };
      fetch('/api/settings/pipeline', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(pipelineDto)
      }).catch(err => console.error('Error updating pipeline settings:', err));
      
      debugLog("Optimierungseinstellungen angewendet");
      status.textContent = 'Optimierungseinstellungen angewendet';
      
      // Some settings might require a restart
      // Audio system restart may be required
      debugLog("Optimierungseinstellungen angewendet - Änderungen können einen Neustart des Audio-Systems erfordern");
      alert("Bitte starten Sie das Audio-System neu, um sicherzustellen, dass alle Änderungen übernommen werden.");
    });
    
    // Reset optimization settings button
    this.resetOptimizationSettingsBtn.addEventListener('click', () => {
      // Reset to defaults
      window.optimizationSettings.useProgressiveTTS = true;
      window.optimizationSettings.useTokenStreaming = true;
      window.optimizationSettings.disableVad = false;
      window.optimizationSettings.useLegacyHttp = false;
      
      // Update UI
      this.updateOptimizationUIFromSettings();
      
      // Save to localStorage
      localStorage.setItem('optimizationSettings', JSON.stringify(window.optimizationSettings));
      
      debugLog("Optimierungseinstellungen zurückgesetzt");
      status.textContent = 'Optimierungseinstellungen zurückgesetzt';
    });
    
    // Reset latency stats button
    this.resetLatencyStatsBtn.addEventListener('click', () => {
      this.resetLatencyStats();
      
      // Update UI
      this.updateLatencyStatsUI();
      
      debugLog("Latenz-Statistiken zurückgesetzt");
    });
    
    // Optimization dropdown change handler
    optimizationMode.addEventListener('change', () => {
      switch (optimizationMode.value) {
        case 'none':
          window.optimizationSettings.useProgressiveTTS = false;
          window.optimizationSettings.useTokenStreaming = false;
          break;
        case 'progressive':
          window.optimizationSettings.useProgressiveTTS = true;
          window.optimizationSettings.useTokenStreaming = false;
          break;
        case 'advanced':
          window.optimizationSettings.useProgressiveTTS = true;
          window.optimizationSettings.useTokenStreaming = true;
          break;
      }
      
      // Update UI if panel is visible
      if (optimizationPanel.style.display !== 'none') {
        this.updateOptimizationUIFromSettings();
      }
      
      // Save settings
      localStorage.setItem('optimizationSettings', JSON.stringify(window.optimizationSettings));
      
      debugLog(`Optimierungsmodus geändert zu: ${optimizationMode.value}`);
      status.textContent = `Optimierungsmodus: ${optimizationMode.value}`;
    });
  },
  
  // Function to update optimization UI from settings
  updateOptimizationUIFromSettings: function() {
    if (!this.useProgressiveTTSCheckbox) return; // Not initialized yet
    
    this.useProgressiveTTSCheckbox.checked = window.optimizationSettings.useProgressiveTTS;
    this.useTokenStreamingCheckbox.checked = window.optimizationSettings.useTokenStreaming;
    this.disableVadCheckbox.checked = window.optimizationSettings.disableVad;
    this.useLegacyHttpCheckbox.checked = window.optimizationSettings.useLegacyHttp;
    
    // Update latency stats
    this.updateLatencyStatsUI();
  },
  
  // Function to update latency stats UI
  updateLatencyStatsUI: function() {
    const stats = window.optimizationSettings.latencyStats;
    
    const getAverage = (arr) => arr.length > 0 ? Math.round(arr.reduce((a, b) => a + b, 0) / arr.length) : '-';
    
    document.getElementById('recordingToTranscriptLatency').textContent = getAverage(stats.recordingToTranscriptLatency);
    document.getElementById('transcriptToLLMLatency').textContent = getAverage(stats.transcriptToLLMLatency);
    document.getElementById('llmToTTSLatency').textContent = getAverage(stats.llmToTTSLatency);
    document.getElementById('textLatency').textContent = getAverage(stats.textLatency);
    document.getElementById('audioLatency').textContent = getAverage(stats.audioLatency);
  },
  
  // Function to track latency at different stages
  trackLatency: function(stage, latencyElement = null) {
    if (window.optimizationSettings === undefined)
    {
      console.log('track latency skipped.')
      return;
    }
    const now = Date.now();
    const stats = window.optimizationSettings.latencyStats;
    let totalLatencyValue = 0;
    
    switch(stage) {
      case 'recordingStart':
        stats.recordingStart = now;
        break;
      case 'transcriptionReceived':
        stats.transcriptionReceived = now;
        if (stats.recordingStart > 0) {
          if (!stats.recordingToTranscriptLatency) stats.recordingToTranscriptLatency = [];
          stats.recordingToTranscriptLatency.push(now - stats.recordingStart);
          if (stats.recordingToTranscriptLatency.length > 10) stats.recordingToTranscriptLatency.shift();
        }
        break;
      case 'llmResponseStart':
        stats.llmResponseStart = now;
        if (stats.transcriptionReceived > 0) {
          // Time until first token (chat text start)
          const delta = now - stats.transcriptionReceived;
          if (!stats.transcriptToLLMLatency) stats.transcriptToLLMLatency = [];
          stats.transcriptToLLMLatency.push(delta);
          if (stats.transcriptToLLMLatency.length > 10) stats.transcriptToLLMLatency.shift();
          // Register as text latency (time until first text displayed)
          if (!stats.textLatency) stats.textLatency = [];
          stats.textLatency.push(delta);
          if (stats.textLatency.length > 10) stats.textLatency.shift();
        }
        break;
      case 'ttsStart':
        stats.ttsStart = now;
        if (stats.llmResponseStart > 0) {
          if (!stats.llmToTTSLatency) stats.llmToTTSLatency = [];
          stats.llmToTTSLatency.push(now - stats.llmResponseStart);
          if (stats.llmToTTSLatency.length > 10) stats.llmToTTSLatency.shift();
        }
        break;
      case 'ttsEnd':
        stats.ttsEnd = now;
        if (stats.recordingStart > 0) {
          totalLatencyValue = now - stats.recordingStart;
          if (!stats.totalLatency) stats.totalLatency = [];
          stats.totalLatency.push(totalLatencyValue);
          if (stats.totalLatency.length > 10) stats.totalLatency.shift();
          
          // We no longer update the UI here because we now update it earlier when audio begins playing
        }
        break;
    }
    
    // Always update latency stats UI
    this.updateLatencyStatsUI();
  }
};