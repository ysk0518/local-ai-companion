package config

import (
	"encoding/json"
	"os"
)

type RuntimeConfig struct {
	ListenAddr       string `json:"listen_addr"`
	RequestTimeoutMs int    `json:"request_timeout_ms"`
}

// WebSocketConfig は WebSocket 接続の設定です。
type WebSocketConfig struct {
	// AllowedOrigins は許可する Origin のリストです。
	// 空の場合は localhost のみ許可します。
	AllowedOrigins []string `json:"allowed_origins"`
}

type PythonServiceConfig struct {
	BaseURL           string `json:"base_url"`
	Command           string `json:"command"`
	ReadyTimeoutMs    int    `json:"ready_timeout_ms"`
	ShutdownTimeoutMs int    `json:"shutdown_timeout_ms"`
}

type LoggingConfig struct {
	Level string `json:"level"`
}

type TTSConfig struct {
	Enabled     bool   `json:"enabled"`
	VoicevoxURL string `json:"voicevox_url"`
	SpeakerID   int    `json:"speaker_id"`
}

// OllamaConfig は Ollama サーバー接続設定です。
type OllamaConfig struct {
	Enabled   bool   `json:"enabled"`
	BaseURL   string `json:"base_url"`
	Model     string `json:"model"`
	TimeoutMs int    `json:"timeout_ms"`
}

// AgentConfig は AgentLoop の設定です。
type AgentConfig struct {
	Enabled            bool     `json:"enabled"`
	MaxToolLoops       int      `json:"max_tool_loops"`
	SystemPrompt       string   `json:"system_prompt"`
	AllowedTools       []string `json:"allowed_tools"`
	Timezone           string   `json:"timezone"`
	Locale             string   `json:"locale"`
	WebSearchURL       string   `json:"web_search_url"`
	WebSearchAPIKeyEnv string   `json:"web_search_api_key_env"`
}

// VoiceInputConfig は音声入力 (VAD + STT) の設定です。
type VoiceInputConfig struct {
	Enabled    bool   `json:"enabled"`
	VADURL     string `json:"vad_url"`
	STTServerURL string `json:"stt_server_url"`
	STTTimeoutMs int  `json:"stt_timeout_ms"`
}

type Config struct {
	Runtime       RuntimeConfig       `json:"runtime"`
	WebSocket     WebSocketConfig     `json:"websocket"`
	PythonService PythonServiceConfig `json:"python_service"`
	Logging       LoggingConfig       `json:"logging"`
	TTS           TTSConfig           `json:"tts"`
	Ollama        OllamaConfig        `json:"ollama"`
	Agent         AgentConfig         `json:"agent"`
	VoiceInput    VoiceInputConfig    `json:"voice_input"`
}

func Load(path string) (*Config, error) {
	cfg := &Config{
		Runtime: RuntimeConfig{
			ListenAddr:       "127.0.0.1:8080",
			RequestTimeoutMs: 30000,
		},
		PythonService: PythonServiceConfig{
			BaseURL:           "http://127.0.0.1:8090",
			ReadyTimeoutMs:    10000,
			ShutdownTimeoutMs: 5000,
		},
		Logging: LoggingConfig{
			Level: "info",
		},
		TTS: TTSConfig{
			Enabled:     false,
			VoicevoxURL: "http://127.0.0.1:50021",
			SpeakerID:   3,
		},
		Ollama: OllamaConfig{
			Enabled:   false,
			BaseURL:   "http://127.0.0.1:11434",
			Model:     "qwen3:14b",
			TimeoutMs: 60000,
		},
		Agent: AgentConfig{
			Enabled:            false,
			MaxToolLoops:       5,
			SystemPrompt:       "あなたは local-ai-companion です。日本語で応答し、必要に応じてツールを使用してください。",
			AllowedTools:       []string{"web_search", "web_fetch", "audio_control", "set_state"},
			Timezone:           "Asia/Tokyo",
			Locale:             "ja-JP",
			WebSearchURL:       "https://ollama.com/api/web_search",
			WebSearchAPIKeyEnv: "",
		},
		VoiceInput: VoiceInputConfig{
			Enabled:      false,
			VADURL:       "http://127.0.0.1:8092",
			STTServerURL: "http://127.0.0.1:8093/v1/transcribe",
			STTTimeoutMs: 10000,
		},
	}

	if path == "" {
		return cfg, nil
	}

	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	if err := json.Unmarshal(data, cfg); err != nil {
		return nil, err
	}
	return cfg, nil
}
