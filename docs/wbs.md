# WBS

## Policy

Phase 1 is detailed because it is the next implementation target.

Later phases are intentionally coarse. They should be refined when the preceding milestone is close to completion.

## v0.1: Python Conversation Core ✅

### 1. Project Scaffold ✅
- Create Python package layout: done
- Add CLI entry point: done
- Add config file loading: done
- Add development dependency management: done
- Add test runner: done

### 2. Response Schema ✅
- Define response JSON schema: done
- Define allowed emotion values: done
- Define allowed motion values: done
- Define allowed speak_style values: done
- Validate interruptible as boolean: done
- Add schema tests: done

### 3. LLM Provider Interface ✅
- Define provider interface: done
- Implement mock provider: done
- Implement OpenAI-compatible provider: done
- Prepare local provider adapter boundary: done
- Add provider selection from config: done

### 4. Prompt Management ✅
- Define system prompt: done
- Define response format instruction: done
- Define character tone instruction: done
- Add max length guidance: done
- Keep prompts editable outside code: done

### 5. Conversation Flow ✅
- Accept user text: done
- Build prompt context: done
- Call selected LLM provider: done
- Parse response: done
- Validate response JSON: done
- Return normalized response: done

### 6. Recovery and Fallback ✅
- Detect invalid JSON: done
- Try JSON extraction: done
- Try minimal repair: done
- Fall back to safe response: done
- Log raw invalid response: done

### 7. History Management ✅
- Store conversation turns: done
- Limit history passed to LLM: done
- Keep conversation_id: done
- Support new conversation creation: done

### 8. Logging ✅
- Write JSONL logs: done
- Log request_id: done
- Log provider name: done
- Log latency: done
- Log validation result: done
- Avoid logging secrets: done

### 9. Tests ✅
- Test valid response: done
- Test invalid JSON fallback: done
- Test mock provider: done
- Test history trimming: done
- Test config loading: done

### 10. Documentation ✅
- Document how to run CLI: done
- Document config format: done
- Document response schema: done
- Document provider behavior: done

## v0.2: Go Runtime Minimum ✅

- Create Go service scaffold: done
- Add config loading: done
- Add structured logging: done
- Add request_id generation: done
- Add HTTP client for Python service: done
- Add timeout handling: done
- Add cancellation handling: done
- Add health check: done
- Add process boundary documentation: done

## v0.3: Unity Text Connection ✅

- Create Unity project scaffold: done
- Add minimal text input UI: done
- Add Go Runtime client: done
- Send user text to Go: done
- Display response text: done
- Display raw JSON debug view: done
- Receive emotion / motion / speak_style fields: done

## v0.4: TTS Output ✅

- Select initial TTS backend: done (VOICEVOX)
- Add TTS service boundary: done
- Generate audio from response text: done
- Add playback queue: done
- Add stop / interrupt: done
- Sync subtitle with playback state: done

## v0.5: Voice Input ✅

- Select initial STT backend: done (faster-whisper on WinPC)
- Add microphone input: done
- Add VAD: done (energy-based in Go)
- Add speech start detection: done
- Add speech end detection: done
- Display recognized text: done (Go側, Unity側手動)
- Add cancellation before send: done
- Prevent TTS feedback loop: done

## v0.5.x: Agent Loop + Tool Calling (追加) ✅

- agent.Loop 統合（callOllama/webSearch 削除、1本化）: done
- Ollama ネイティブAPI形式対応: done
- tool_calls 先判定 / tool_name 付与: done
- memory.Store 統合（ConversationTurn, SaveTurn, トランザクション）: done
- web_search / web_fetch / audio_control / set_state ツール: done
- SSRF 対策（DNS解決 + IP検査）: done
- セッションID永続化（query param）: done
- セッション単位排他制御（agentMu → wsConnState.mu）: done
- TTS 状態復帰（SPEAKING → IDLE）: done
- Codex 相互レビュー自動化: done
- マージキュー自動化: done

## v0.6: Character Control ⏳

- Select first character display method
- Map emotion to expression
- Map motion to animation
- Add speaking state
- Add idle state
- Add subtitle display
- Add external control API

## Later: Memory, RAG, Autonomous Behavior

- 長期記憶（RAG / ベクトルDB）
- 会話ログ検索
- PC作業状態の取得
- スケジュール・予定管理
- AI側からの自発的な働きかけ
