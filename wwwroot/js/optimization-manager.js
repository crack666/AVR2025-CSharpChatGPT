// Optimization Manager
const optimizationManager = {
  // Optimization settings references
  useProgressiveTTSCheckbox: null,
  useTokenStreamingCheckbox: null,
  useChunkBasedAudioCheckbox: null,
  useEarlyAudioProcessingCheckbox: null,
  useCachedAudioContextCheckbox: null,
  useSmartChunkSplittingCheckbox: null,
  ttsDynamicChunkSizeSlider: null,
  ttsDynamicChunkSizeValue: null,
  applyOptimizationSettingsBtn: null,
  resetOptimizationSettingsBtn: null,
  resetLatencyStatsBtn: null,
  
  init: function() {
    // Initialize UI references
    this.useProgressiveTTSCheckbox = document.getElementById('useProgressiveTTS');
    this.useTokenStreamingCheckbox = document.getElementById('useTokenStreaming');
    this.useChunkBasedAudioCheckbox = document.getElementById('useChunkBasedAudio');
    this.useEarlyAudioProcessingCheckbox = document.getElementById('useEarlyAudioProcessing');
    this.useCachedAudioContextCheckbox = document.getElementById('useCachedAudioContext');
    this.useSmartChunkSplittingCheckbox = document.getElementById('useSmartChunkSplitting');
    this.disableVadCheckbox = document.getElementById('disableVad');
    this.useLegacyHttpCheckbox = document.getElementById('useLegacyHttp');
    this.ttsDynamicChunkSizeSlider = document.getElementById('ttsDynamicChunkSize');
    this.ttsDynamicChunkSizeValue = document.getElementById('ttsDynamicChunkSizeValue');
    this.applyOptimizationSettingsBtn = document.getElementById('applyOptimizationSettings');
    this.resetOptimizationSettingsBtn = document.getElementById('resetOptimizationSettings');
    this.resetLatencyStatsBtn = document.getElementById('resetLatencyStats');
    
    // Create optimization settings object
    window.optimizationSettings = {
      useProgressiveTTS: true,
      useTokenStreaming: true,
      useChunkBasedAudio: true,
      useEarlyAudioProcessing: false,
      useCachedAudioContext: false,
      useSmartChunkSplitting: true,
      // New pipeline flags
      disableVad: false,
      useLegacyHttp: false,
      ttsDynamicChunkSize: 100,
      
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
          window.optimizationSettings.useChunkBasedAudio = !dto.UseLegacyHttp;
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
    // TTS chunk size slider
    this.ttsDynamicChunkSizeSlider.addEventListener('input', () => {
      this.ttsDynamicChunkSizeValue.textContent = this.ttsDynamicChunkSizeSlider.value;
    });
    
    // Apply optimization settings button
    this.applyOptimizationSettingsBtn.addEventListener('click', () => {
      // Capture old settings to detect changes requiring restart
      const oldUseCachedAudioContext = window.optimizationSettings.useCachedAudioContext;
      // Update settings from UI
      window.optimizationSettings.useProgressiveTTS = this.useProgressiveTTSCheckbox.checked;
      window.optimizationSettings.useTokenStreaming = this.useTokenStreamingCheckbox.checked;
      window.optimizationSettings.useChunkBasedAudio = this.useChunkBasedAudioCheckbox.checked;
      window.optimizationSettings.disableVad = this.disableVadCheckbox.checked;
      window.optimizationSettings.useLegacyHttp = this.useLegacyHttpCheckbox.checked;
      window.optimizationSettings.useEarlyAudioProcessing = this.useEarlyAudioProcessingCheckbox.checked;
      window.optimizationSettings.useCachedAudioContext = this.useCachedAudioContextCheckbox.checked;
      window.optimizationSettings.useSmartChunkSplitting = this.useSmartChunkSplittingCheckbox.checked;
      window.optimizationSettings.ttsDynamicChunkSize = parseInt(this.ttsDynamicChunkSizeSlider.value);
      
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
        DisableProgressiveTts: !window.optimizationSettings.useProgressiveTTS
      };
      fetch('/api/settings/pipeline', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(pipelineDto)
      }).catch(err => console.error('Error updating pipeline settings:', err));
      
      debugLog("Optimierungseinstellungen angewendet");
      status.textContent = 'Optimierungseinstellungen angewendet';
      
      // Some settings might require a restart
      if (window.optimizationSettings.useCachedAudioContext !== oldUseCachedAudioContext) {
        debugLog("Neustart des Audio-Systems empfohlen wegen Änderung der Audio-Context-Konfiguration");
        alert("Bitte starten Sie das Audio-System neu, um die Änderungen zu übernehmen.");
      }
    });
    
    // Reset optimization settings button
    this.resetOptimizationSettingsBtn.addEventListener('click', () => {
      // Reset to defaults
      window.optimizationSettings.useProgressiveTTS = true;
      window.optimizationSettings.useTokenStreaming = true;
      window.optimizationSettings.useChunkBasedAudio = true;
      window.optimizationSettings.useEarlyAudioProcessing = false;
      window.optimizationSettings.useCachedAudioContext = false;
      window.optimizationSettings.useSmartChunkSplitting = true;
      window.optimizationSettings.ttsDynamicChunkSize = 100;
      
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
    this.useChunkBasedAudioCheckbox.checked = window.optimizationSettings.useChunkBasedAudio;
    this.disableVadCheckbox.checked = window.optimizationSettings.disableVad;
    this.useLegacyHttpCheckbox.checked = window.optimizationSettings.useLegacyHttp;
    this.useEarlyAudioProcessingCheckbox.checked = window.optimizationSettings.useEarlyAudioProcessing;
    this.useCachedAudioContextCheckbox.checked = window.optimizationSettings.useCachedAudioContext;
    this.useSmartChunkSplittingCheckbox.checked = window.optimizationSettings.useSmartChunkSplitting;
    this.ttsDynamicChunkSizeSlider.value = window.optimizationSettings.ttsDynamicChunkSize;
    this.ttsDynamicChunkSizeValue.textContent = window.optimizationSettings.ttsDynamicChunkSize;
    
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
          if (!stats.transcriptToLLMLatency) stats.transcriptToLLMLatency = [];
          stats.transcriptToLLMLatency.push(now - stats.transcriptionReceived);
          if (stats.transcriptToLLMLatency.length > 10) stats.transcriptToLLMLatency.shift();
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
    
    // Update UI if optimization panel is visible
    if (optimizationPanel.style.display !== 'none') {
      this.updateLatencyStatsUI();
    }
  }
};