package api

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/gorilla/websocket"
	"github.com/ysk-dev-taualpha/local-ai-companion/runtime/internal/state"
)

type fakeTTSClient struct{}

func (fakeTTSClient) Speak(text string) ([]byte, error) {
	return []byte("wav"), nil
}

func readAllMessages(t *testing.T, conn *websocket.Conn, timeout time.Duration) []string {
	t.Helper()
	var msgs []string
	deadline := time.Now().Add(timeout)
	conn.SetReadDeadline(deadline)
	for {
		_, data, err := conn.ReadMessage()
		if err != nil {
			break
		}
		msg := string(data)
		msgs = append(msgs, msg)
		if strings.Contains(msg, `"state":"IDLE"`) {
			break
		}
	}
	return msgs
}

func TestVoiceInputStateFlow(t *testing.T) {
	hub := newTestHub(t)
	srv := httptest.NewServer(http.HandlerFunc(hub.HandleWS))
	defer srv.Close()
	wsURL := "ws" + strings.TrimPrefix(srv.URL, "http")
	conn, _, err := websocket.DefaultDialer.Dial(wsURL, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()
	if hub.stateMachine.Current() != state.IDLE {
		t.Errorf("expected IDLE, got %s", hub.stateMachine.Current())
	}
	msg := WSMessage{Type: "text", Payload: "こんにちは", RequestID: "voice-test-001"}
	msgBytes, _ := json.Marshal(msg)
	conn.WriteMessage(websocket.TextMessage, msgBytes)
	msgs := readAllMessages(t, conn, 5*time.Second)
	hasListening := false
	hasIdle := false
	for _, m := range msgs {
		if strings.Contains(m, `"state":"LISTENING"`) {
			hasListening = true
		}
		if strings.Contains(m, `"state":"IDLE"`) {
			hasIdle = true
		}
	}
	if !hasListening {
		t.Error("expected LISTENING state notification")
	}
	if !hasIdle {
		t.Error("expected IDLE state notification")
	}
	if hub.stateMachine.Current() != state.IDLE {
		t.Errorf("expected final state IDLE, got %s", hub.stateMachine.Current())
	}
}

func TestVoiceInputConcurrentAudioChunks(t *testing.T) {
	hub := newTestHub(t)
	srv := httptest.NewServer(http.HandlerFunc(hub.HandleWS))
	defer srv.Close()
	wsURL := "ws" + strings.TrimPrefix(srv.URL, "http")
	conn, _, err := websocket.DefaultDialer.Dial(wsURL, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()
	var wg sync.WaitGroup
	var writeMu sync.Mutex
	wg.Add(1)
	go func() {
		defer wg.Done()
		for i := 0; i < 5; i++ {
			frame := buildAudioChunkFrame("req", uint32(i), 16000, []int16{100, 200, 300})
			writeMu.Lock()
			conn.WriteMessage(websocket.BinaryMessage, frame)
			writeMu.Unlock()
			time.Sleep(10 * time.Millisecond)
		}
	}()
	wg.Add(1)
	go func() {
		defer wg.Done()
		time.Sleep(50 * time.Millisecond)
		msg := WSMessage{Type: "text", Payload: "こんにちは", RequestID: "conc-req"}
		msgBytes, _ := json.Marshal(msg)
		writeMu.Lock()
		conn.WriteMessage(websocket.TextMessage, msgBytes)
		writeMu.Unlock()
	}()
	msgs := readAllMessages(t, conn, 10*time.Second)
	wg.Wait()
	hasResponse := false
	for _, m := range msgs {
		if strings.Contains(m, "ai_response") {
			hasResponse = true
			break
		}
	}
	if !hasResponse {
		t.Error("expected ai_response after concurrent audio/text messages")
	}
	if hub.stateMachine.Current() != state.IDLE {
		t.Errorf("expected final state IDLE, got %s", hub.stateMachine.Current())
	}
}

func TestVoiceInputErrorRecovery(t *testing.T) {
	errorMock := &mockPythonClient{
		err: fmt.Errorf("mock STT server error"),
	}
	hub := NewWebSocketHub(nil, errorMock, nil, state.New(nil), 5000, nil, nil, nil)
	srv := httptest.NewServer(http.HandlerFunc(hub.HandleWS))
	defer srv.Close()
	wsURL := "ws" + strings.TrimPrefix(srv.URL, "http")
	conn, _, err := websocket.DefaultDialer.Dial(wsURL, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()
	msg := WSMessage{Type: "text", Payload: "test", RequestID: "err-req"}
	msgBytes, _ := json.Marshal(msg)
	conn.WriteMessage(websocket.TextMessage, msgBytes)
	msgs := readAllMessages(t, conn, 5*time.Second)
	hasError := false
	for _, m := range msgs {
		if strings.Contains(m, `"type":"error"`) {
			hasError = true
		}
	}
	if !hasError {
		t.Error("expected error response")
	}
	// Poll for IDLE — handler may still be completing state transitions
	deadline := time.Now().Add(2 * time.Second)
	for hub.stateMachine.Current() != state.IDLE && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	if hub.stateMachine.Current() != state.IDLE {
		t.Errorf("expected IDLE after error, got %s", hub.stateMachine.Current())
	}
}

func TestVoiceInputStateTransitionSequence(t *testing.T) {
	hub := newTestHub(t)
	srv := httptest.NewServer(http.HandlerFunc(hub.HandleWS))
	defer srv.Close()
	wsURL := "ws" + strings.TrimPrefix(srv.URL, "http")
	conn, _, err := websocket.DefaultDialer.Dial(wsURL, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()
	msg := WSMessage{Type: "text", Payload: "テスト", RequestID: "seq-req"}
	msgBytes, _ := json.Marshal(msg)
	conn.WriteMessage(websocket.TextMessage, msgBytes)
	msgs := readAllMessages(t, conn, 5*time.Second)
	expectedOrder := []string{"LISTENING", "THINKING", "SPEAKING", "IDLE"}
	msgIdx := 0
	for _, expected := range expectedOrder {
		found := false
		for msgIdx < len(msgs) {
			if strings.Contains(msgs[msgIdx], `"state":"`+expected+`"`) {
				found = true
				msgIdx++
				break
			}
			msgIdx++
		}
		if !found {
			t.Errorf("expected state %q in sequence, not found in %d messages", expected, len(msgs))
		}
	}
}

func TestVoiceInputCancelSpeech(t *testing.T) {
	hub := newTestHub(t)
	hub.stateMachine.Transition(state.LISTENING)
	hub.stateMachine.Transition(state.THINKING)
	hub.stateMachine.Transition(state.SPEAKING)
	if hub.stateMachine.Current() != state.SPEAKING {
		t.Fatalf("setup: expected SPEAKING, got %s", hub.stateMachine.Current())
	}
	err := hub.stateMachine.Transition(state.LISTENING)
	if err == nil {
		t.Error("expected error for SPEAKING -> LISTENING transition")
	}
	hub.stateMachine.Reset()
	if hub.stateMachine.Current() != state.IDLE {
		t.Errorf("expected IDLE after reset, got %s", hub.stateMachine.Current())
	}
}

func TestSendTTSSeparatelyWaitsForPlaybackCompletion(t *testing.T) {
	hub := NewWebSocketHub(nil, nil, fakeTTSClient{}, state.New(nil), 5000, nil, nil, nil)
	if err := hub.stateMachine.Transition(state.LISTENING); err != nil {
		t.Fatalf("LISTENING transition: %v", err)
	}
	if err := hub.stateMachine.Transition(state.THINKING); err != nil {
		t.Fatalf("THINKING transition: %v", err)
	}
	if err := hub.stateMachine.Transition(state.SPEAKING); err != nil {
		t.Fatalf("SPEAKING transition: %v", err)
	}

	hub.sendTTSSeparately(nil, "req-tts", "hello")

	if hub.stateMachine.Current() != state.SPEAKING {
		t.Fatalf("expected SPEAKING until playback completion, got %s", hub.stateMachine.Current())
	}
	hub.handleAudioPlaybackFinished(nil, "other-request")
	if hub.stateMachine.Current() != state.SPEAKING {
		t.Fatalf("expected mismatched completion to keep SPEAKING, got %s", hub.stateMachine.Current())
	}
	hub.handleAudioPlaybackFinished(nil, "req-tts")
	if hub.stateMachine.Current() != state.IDLE {
		t.Fatalf("expected IDLE after playback completion, got %s", hub.stateMachine.Current())
	}
}

func TestSendTTSSeparatelyResetsStateWithoutTTSClient(t *testing.T) {
	hub := NewWebSocketHub(nil, nil, nil, state.New(nil), 5000, nil, nil, nil)
	if err := hub.stateMachine.Transition(state.LISTENING); err != nil {
		t.Fatalf("LISTENING transition: %v", err)
	}
	if err := hub.stateMachine.Transition(state.THINKING); err != nil {
		t.Fatalf("THINKING transition: %v", err)
	}
	if err := hub.stateMachine.Transition(state.SPEAKING); err != nil {
		t.Fatalf("SPEAKING transition: %v", err)
	}

	hub.sendTTSSeparately(nil, "req-tts", "hello")

	if hub.stateMachine.Current() != state.IDLE {
		t.Fatalf("expected IDLE without TTS client, got %s", hub.stateMachine.Current())
	}
}

func TestVoiceInputIdleStateAudioChunks(t *testing.T) {
	hub := newTestHub(t)
	if hub.stateMachine.Current() != state.IDLE {
		t.Fatalf("setup: expected IDLE, got %s", hub.stateMachine.Current())
	}
	srv := httptest.NewServer(http.HandlerFunc(hub.HandleWS))
	defer srv.Close()
	wsURL := "ws" + strings.TrimPrefix(srv.URL, "http")
	conn, _, err := websocket.DefaultDialer.Dial(wsURL, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()
	frame := buildAudioChunkFrame("req", 0, 16000, []int16{1, 2, 3})
	conn.WriteMessage(websocket.BinaryMessage, frame)
	msg := WSMessage{Type: "ping", Payload: "pong"}
	msgBytes, _ := json.Marshal(msg)
	conn.WriteMessage(websocket.TextMessage, msgBytes)
	_, respBytes, err := conn.ReadMessage()
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	if !strings.Contains(string(respBytes), "ping_ack") {
		t.Errorf("expected ping_ack, got: %s", string(respBytes))
	}
}
