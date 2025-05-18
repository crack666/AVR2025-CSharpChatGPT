// Utility functions for the application

// UI Elements - wrapped in a function to allow refreshing references
function initializeUIElements() {
  window.status = document.getElementById('status');
  window.stopBtn = document.getElementById('stopBtn');
  window.clearBtn = document.getElementById('clearBtn');
  window.debugBtn = document.getElementById('debugBtn');
  window.optimizationBtn = document.getElementById('optimizationBtn');
  window.chatLog = document.getElementById('chatLog');
  window.modelSel = document.getElementById('model');
  window.langSel = document.getElementById('language');
  window.voiceSel = document.getElementById('voice');
  window.silenceSecInput = document.getElementById('silenceSec');
  window.silenceSecRange = document.getElementById('silenceSecRange');
  window.silenceThresholdRange = document.getElementById('silenceThresholdRange');
  window.thresholdValue = document.getElementById('thresholdValue');
  window.asrMode = document.getElementById('asrMode');
  window.optimizationMode = document.getElementById('optimizationMode');
  window.debugPanel = document.getElementById('debugPanel');
  window.optimizationPanel = document.getElementById('optimizationPanel');
  window.debugOutput = document.getElementById('debugOutput');
}

// Initialize UI elements on load
initializeUIElements();

// Export function to allow refreshing references
window.refreshUIElements = initializeUIElements;

// Audio state
let currentAudio = null;
let currentUtterance = null;
let silenceThreshold = parseFloat(silenceThresholdRange.value);
let recording = false;

// Noise level tracking
let noiseValues = [];
let maxNoiseLevel = 0;
let averageNoiseLevel = 0;
let currentNoiseLevel = 0;

// Available OpenAI voices
const openaiVoices = ['nova', 'shimmer', 'echo', 'onyx', 'fable', 'alloy', 'ash', 'sage', 'coral'];

// Debug log
function debugLog(message) {
  console.log(message);
  const logEntry = document.createElement('div');
  logEntry.textContent = `${new Date().toLocaleTimeString()}: ${message}`;
  debugOutput.appendChild(logEntry);
  
  // Keep only last 100 entries
  while (debugOutput.children.length > 100) {
    debugOutput.removeChild(debugOutput.firstChild);
  }
  
  // Auto-scroll
  debugOutput.scrollTop = debugOutput.scrollHeight;
}

// Function to stop all audio playback
function stopAllAudio() {
  // Stop normal audio
  if (currentAudio) {
    currentAudio.pause();
    URL.revokeObjectURL(currentAudio.src);
    currentAudio = null;
  }
  
  // Stop speech synthesis
  if (currentUtterance) {
    speechSynthesis.cancel();
    currentUtterance = null;
  }
  
  // Reset all progressive TTS playback
  if (window.eventSource) {
    window.eventSource.close();
  }
  
  // Reset all audio elements created for progressive TTS
  if (window.allAudioElements) {
    window.allAudioElements.forEach(audio => {
      if (audio) {
        audio.pause();
        if (audio.src) URL.revokeObjectURL(audio.src);
      }
    });
    window.allAudioElements = [];
  }
  
  // CRITICAL: Make sure we re-enable audio processing regardless of how audio was stopped
  window.isProcessingOrPlayingAudio = false;
  
  console.log("All audio playback stopped, processing re-enabled");
}

// Function to stop specific audio
function stopAudio(audio) {
  if (audio) {
    audio.pause();
    if (audio === currentAudio) {
      URL.revokeObjectURL(audio.src);
      currentAudio = null;
    }
  }
  
  // Always ensure processing is re-enabled
  window.isProcessingOrPlayingAudio = false;
}

// Initialize values
thresholdValue.textContent = silenceThreshold.toFixed(3);

