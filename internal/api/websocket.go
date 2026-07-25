package api

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"log"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/ysk-dev-taualpha/local-ai-companion/runtime/internal/agent"
	"github.com/ysk-dev-taualpha/local-ai-companion/runtime/internal/client"
	"github.com/ysk-dev-taualpha/local-ai-companion/runtime/internal/memory"
	"github.com/ysk-dev-taualpha/local-ai-companion/runtime/internal/state"
	"github.com/ysk-dev-taualpha/local-ai-companion/runtime/internal/tts"
)

func buildCheckOrigin(allowedOrigins []string) func(r *http.Request) bool {
	if len(allowedOrigins) == 0 {
		return func(r *http.Request) bool {
			origin := r.Header.Get("Origin")
			if origin == "" {
				return true
			}
			u, err := url.Parse(origin)
			if err != nil {
				return false
			}
			host := u.Hostname()
			return host == "localhost" || host == "127.0.0.1" || host == "::1"
		}
	}
	return func(r *http.Request) bool {
		origin := r.Header.Get("Origin")
		if origin == "" {
			return true
		}
		for _, allowed := range allowedOrigins {
			if strings.EqualFold(origin, allowed) {
				return true
			}
		}
		return false
	}
}

type WSMessage struct {
	Type      string `json:"type"`
	Payload   string `json:"payload"`
	RequestID string `json:"request_id,omitempty"`
}

type WSAIResponse struct {
	Type           string                   `json:"type"`
	RequestID      string                   `json:"request_id"`
	ConversationID string                   `json:"conversation_id,omitempty"`
	Assistant      *client.AssistantMessage `json:"assistant,omitempty"`
	Text           string                   `json:"text,omitempty"`
	Audio          string                   `json:"audio,omitempty"`
	Error          string                   `json:"error,omitempty"`
}

type WSAudioMessage struct {
	Type      string `json:"type"`
	RequestID string `json:"request_id"`
	Data      string `json:"data"`
	Format    string `json:"format"`
}

type WSStateNotification struct {
	Type  string `json:"type"`
	State string `json:"state"`
}

type wsConnState struct {
	writeMu   sync.Mutex
	mu        sync.Mutex // per-session agent lock (replaces global agentMu)
	sessionID string
}

type WebSocketHub struct {
	mu               sync.RWMutex
	stateMu          sync.Mutex
	conns            map[*websocket.Conn]*wsConnState
	memoryStore      *memory.Store
	pythonClient     PythonClient
	ttsClient        tts.TTSClient
	stateMachine     *state.StateMachine
	agentLoop        *agent.Loop
	voicePipeline    *VoicePipeline
	pendingCancels   map[string]context.CancelFunc
	requestTimeoutMs int
	upgrader         websocket.Upgrader
}

func NewWebSocketHub(memStore *memory.Store, pythonClient PythonClient, ttsClient tts.TTSClient, stateMachine *state.StateMachine, requestTimeoutMs int, allowedOrigins []string, agentLoop *agent.Loop, vp *VoicePipeline) *WebSocketHub {
	return &WebSocketHub{
		conns:            make(map[*websocket.Conn]*wsConnState),
		memoryStore:      memStore,
		pythonClient:     pythonClient,
		ttsClient:        ttsClient,
		stateMachine:     stateMachine,
		agentLoop:        agentLoop,
		voicePipeline:    vp,
		pendingCancels:   make(map[string]context.CancelFunc),
		requestTimeoutMs: requestTimeoutMs,
		upgrader: websocket.Upgrader{
			CheckOrigin: buildCheckOrigin(allowedOrigins),
		},
	}
}

