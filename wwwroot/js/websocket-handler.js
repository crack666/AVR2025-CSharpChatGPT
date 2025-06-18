// --- WebSocket State & Functions ---
let webSocket = null;
let connectionPromise = null;
let currentBotModel = null;
let currentBotVoice = null;

// Callbacks and config to be set by initWebSocketHandler
let debugLogCallback = null;
let getPipelineOptionsCallback = null;
let getVadSettingsCallback = null;
let onWebSocketOpenCallback = null;
let onWebSocketCloseCallback = null;
let onWebSocketMessageCallback = null;
let onWebSocketErrorCallback = null;

export function initWebSocketHandler(config) {
    debugLogCallback = config.debugLog;
    getPipelineOptionsCallback = config.getPipelineOptions;
    getVadSettingsCallback = config.getVadSettings;
    onWebSocketOpenCallback = config.onOpen;
    onWebSocketCloseCallback = config.onClose;
    onWebSocketMessageCallback = config.onMessage;
    onWebSocketErrorCallback = config.onError;
    if (debugLogCallback) debugLogCallback("[WebSocketHandler] Initialized.");
}

function getWebSocketUrlWithParams() {
    const protocol = window.location.protocol === 'https:' ? 'wss' : 'ws';
    const host = window.location.host;

    let queryParams = {};
    if (getPipelineOptionsCallback) {
        const pipelineOpts = getPipelineOptionsCallback();
        if (pipelineOpts) {
            queryParams = { ...pipelineOpts }; // Clone to avoid modifying original
            currentBotModel = pipelineOpts.ChatModel;
            currentBotVoice = pipelineOpts.TtsVoice;
            if (debugLogCallback) debugLogCallback("[WebSocketHandler] Using pipeline options:", queryParams);
        } else {
            console.error("[WebSocketHandler] CRITICAL: getPipelineOptionsCallback returned undefined.");
            // Fallback or error handling might be needed here
        }
    } else {
        console.error("[WebSocketHandler] CRITICAL: getPipelineOptionsCallback is not available.");
        // Fallback to some very basic defaults
        queryParams = {
            Language: 'en',
            ChatModel: 'gpt-3.5-turbo',
            TtsVoice: 'nova',
            DisableVad: false,
            DisableTts: false,
            DisableProgressiveTts: false,
        };
        currentBotModel = queryParams.ChatModel;
        currentBotVoice = queryParams.TtsVoice;
    }

    if (getVadSettingsCallback) {
        const vadSettings = getVadSettingsCallback();
        if (vadSettings && !queryParams.DisableVad) {
            queryParams = { ...queryParams, ...vadSettings }; // Merge VAD settings
            if (debugLogCallback) debugLogCallback("[WebSocketHandler] Including VAD settings:", vadSettings);
        } else if (queryParams.DisableVad) {
            if (debugLogCallback) debugLogCallback("[WebSocketHandler] VAD is disabled by pipeline options, not including VAD settings.");
        }
    } else {
        if (debugLogCallback) debugLogCallback("[WebSocketHandler] getVadSettingsCallback not available. VAD settings might be incorrect if VAD is enabled.");
    }

    const queryString = new URLSearchParams(queryParams).toString();
    const path = `/ws/audio?${queryString}`;
    const fullUrl = `${protocol}://${host}${path}`;
    if (debugLogCallback) debugLogCallback("[WebSocketHandler] Constructed WebSocket URL:", fullUrl);
    return fullUrl;
}