// Dynamically load available chat models from server
async function loadModels() {
  modelSel.innerHTML = '';
  try {
    const res = await fetch('/api/models');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const models = await res.json();
    models.forEach(m => {
      const opt = document.createElement('option'); opt.value = m; opt.textContent = m;
      modelSel.appendChild(opt);
    });
  } catch (err) {
    console.error('Fehler beim Laden der Modelle:', err);
    ['gpt-3.5-turbo', 'gpt-4'].forEach(m => {
      const opt = document.createElement('option'); opt.value = m; opt.textContent = m;
      modelSel.appendChild(opt);
    });
  }
}

// Populate browser voices and OpenAI TTS voices
function populateVoices() {
  // Speech language dropdown
  const voices = speechSynthesis.getVoices();
  langSel.innerHTML = '';
  voiceSel.innerHTML = '';
  const languages = [...new Set(voices.map(v => v.lang))];
  languages.forEach(lang => {
    const opt = document.createElement('option'); opt.value = lang; opt.textContent = lang;
    langSel.appendChild(opt);
  });
  // OpenAI TTS voice options
  openaiVoices.forEach(v => {
    const opt = document.createElement('option');
    opt.value = v;
    opt.textContent = `OpenAI ${v.charAt(0).toUpperCase() + v.slice(1)}`;
    voiceSel.appendChild(opt);
  });
  // Browser-native voices
  voices.forEach(voice => {
    const opt = document.createElement('option');
    opt.value = voice.name;
    opt.textContent = `${voice.name} (${voice.lang})`;
    voiceSel.appendChild(opt);
  });
}

// Create a user message bubble
function createUserMessage(text) {
  // Make sure we have the latest reference to chatLog
  const chatLogElement = window.chatLog || document.getElementById('chatLog');
  if (!chatLogElement) {
    console.error("chatLog element not found!");
    return null;
  }
  
  const messageDiv = document.createElement('div');
  messageDiv.className = 'message user-message';
  
  const header = document.createElement('div');
  header.className = 'message-header';
  header.textContent = 'Du';
  
  const content = document.createElement('div');
  content.className = 'message-content';
  content.textContent = text;
  
  messageDiv.appendChild(header);
  messageDiv.appendChild(content);
  chatLogElement.appendChild(messageDiv);
  chatLogElement.scrollTop = chatLogElement.scrollHeight;
  
  return messageDiv;
}

// Create a bot message bubble with optional audio
function createBotMessage(text, model = window.modelSel ? window.modelSel.value : 'default') {
  // Make sure we have the latest reference to chatLog
  const chatLogElement = window.chatLog || document.getElementById('chatLog');
  if (!chatLogElement) {
    console.error("chatLog element not found!");
    return { messageDiv: null, content: null, stopButton: null, latencyInfo: null };
  }
  
  const messageDiv = document.createElement('div');
  messageDiv.className = 'message bot-message';
  
  const header = document.createElement('div');
  header.className = 'message-header';
  header.textContent = `Assistant (${model})`;
  
  const content = document.createElement('div');
  content.className = 'message-content';
  content.textContent = text || '...';
  
  const latencyInfo = document.createElement('div');
  latencyInfo.className = 'message-latency';
  latencyInfo.innerHTML = '<span class="latency-label">Latenz:</span> ' +
                         '<span class="latency-text-label">Text:</span> <span class="latency-text-value">berechne...</span> | ' + 
                         '<span class="latency-audio-label">Audio:</span> <span class="latency-audio-value">berechne...</span>';
  
  const controls = document.createElement('div');
  controls.className = 'message-controls';
  
  const stopButton = document.createElement('button');
  stopButton.className = 'stop-button';
  stopButton.textContent = 'Audio stoppen';
  stopButton.style.display = 'none';  // Hide initially until audio is playing
  
  messageDiv.appendChild(header);
  messageDiv.appendChild(content);
  messageDiv.appendChild(latencyInfo);
  messageDiv.appendChild(controls);
  controls.appendChild(stopButton);
  chatLogElement.appendChild(messageDiv);
  chatLogElement.scrollTop = chatLogElement.scrollHeight;
  
  return { messageDiv, content, stopButton, latencyInfo };
}