func (h *WebSocketHub) HandleWS(w http.ResponseWriter, r *http.Request) {
	conn, err := h.upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}

	// Reuse session ID from query param for history continuity across reconnects
	sid := r.URL.Query().Get("session_id")
	if sid == "" {
		sid = newRequestID()
	} else if !isValidSessionID(sid) {
		log.Printf("websocket: rejected invalid session_id=%q, generating new", sid)
		sid = newRequestID()
	}

	h.mu.Lock()
	h.conns[conn] = &wsConnState{sessionID: sid}
	h.mu.Unlock()
	log.Printf("websocket: connected session_id=%s remote=%s", sid, r.RemoteAddr)

	defer func() {
		h.mu.Lock()
		delete(h.conns, conn)
		h.mu.Unlock()
		conn.Close()
	}()

	for {
		msgType, msgBytes, err := conn.ReadMessage()
		if err != nil {
			break
		}

		if msgType == websocket.BinaryMessage {
			h.handleAudioChunk(conn, msgBytes)
			continue
		}

		var msg WSMessage
		if err := json.Unmarshal(msgBytes, &msg); err != nil {
			continue
		}

		switch msg.Type {
		case "text":
			if h.agentLoop != nil {
				go h.handleTextMessageAgent(conn, msg)
			} else {
				h.handleTextMessage(conn, msg)
			}
		case "cancel_speech":
			h.handleCancelSpeech(msg.RequestID)
		default:
			h.handleEcho(conn, msg)
		}
	}
}

func (h *WebSocketHub) sessionID(conn *websocket.Conn) string {
	h.mu.RLock()
	cs := h.conns[conn]
	h.mu.RUnlock()
	if cs == nil {
		return "default"
	}
	return cs.sessionID
}

func (h *WebSocketHub) handleTextMessageAgent(conn *websocket.Conn, msg WSMessage) {
	requestID := msg.RequestID
	if requestID == "" {
		requestID = newRequestID()
	}

	h.mu.RLock()
	cs := h.conns[conn]
	h.mu.RUnlock()
	if cs == nil {
		return
	}
	cs.mu.Lock()
	defer cs.mu.Unlock()

	h.stateMu.Lock()
	if err := h.stateMachine.Transition(state.LISTENING); err != nil {
		h.stateMu.Unlock()
		h.sendError(conn, requestID, "busy: "+err.Error())
		return
	}
	h.broadcastState("LISTENING")
	if err := h.stateMachine.Transition(state.THINKING); err != nil {
		h.broadcastState("IDLE")
		h.stateMachine.Reset()
		h.stateMu.Unlock()
		h.sendError(conn, requestID, "invalid state transition: "+err.Error())
		return
	}
	h.broadcastState("THINKING")
	h.stateMu.Unlock()

	ctx, cancel := context.WithTimeout(context.Background(), 120*time.Second)
	defer cancel()

	sessionID := h.sessionID(conn)
	responseText, err := h.agentLoop.Run(ctx, sessionID, msg.Payload, requestID)
	if err != nil {
		h.sendError(conn, requestID, "LLM error: "+err.Error())
		h.resetAndBroadcastIdle()
		return
	}
	if responseText == "" {
		h.sendError(conn, requestID, "LLM returned empty response")
		h.resetAndBroadcastIdle()
		return
	}

	assistant := parseAssistantResponse(responseText)
	aiResp := WSAIResponse{
		Type:      "ai_response",
		RequestID: requestID,
		Assistant: &assistant,
		Text:      assistant.Text,
	}
	log.Printf("websocket: ai_response: request_id=%s text=%q", requestID, assistant.Text)

	if err := h.writeJSON(conn, aiResp); err != nil {
		log.Printf("websocket: write ai_response failed: %v", err)
		h.resetAndBroadcastIdle()
		return
	}

	h.stateMu.Lock()
	_ = h.stateMachine.Transition(state.SPEAKING)
	h.broadcastState("SPEAKING")
	h.stateMu.Unlock()

	go h.sendTTSSeparately(conn, requestID, assistant.Text)
}

