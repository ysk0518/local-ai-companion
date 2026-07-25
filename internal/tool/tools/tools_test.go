package tools

import (
	"encoding/json"
	"testing"
)

func TestWebSearch_MissingQuery(t *testing.T) {
	ws := NewWebSearch("", "")
	_, err := ws.Execute(json.RawMessage(`{}`))
	if err == nil {
		t.Fatal("expected error for missing query")
	}
}

func TestWebSearch_MalformedArgs(t *testing.T) {
	ws := NewWebSearch("", "")
	_, err := ws.Execute(json.RawMessage(`{invalid}`))
	if err == nil {
		t.Fatal("expected error for malformed args")
	}
}

func TestWebFetch_MissingURL(t *testing.T) {
	wf := NewWebFetch()
	_, err := wf.Execute(json.RawMessage(`{}`))
	if err == nil {
		t.Fatal("expected error for missing url")
	}
}

func TestWebFetch_PrivateURL(t *testing.T) {
	wf := NewWebFetch()
	_, err := wf.Execute(json.RawMessage(`{"url":"http://localhost:8080/test"}`))
	if err == nil {
		t.Fatal("expected error for private URL")
	}
}

func TestWebFetch_InternalIPs(t *testing.T) {
	wf := NewWebFetch()
	privateURLs := []string{
		"http://127.0.0.1/test", "http://192.168.1.1/test",
		"http://10.0.0.1/test", "http://172.16.0.1/test",
	}
	for _, url := range privateURLs {
		body, _ := json.Marshal(map[string]string{"url": url})
		_, err := wf.Execute(body)
		if err == nil {
			t.Errorf("expected error for %s", url)
		}
	}
}

func TestAudioControl_MissingAction(t *testing.T) {
	ac := NewAudioControl(nil)
	_, err := ac.Execute(json.RawMessage(`{}`))
	if err == nil {
		t.Fatal("expected error for missing action")
	}
}

func TestAudioControl_InvalidAction(t *testing.T) {
	ac := NewAudioControl(nil)
	_, err := ac.Execute(json.RawMessage(`{"action":"destroy"}`))
	if err == nil {
		t.Fatal("expected error for invalid action")
	}
}

func TestAudioControl_ValidActions(t *testing.T) {
	called := ""
	cb := func(action string) (string, error) { called = action; return "done: " + action, nil }
	ac := NewAudioControl(cb)
	for _, a := range []string{"stop", "clear_queue"} {
		called = ""
		body, _ := json.Marshal(map[string]string{"action": a})
		result, err := ac.Execute(body)
		if err != nil {
			t.Errorf("unexpected error for %s: %v", a, err)
		}
		if called != a {
			t.Errorf("expected callback with %s, got %s", a, called)
		}
		if result != "done: "+a {
			t.Errorf("expected 'done: %s', got %s", a, result)
		}
	}
}

func TestAudioControl_NoCallback(t *testing.T) {
	ac := NewAudioControl(nil)
	result, err := ac.Execute(json.RawMessage(`{"action":"stop"}`))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if result != "audio stop acknowledged (no-op)" {
		t.Errorf("expected no-op message, got %s", result)
	}
}

func TestSetState_MissingState(t *testing.T) {
	ss := NewSetState(nil)
	_, err := ss.Execute(json.RawMessage(`{}`))
	if err == nil {
		t.Fatal("expected error for missing state")
	}
}

func TestSetState_InvalidState(t *testing.T) {
	ss := NewSetState(nil)
	_, err := ss.Execute(json.RawMessage(`{"state":"exploding"}`))
	if err == nil {
		t.Fatal("expected error for invalid state")
	}
}

func TestSetState_ValidStates(t *testing.T) {
	lastState := ""
	ss := NewSetState(func(state string) error { lastState = state; return nil })
	for _, s := range []string{"IDLE", "LISTENING", "THINKING", "SPEAKING"} {
		lastState = ""
		body, _ := json.Marshal(map[string]string{"state": s})
		result, err := ss.Execute(body)
		if err != nil {
			t.Errorf("unexpected error for %s: %v", s, err)
		}
		if lastState != s {
			t.Errorf("expected callback with %s, got %s", s, lastState)
		}
		if result != "state set to "+s {
			t.Errorf("expected 'state set to %s', got %s", s, result)
		}
	}
}

func TestSetState_NoCallback(t *testing.T) {
	ss := NewSetState(nil)
	result, err := ss.Execute(json.RawMessage(`{"state":"IDLE"}`))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if result != "state set to IDLE" {
		t.Errorf("expected 'state set to IDLE', got %s", result)
	}
}

func TestValidatePublicURL(t *testing.T) {
	tests := []struct {
		url     string
		blocked bool
	}{
		{"http://localhost/test", true},
		{"http://127.0.0.1:8080/test", true},
		{"http://192.168.1.1/test", true},
		{"http://10.0.0.1/test", true},
		{"http://172.16.0.1/test", true},
		{"http://[::1]/test", true},
		{"https://example.com", false},
		{"https://google.com", false},
	}
	for _, tt := range tests {
		err := validatePublicURL(tt.url)
		blocked := err != nil
		if blocked != tt.blocked {
			t.Errorf("validatePublicURL(%q) blocked=%v, want %v (err=%v)", tt.url, blocked, tt.blocked, err)
		}
	}
}
