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
  window.asrMode = document.getElementById('asrMode');
  window.optimizationMode = document.getElementById('optimizationMode');
  window.debugPanel = document.getElementById('debugPanel');
  window.optimizationPanel = document.getElementById('optimizationPanel');
  window.debugOutput = document.getElementById('debugOutput');
  window.eventTimeline = document.getElementById('eventTimeline');
  // Separate container for audio level history (bars + numeric values)
  window.audioLevelHistory = document.getElementById('audioLevelHistory');
}

// Initialize UI elements on load
initializeUIElements();

// Export function to allow refreshing references
window.refreshUIElements = initializeUIElements;

// Audio state
let currentAudio = null;
let currentUtterance = null;
let recording = false;

// Noise level tracking
let noiseValues = [];
let maxNoiseLevel = 0;
let averageNoiseLevel = 0;
let currentNoiseLevel = 0;

// Available OpenAI voices
const openaiVoices = ['nova', 'shimmer', 'echo', 'onyx', 'fable', 'alloy', 'ash', 'sage', 'coral'];

// Debug log (FE console + debug panel)
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

// Event timeline (FE console + timeline panel)
function eventLog(message) {
  console.log(message);
  if (!eventTimeline) return;
  const entry = document.createElement('div');
  entry.textContent = `${new Date().toLocaleTimeString()}: ${message}`;
  eventTimeline.appendChild(entry);
  // Keep only last 100 entries
  while (eventTimeline.children.length > 100) {
    eventTimeline.removeChild(eventTimeline.firstChild);
  }
  eventTimeline.scrollTop = eventTimeline.scrollHeight;
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
  
  // Stop all AudioContext-based buffer sources
  if (window.allAudioSources) {
    window.allAudioSources.forEach(src => {
      try { src.stop(); } catch { }
    });
    window.allAudioSources = [];
  }
  
  // Reset all progressive TTS playback (EventSource)
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
  
  // Do not close WebSocket - only stop audio playback
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
  // Get available TTS voices from the browser
  const voices = speechSynthesis.getVoices();

  // Clear previous options
  langSel.innerHTML = '';
  voiceSel.innerHTML = '';

  // --- Language Dropdown (for ASR/Transcription) ---
  // This is decoupled from TTS voices to ensure correct ISO-639-1 format.
  const supportedLanguages = [
    { code: 'de', name: 'Deutsch' },
    { code: 'en', name: 'English' },
    { code: 'fr', name: 'Français' },
    { code: 'es', name: 'Español' },
    { code: 'it', name: 'Italiano' },
    { code: 'nl', name: 'Nederlands' },
    { code: 'pl', name: 'Polski' },
    { code: 'pt', name: 'Português' },
    { code: 'ru', name: 'Русский' },
    { code: 'ja', name: 'Japanisch' },
    { code: 'zh', name: 'Chinesisch' }
  ];

  supportedLanguages.forEach(lang => {
    const opt = document.createElement('option');
    opt.value = lang.code;
    opt.textContent = lang.name;
    langSel.appendChild(opt);
  });

  // Set default language to German as requested
  langSel.value = 'de';

  // --- Voice Dropdown (for TTS) ---
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
function createBotMessage(text, model = (window.modelSel ? window.modelSel.value : 'default'), voice) {
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
  let title = `Assistant (${model}`;
  if (model.startsWith('gpt-4')) title += ' - GPT-4';
  else if (model.startsWith('gpt-3.5')) title += ' - GPT-3.5';
  else if (model.startsWith('gpt-3')) title += ' - GPT-3';
  header.textContent = title;
  
  const content = document.createElement('div');
  content.className = 'message-content';
  // If text is initially undefined or null, use an empty string to avoid "undefined" or "null" literal text
  content.textContent = text || ''; // Ensure empty string if text is falsy

  const audioControls = document.createElement('div');
  audioControls.className = 'audio-controls';
  
  const stopButton = document.createElement('button');
  stopButton.className = 'stop-btn';
  stopButton.textContent = '⏹️';
  stopButton.title = 'Audio stoppen';
  stopButton.style.display = 'none'; // Initially hidden
  stopButton.onclick = () => {
    // Stop audio playback for this message only
    if (messageDiv.audio) {
      stopAudio(messageDiv.audio);
      messageDiv.audio = null;
      stopButton.style.display = 'none';
    }
  };
  
  const latencyInfo = document.createElement('div');
  latencyInfo.className = 'latency-info';
  latencyInfo.textContent = '⏳'; // Hourglass icon as placeholder
  latencyInfo.style.display = 'none'; // Initially hidden
  
  audioControls.appendChild(stopButton);
  audioControls.appendChild(latencyInfo);
  messageDiv.appendChild(header);
  messageDiv.appendChild(content);
  messageDiv.appendChild(audioControls);
  chatLogElement.appendChild(messageDiv);
  chatLogElement.scrollTop = chatLogElement.scrollHeight;
  
  // Streaming audio setup
  messageDiv.audio = null;
  messageDiv.stopButton = stopButton;
  messageDiv.latencyInfo = latencyInfo;
  
  // Token streaming handler
  let isFirstToken = true;
  function handleToken(token) {
    // Show message content gradually
    if (isFirstToken) {
      content.textContent = ''; // Clear initial placeholder text
      isFirstToken = false;
    }
    content.textContent += token;
    
    // Auto-scroll chat log
    chatLogElement.scrollTop = chatLogElement.scrollHeight;
  }
  
  // Audio playback handler
  function handleAudio(audioUrl) {
    // Stop any ongoing audio for this message
    if (messageDiv.audio) {
      stopAudio(messageDiv.audio);
      messageDiv.audio = null;
      stopButton.style.display = 'none';
    }
    
    // Create new audio element for the received audio URL
    const audio = new Audio(audioUrl);
    audio.crossOrigin = 'anonymous'; // Keep this for legitimate audio
    audio.play().catch(err => {
      console.error('Error beim Abspielen der Audio:', err);
      debugLog('Audio play error: ' + err.message);
    });
    
    // Update message div with new audio reference
    messageDiv.audio = audio;
    stopButton.style.display = 'inline-block';
    
    // Cleanup on audio end
    audio.onended = () => {
      messageDiv.audio = null;
      stopButton.style.display = 'none';
    };
  }
  
  return { messageDiv, content, stopButton, latencyInfo };
}

// Send a chat message to the server
async function sendMessage(content, model, voice, isRetry = false) {
  // Trim content to avoid unnecessary spaces
  content = content.trim();
  if (!content) return;
  
  // Disable UI elements to prevent further input
  window.isProcessingOrPlayingAudio = true;
  status.textContent = 'Warte auf Antwort...';
  stopBtn.disabled = true;
  clearBtn.disabled = true;
  debugBtn.disabled = true;
  optimizationBtn.disabled = true;
  modelSel.disabled = true;
  langSel.disabled = true;
  voiceSel.disabled = true;
  asrMode.disabled = true;
  optimizationMode.disabled = true;
  
  // Create user message bubble
  const userMessageDiv = createUserMessage(content);
  
  // Prepare request payload
  const payload = {
    model: model || 'gpt-3.5-turbo',
    messages: [{ role: 'user', content }],
    // Include voice selection for OpenAI TTS
    voice: voice || 'nova', // Default to 'nova' if not specified
    temperature: 0.7,
    max_tokens: 150,
    n: 1,
    stop: null,
    stream: true // Enable streaming
  };
  
  try {
    // Send message to server
    const res = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    
    // Handle response streaming
    const reader = res.body.getReader();
    const decoder = new TextDecoder('utf-8');
    let isFirstChunk = true;
    let fullResponse = '';
    
    // Reset debug output for new response
    debugOutput.innerHTML = '';
    
    // Read and process each chunk of the response
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      
      // Decode and trim the chunk
      const chunk = decoder.decode(value, { stream: true }).trim();
      if (!chunk) continue;
      
      // Log the raw chunk data to debug output
      debugLog('Raw chunk: ' + chunk);
      
      // Handle initial data (e.g., latency info)
      if (isFirstChunk) {
        isFirstChunk = false;
        // Extract and display latency information if available
        const latencyMatch = chunk.match(/latency:\s*(\d+ms)/);
        if (latencyMatch) {
          const latency = latencyMatch[1];
          userMessageDiv.querySelector('.latency-info').textContent = `🕒 ${latency}`;
        }
      }
      
      // Append chunk to full response
      fullResponse += chunk;
      
      // Check for audio URL in the chunk
      const audioUrlMatch = chunk.match(/https?:\/\/[^\s]+/);
      if (audioUrlMatch) {
        const audioUrl = audioUrlMatch[0];
        // Play the audio using the browser's native Audio API
        const audio = new Audio(audioUrl);
        audio.crossOrigin = 'anonymous';
        audio.play().catch(err => {
          console.error('Error beim Abspielen der Audio:', err);
          debugLog('Audio play error: ' + err.message);
        });
      }
      
      // Update the message content with the received chunk
      userMessageDiv.querySelector('.message-content').textContent = fullResponse;
      
      // Auto-scroll chat log
      window.chatLog.scrollTop = window.chatLog.scrollHeight;
    }
    
    // Close the reader when done
    reader.releaseLock();
    
    // Re-enable UI elements
    status.textContent = 'Fertig!';
    stopBtn.disabled = false;
    clearBtn.disabled = false;
    debugBtn.disabled = false;
    optimizationBtn.disabled = false;
    modelSel.disabled = false;
    langSel.disabled = false;
    voiceSel.disabled = false;
    asrMode.disabled = false;
    optimizationMode.disabled = false;
  } catch (err) {
    console.error('Fehler beim Senden der Nachricht:', err);
    status.textContent = 'Fehler! Bitte erneut versuchen.';
    
    // Retry logic for network or server errors
    if (!isRetry) {
      setTimeout(() => {
        sendMessage(content, model, voice, true);
      }, 2000);
    }
  }
}

// Stop all ongoing processes (audio, video, etc.)
function stopAll() {
  // Stop audio playback
  stopAllAudio();
  
  // Stop video playback (if any)
  if (window.currentVideo) {
    window.currentVideo.pause();
    window.currentVideo = null;
  }
  
  // Reset UI elements
  status.textContent = 'Bereit';
  stopBtn.disabled = true;
  clearBtn.disabled = false;
  debugBtn.disabled = false;
  optimizationBtn.disabled = false;
  modelSel.disabled = false;
  langSel.disabled = false;
  voiceSel.disabled = false;
  asrMode.disabled = false;
  optimizationMode.disabled = false;
}

// Initialize model and voice selections on page load
loadModels();
populateVoices();

// Global error handler
window.onerror = function(message, source, lineno, colno, error) {
  console.error('Global error handler:', message, source, lineno, colno, error);
  alert('Ein unerwarteter Fehler ist aufgetreten. Bitte versuchen Sie es erneut.');
  // Optionally, send error details to server for logging
  // fetch('/api/logError', { method: 'POST', body: JSON.stringify({ message, source, lineno, colno, error }) });
};