export async function connectWebSocket() {
    if (!debugLogCallback || !onWebSocketOpenCallback || !onWebSocketCloseCallback || !onWebSocketMessageCallback || !onWebSocketErrorCallback) {
        console.error("[WebSocketHandler] Not initialized properly. Call initWebSocketHandler first.");
        throw new Error("WebSocketHandler module not initialized.");
    }

    // If a connection is already in progress, return the existing promise
    if (connectionPromise) {
        debugLogCallback('[WebSocketHandler] Connection attempt already in progress. Reusing existing promise.');
        return connectionPromise;
    }

    if (webSocket && webSocket.readyState === WebSocket.OPEN) {
        debugLogCallback('[WebSocketHandler] WebSocket already open. Checking if VAD settings need update.');
        if (getPipelineOptionsCallback && getVadSettingsCallback) {
            const pipelineOptions = getPipelineOptionsCallback();
            if (pipelineOptions && !pipelineOptions.DisableVad) {
                const vadSettings = getVadSettingsCallback();
                if (vadSettings) sendWebSocketMessage({ type: 'updateVadSettings', payload: vadSettings });
            } else {
                debugLogCallback("[WebSocketHandler] VAD disabled, not sending VAD settings update on existing WebSocket.");
            }
        }
        return Promise.resolve(); // Already open
    }

    if (webSocket && (webSocket.readyState === WebSocket.CONNECTING || webSocket.readyState === WebSocket.CLOSING)) {
        debugLogCallback('[WebSocketHandler] WebSocket is currently connecting or closing. Please wait.');
        // Optionally, return a promise that resolves when the state changes, or reject.
        return Promise.reject(new Error("WebSocket busy")); 
    }

    const wsUrl = getWebSocketUrlWithParams();
    debugLogCallback(`[WebSocketHandler] Connecting to WebSocket: ${wsUrl}`);
    
    connectionPromise = new Promise((resolve, reject) => {
        webSocket = new WebSocket(wsUrl);

        webSocket.onopen = (event) => {
            connectionPromise = null; // Clear promise on success
            onWebSocketOpenCallback(event);
            resolve();
        };

        webSocket.onmessage = (event) => {
            onWebSocketMessageCallback(event);
        };

        webSocket.onerror = (event) => {
            connectionPromise = null; // Clear promise on error
            onWebSocketErrorCallback(event);
            reject(new Error("WebSocket connection error")); // Reject promise on error during connection phase
        };

        webSocket.onclose = (event) => {
            // If the promise is still pending, it means the connection failed.
            // Clear the promise to allow new connection attempts.
            if (connectionPromise) {
                connectionPromise = null;
            }
            onWebSocketCloseCallback(event);
            // The socket is now closed, so we can nullify the main variable.
            webSocket = null;
        };
    });
    return connectionPromise;
}

export async function closeWebSocket(sendEndOfStream = true) {
    if (connectionPromise) {
        debugLogCallback('[WebSocketHandler] A connection is in progress while close is called. Nullifying promise.');
        connectionPromise = null;
    }

    if (webSocket && webSocket.readyState === WebSocket.OPEN) {
        debugLogCallback("[WebSocketHandler] Closing WebSocket connection...");
        if (sendEndOfStream) {
            try {
                // This message signals the server to process any buffered audio before closing.
                webSocket.send(JSON.stringify({ type: "endOfStream" }));
                debugLogCallback("[WebSocketHandler] Sent endOfStream message.");
            } catch (e) {
                console.error("[WebSocketHandler] Error sending endOfStream message:", e);
            }
        }
        webSocket.close();
    } else if (webSocket && webSocket.readyState === WebSocket.CONNECTING) {
        debugLogCallback("[WebSocketHandler] WebSocket is connecting, attempting to close...");
        webSocket.close(); // This will trigger onerror and then onclose, which will also nullify the websocket object.
    } else {
        debugLogCallback("[WebSocketHandler] WebSocket not open or already closed.");
    }
    webSocket = null; // Also nullify here to ensure a clean state immediately.
}

export function sendWebSocketMessage(messageObject) {
    if (webSocket && webSocket.readyState === WebSocket.OPEN) {
        try {
            const messageString = JSON.stringify(messageObject);
            webSocket.send(messageString);
            if (debugLogCallback) debugLogCallback('[WebSocketHandler] Sent message:', messageObject);
        } catch (error) {
            console.error('[WebSocketHandler] Error sending WebSocket message:', error, messageObject);
        }
    } else {
        if (debugLogCallback) debugLogCallback('[WebSocketHandler] WebSocket not open. Cannot send message:', messageObject);
    }
}

export function sendBinaryData(data) {
    if (webSocket && webSocket.readyState === WebSocket.OPEN) {
        try {
            webSocket.send(data);
        } catch (error) {
            console.error("[WebSocketHandler] Error sending binary data:", error);
        }
    } else {
        // if (debugLogCallback) debugLogCallback('[WebSocketHandler] WebSocket not open. Cannot send binary data.');
    }
}

export function getWebSocketState() {
    return webSocket ? webSocket.readyState : WebSocket.CLOSED; // Return WebSocket.CLOSED (3) if null
}

export function getCurrentBotDetails() {
    return { model: currentBotModel, voice: currentBotVoice };
}