func (h *WebSocketHub) HandleVoiceTextAgent(conn *websocket.Conn, text, requestID string) {
	if h.agentLoop == nil {
		h.sendError(conn, requestID, "agent loop not available")
		h.resetAndBroadcastIdle()
		return
	}

	h.mu.RLock()
	cs := h.conns[conn]
	h.mu.RUnlock()
	if cs == nil {
		return
	}
	cs.mu.Lock()
	defer cs.mu.Unlock()

	ctx, cancel := context.WithTimeout(context.Background(), 120*time.Second)
	defer cancel()

	sessionID := h.sessionID(conn)
	responseText, err := h.agentLoop.Run(ctx, sessionID, text, requestID)
	if err != nil {
		h.sendError(conn, requestID, "LLM error: "+err.Error())
		h.resetAndBroadcastIdle()
		return
	}
	if responseText == "" {
		h.sendError(conn, requestID, "LLM returned empty response")
		h.resetAndBroadcastIdle()
		return
	}

	assistant := parseAssistantResponse(responseText)
	aiResp := WSAIResponse{
		Type:      "ai_response",
		RequestID: requestID,
		Assistant: &assistant,
		Text:      assistant.Text,
	}
	log.Printf("websocket: ai_response: request_id=%s text=%q", requestID, assistant.Text)

	if err := h.writeJSON(conn, aiResp); err != nil {
		log.Printf("websocket: write ai_response failed: %v", err)
		h.resetAndBroadcastIdle()
		return
	}

	h.stateMu.Lock()
	_ = h.stateMachine.Transition(state.SPEAKING)
	h.broadcastState("SPEAKING")
	h.stateMu.Unlock()

	go h.sendTTSSeparately(conn, requestID, assistant.Text)
}

func (h *WebSocketHub) sendTTSSeparately(conn *websocket.Conn, requestID, text string) {
	defer h.resetAndBroadcastIdle()
	defer func() {
		if r := recover(); r != nil {
			log.Printf("websocket: tts synthesis panicked: %v", r)
		}
	}()

	if h.ttsClient == nil || text == "" {
		return
	}
	audioData, ttsErr := h.ttsClient.Speak(text)
	if ttsErr != nil {
		log.Printf("websocket: tts synthesis failed: %v", ttsErr)
		return
	}
	audioMsg := WSAudioMessage{
		Type:      "audio",
		RequestID: requestID,
		Data:      base64.StdEncoding.EncodeToString(audioData),
		Format:    "wav",
	}
	if err := h.writeJSON(conn, audioMsg); err != nil {
		log.Printf("websocket: failed to write audio message: %v", err)
	}
}

func parseAssistantResponse(raw string) client.AssistantMessage {
	assistant := client.AssistantMessage{
		Text:          raw,
		SpeakStyle:    "normal",
		Interruptible: true,
	}
	if raw == "" {
		return assistant
	}

	clean := raw
	clean = strings.TrimSpace(clean)
	clean = strings.TrimPrefix(clean, "```json\n")
	clean = strings.TrimPrefix(clean, "```json")
	clean = strings.TrimPrefix(clean, "```\n")
	clean = strings.TrimPrefix(clean, "```")
	clean = strings.TrimSuffix(clean, "\n```")
	clean = strings.TrimSuffix(clean, "```")
	clean = strings.TrimSpace(clean)

	var parsed client.AssistantMessage
	if err := json.Unmarshal([]byte(clean), &parsed); err != nil {
		return assistant
	}
	if parsed.Text == "" {
		return assistant
	}
	if parsed.SpeakStyle == "" {
		parsed.SpeakStyle = "normal"
	}
	return parsed
}

func (h *WebSocketHub) handleTextMessage(conn *websocket.Conn, msg WSMessage) {
	h.stateMu.Lock()
	defer h.stateMu.Unlock()

	if err := h.stateMachine.Transition(state.LISTENING); err != nil {
		h.sendError(conn, msg.RequestID, "invalid state for listening: "+err.Error())
		return
	}
	h.broadcastState("LISTENING")

	if err := h.stateMachine.Transition(state.THINKING); err != nil {
		h.broadcastState("IDLE")
		h.stateMachine.Reset()
		h.sendError(conn, msg.RequestID, "invalid state transition to thinking: "+err.Error())
		return
	}
	h.broadcastState("THINKING")

	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(h.requestTimeoutMs)*time.Millisecond)
	defer cancel()

	req := client.ConversationRequest{
		Message:   msg.Payload,
		RequestID: msg.RequestID,
	}
	resp, err := h.pythonClient.Send(ctx, req)
	if err != nil {
		h.sendError(conn, msg.RequestID, err.Error())
		h.broadcastState("IDLE")
		h.stateMachine.Reset()
		return
	}

	if err := h.stateMachine.Transition(state.SPEAKING); err != nil {
		h.broadcastState("IDLE")
		h.stateMachine.Reset()
		h.sendError(conn, msg.RequestID, "invalid state transition to speaking: "+err.Error())
		return
	}
	h.broadcastState("SPEAKING")

	aiResp := WSAIResponse{
		Type:           "ai_response",
		RequestID:      resp.RequestID,
		ConversationID: resp.ConversationID,
		Assistant:      &resp.Assistant,
	}

	if h.ttsClient != nil && resp.Assistant.Text != "" {
		audioData, ttsErr := h.ttsClient.Speak(resp.Assistant.Text)
		if ttsErr != nil {
			log.Printf("websocket: tts synthesis failed: %v", ttsErr)
		} else {
			aiResp.Audio = base64.StdEncoding.EncodeToString(audioData)
		}
	}
	if err := h.writeJSON(conn, aiResp); err != nil {
		log.Printf("websocket: failed to write ai_response: %v", err)
	}

	if err := h.stateMachine.Transition(state.IDLE); err != nil {
		log.Printf("websocket: failed to transition to IDLE: %v", err)
		h.stateMachine.Reset()
	}
	h.broadcastState("IDLE")
}

func (h *WebSocketHub) resetAndBroadcastIdle() {
	h.stateMu.Lock()
	defer h.stateMu.Unlock()
	h.stateMachine.Reset()
	h.broadcastState("IDLE")
}

func (h *WebSocketHub) handleCancelSpeech(requestID string) {
	h.stateMu.Lock()
	defer h.stateMu.Unlock()

	if cancel, ok := h.pendingCancels[requestID]; ok {
		cancel()
		delete(h.pendingCancels, requestID)
		log.Printf("voice: speech cancelled: request_id=%s", requestID)
		h.broadcastState("IDLE")
		h.stateMachine.Reset()
	}
}

func (h *WebSocketHub) handleEcho(conn *websocket.Conn, msg WSMessage) {
	resp := map[string]interface{}{
		"type":       msg.Type + "_ack",
		"payload":    msg.Payload,
		"request_id": msg.RequestID,
	}
	h.writeJSON(conn, resp)
}

func (h *WebSocketHub) sendError(conn *websocket.Conn, requestID, errMsg string) {
	errResp := WSAIResponse{
		Type:      "error",
		RequestID: requestID,
		Error:     errMsg,
	}
	h.writeJSON(conn, errResp)
}

func (h *WebSocketHub) broadcastState(stateName string) {
	notif := WSStateNotification{
		Type:  "state_change",
		State: stateName,
	}
	h.Broadcast(notif)
}

func (h *WebSocketHub) writeJSON(conn *websocket.Conn, v interface{}) error {
	h.mu.RLock()
	cs := h.conns[conn]
	h.mu.RUnlock()
	if cs == nil {
		return nil
	}
	cs.writeMu.Lock()
	defer cs.writeMu.Unlock()

	data, err := json.Marshal(v)
	if err != nil {
		return err
	}
	return conn.WriteMessage(websocket.TextMessage, data)
}

func (h *WebSocketHub) Broadcast(msg interface{}) error {
	data, err := json.Marshal(msg)
	if err != nil {
		return err
	}

	h.mu.RLock()
	conns := make([]*websocket.Conn, 0, len(h.conns))
	for conn := range h.conns {
		conns = append(conns, conn)
	}
	h.mu.RUnlock()

	for _, conn := range conns {
		h.mu.RLock()
		cs := h.conns[conn]
		h.mu.RUnlock()
		if cs == nil {
			continue
		}
		cs.writeMu.Lock()
		conn.WriteMessage(websocket.TextMessage, data)
		cs.writeMu.Unlock()
	}
	return nil
}

func (h *WebSocketHub) ConnectionCount() int {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return len(h.conns)
}

var validSessionID = regexp.MustCompile(`^[A-Za-z0-9._:-]{1,128}$`)

func isValidSessionID(sid string) bool {
	return validSessionID.MatchString(sid)
}
