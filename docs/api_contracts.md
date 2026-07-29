# API Contracts

## Go Runtime エンドポイント一覧

| メソッド | パス | 説明 |
|----------|------|------|
| POST | `/v1/conversation` | 会話リクエストを Python サービスに転送 |
| GET | `/healthz` | ヘルスチェック |
| GET | `/ws` | WebSocket 接続（Upgrade） |

## Assistant Response JSON

ConversationCore が返す assistant response のスキーマ。

```json
{
  "text": "返答本文",
  "emotion": "neutral",
  "motion": "idle",
  "speak_style": "normal",
  "interruptible": true
}
```

## Fields

### text

アシスタントの応答テキスト。

制約:

- 文字列であること
- 空文字列・空白のみは不可（strip 後に 1 文字以上）
- 最大 500 文字（`MAX_TEXT_LENGTH`）。TTS での発話長を考慮した制限

### emotion

許可値（8 種）:

```text
neutral
happy
sad
thinking
surprised
angry
sleepy
confident
```

実装箇所: `src/local_ai_companion/schema.py` → `ALLOWED_EMOTIONS`

### motion

許可値（7 種）:

```text
idle
nod
shake_head
wave
look_away
think
point
```

実装箇所: `src/local_ai_companion/schema.py` → `ALLOWED_MOTIONS`

### speak_style

許可値（6 種）:

```text
normal
soft
fast
slow
serious
playful
```

実装箇所: `src/local_ai_companion/schema.py` → `ALLOWED_SPEAK_STYLES`

### interruptible

発話再生中に割り込み可能かどうか。

制約:

- boolean であること（`True` / `False`）

## Validation

`validate_assistant_response(value)` が以下を実行する:

1. 入力が dict であることを確認
2. 全フィールドの存在確認
3. `text`: 非空文字列かつ `MAX_TEXT_LENGTH` 以下
4. `emotion`: `ALLOWED_EMOTIONS` に含まれる
5. `motion`: `ALLOWED_MOTIONS` に含まれる
6. `speak_style`: `ALLOWED_SPEAK_STYLES` に含まれる
7. `interruptible`: bool 型

違反時は `ResponseValidationError(ValueError)` を送出する。

## Fallback Response

LLM の応答が validation を通過できなかった場合、`fallback_response()` が以下を返す:

```json
{
  "text": "すみません、応答を整えるところで失敗しました。もう一度お願いします。",
  "emotion": "neutral",
  "motion": "idle",
  "speak_style": "soft",
  "interruptible": true
}
```

fallback 自体も `validate_assistant_response()` を通過することをテストで保証する。

## Conversation Request

ConversationCore.send() の入力:

```text
user_text: str          # ユーザーのテキスト入力
conversation_id: str    # 会話セッション識別子（デフォルト "default"）
request_id: str | None  # リクエスト識別子。未指定時は自動生成 (uuid4)
```

## Conversation Response

ConversationCore.send() の戻り値:

```json
{
  "request_id": "uuid",
  "conversation_id": "default",
  "assistant": {
    "text": "受け取りました: ...",
    "emotion": "neutral",
    "motion": "nod",
    "speak_style": "normal",
    "interruptible": true
  }
}
```

## Error Handling

- JSON パース失敗時 → `fallback_response()` を返す
- バリデーション失敗時 → `fallback_response()` を返す
- 内部履歴には `valid: false` と `error` メッセージを記録する

エラーレスポンスに API キーや raw secrets、不要な個人情報を含めないこと。

## Go Runtime: /v1/conversation

**メソッド:** `POST`

**リクエストボディ:**

```json
{
  "message": "ユーザーの入力テキスト",
  "conversation_id": "default",
  "request_id": "32byte-hex-string"
}
```

| フィールド | 型 | 必須 | 説明 |
|------------|-----|------|------|
| `message` | string | **必須** | ユーザーの入力テキスト |
| `conversation_id` | string | 任意 | 会話セッション識別子。未指定時は Python 側のデフォルト |
| `request_id` | string | 任意 | リクエスト識別子。未指定時は 32byte ランダム hex 文字列を自動生成 |

**成功レスポンス (200):**

```json
{
  "request_id": "a1b2c3d4e5f6...",
  "conversation_id": "default",
  "assistant": {
    "text": "こんにちは、何かお手伝いしましょうか？",
    "emotion": "happy",
    "motion": "wave",
    "speak_style": "normal",
    "interruptible": true
  }
}
```

**エラーレスポンス:**

| ステータス | コード | 説明 |
|------------|--------|------|
| 400 | `invalid_request` | JSON パース失敗、または `message` が空 |
| 405 | `method_not_allowed` | POST 以外のメソッド |
| 502 | `python_service_error` | Python サービスのエラー |
| 504 | `timeout` | リクエストタイムアウト（`RequestTimeoutMs` 設定値） |

エラーレスポンスのフォーマット:

```json
{
  "error": {
    "code": "invalid_request",
    "message": "message is required"
  }
}
```

実装箇所: `internal/api/handler.go` → `HandleConversation`

## Go Runtime: /healthz

**メソッド:** `GET`

**レスポンス (200):**

```json
{
  "status": "ok"
}
```

常に 200 を返す。実装箇所: `internal/api/handler.go` → `HandleHealth`

## Go Runtime: Python Service 起動設定

Go Runtime は `python_service.command` が空の場合、`python_service.base_url` で指定された外部 Python AI Service に接続する。
`python_service.command` が指定された場合は Runtime 起動時に子プロセスとして Python AI Service を起動し、`base_url` に HTTP 到達できるまで待ってから Runtime を ready とする。

```json
{
  "python_service": {
    "base_url": "http://127.0.0.1:8090",
    "command": "PYTHONPATH=./src python3 -m local_ai_companion --serve",
    "ready_timeout_ms": 10000,
    "shutdown_timeout_ms": 5000
  }
}
```

| フィールド | 型 | 必須 | 説明 |
|------------|----|:--:|------|
| `base_url` | string | 任意 | Python AI Service の base URL。既定値は `http://127.0.0.1:8090` |
| `command` | string | 任意 | 子プロセスとして起動する shell command。空文字の場合は起動管理しない |
| `ready_timeout_ms` | number | 任意 | 起動後に `base_url` へ到達可能になるまで待つ最大時間。既定値は `10000` |
| `shutdown_timeout_ms` | number | 任意 | Runtime shutdown 時に子プロセス停止を待つ最大時間。既定値は `5000` |

実装箇所:

- `internal/config/config.go` → `PythonServiceConfig`
- `internal/pythonservice/service.go` → `Service`
- `cmd/local-ai-runtime/main.go`

## Go Runtime: /ws — WebSocket

**プロトコル:** WebSocket（`ws://` または `wss://`）。HTTP GET を WebSocket に Upgrade して利用する。

**接続管理:** `WebSocketHub` が `sync.RWMutex` で goroutine-safe に全接続を管理。切断時は自動的にクリーンアップされる。

### WebSocketHub API

| メソッド | シグネチャ | 説明 |
|----------|-----------|------|
| `NewWebSocketHub(pythonClient, stateMachine, requestTimeoutMs)` | `*WebSocketHub` | Hub を生成 |
| `HandleWS(w, r)` | `http.HandlerFunc` | `/ws` エンドポイントのハンドラ |
| `Broadcast(msg)` | `error` | 全接続クライアントにメッセージを一斉送信 |
| `ConnectionCount()` | `int` | 現在の接続数を返す |

### メッセージフォーマット (WSMessage)

```json
{
  "type": "text",
  "payload": "送信内容",
  "request_id": "req-001"
}
```

| フィールド | 型 | 必須 | 説明 |
|------------|-----|------|------|
| `type` | string | **必須** | メッセージ種別。`text` は AI Service 連携、それ以外は ack エコー |
| `payload` | string | **必須** | メッセージ本文 |
| `request_id` | string | 任意 | リクエスト識別子。`json:"request_id,omitempty"` |

**リクエスト例:**

```json
{
  "type": "text",
  "payload": "Hello, Unity!",
  "request_id": "req-001"
}
```

**`type: "text"` のレスポンス:**

`type: "text"` は Python AI Service に転送され、状態遷移通知と AI 応答を WebSocket で返す。

状態遷移通知:

```json
{
  "type": "state_change",
  "state": "LISTENING"
}
```

AI 応答:

```json
{
  "type": "ai_response",
  "request_id": "req-001",
  "conversation_id": "default",
  "assistant": {
    "text": "こんにちは、何かお手伝いしましょうか？",
    "emotion": "happy",
    "motion": "wave",
    "speak_style": "normal",
    "interruptible": true
  }
}
```

エラー時:

```json
{
  "type": "error",
  "request_id": "req-001",
  "error": "python service error"
}
```

状態遷移は通常 `LISTENING` → `THINKING` → `SPEAKING` → `IDLE` の順で通知される。

**非 `text` メッセージのレスポンス（ack）:**

`type: "text"` 以外のメッセージは、後方互換のため `type` に `_ack` を付加してエコーバックする:

```json
{
  "type": "ping_ack",
  "payload": "Hello, Unity!",
  "request_id": "req-001"
}
```

**Broadcast 例:**

```json
{
  "type": "broadcast",
  "payload": "hello all"
}
```

Broadcast は全接続クライアントに同じメッセージを送信する。個別の送信エラーは無視され、他の接続への送信は継続される。

### `audio` メッセージタイプ（v0.4 TTS 連携）

`ai_response` に続いて音声データを送信する。

```json
{
  "type": "audio",
  "request_id": "req-001",
  "format": "wav",
  "sample_rate": 24000,
  "channels": 1,
  "data": "base64_encoded_audio_data",
  "is_last": true
}
```

| フィールド | 型 | 必須 | 説明 |
|------------|-----|------|------|
| `type` | string | **必須** | `"audio"` 固定 |
| `request_id` | string | 任意 | 対応する `ai_response` の `request_id` |
| `format` | string | **必須** | 音声フォーマット。`"wav"` または `"mp3"` |
| `sample_rate` | number | **必須** | サンプルレート（Hz） |
| `channels` | number | **必須** | チャンネル数。モノラルは `1` |
| `data` | string | **必須** | Base64 エンコードされた音声データ |
| `is_last` | boolean | **必須** | 最終チャンクかどうか。分割送信時に使用 |

**注意事項:**

- 不正な JSON を受信した場合、そのメッセージは無視され接続は維持される
- `request_id` が空の場合、レスポンスの `request_id` は空文字列になる（`omitempty` によりキー自体が省略される場合もある）
- 全オリジンを許可（`CheckOrigin: true`）
- 同時接続は goroutine-safe に管理される
- 同一 WebSocket 接続への書き込みは接続ごとの mutex で直列化される
- `StateMachine` の `Current` / `Transition` / `Reset` は goroutine-safe
- WebSocket の会話フロー全体は `WebSocketHub` 側でも直列化される
- `ai_response` の直後に `audio` メッセージが続く。`audio` がない場合は音声なし応答とみなす

実装箇所: `internal/api/websocket.go` → `HandleWS` / `WebSocketHub`

## TTS (Text-to-Speech) Integration

v0.4 で導入された TTS 連携の API 仕様。

### WebSocket `audio` メッセージ

`ai_response` に続いて、音声データを `audio` タイプでクライアントに送信する。

```json
{
  "type": "audio",
  "request_id": "req-001",
  "conversation_id": "default",
  "payload": "<base64-encoded WAV audio>",
  "format": "wav",
  "sample_rate": 24000
}
```

| フィールド | 型 | 必須 | 説明 |
|------------|-----|:--:|------|
| `type` | string | **必須** | `"audio"` 固定 |
| `request_id` | string | 任意 | 対応する `ai_response` の request_id |
| `conversation_id` | string | 任意 | 会話セッション識別子 |
| `payload` | string | **必須** | base64 エンコードされた音声データ |
| `format` | string | **必須** | 音声フォーマット。`"wav"` または `"mp3"` |
| `sample_rate` | number | 任意 | サンプルレート（Hz）。既定値 24000 |

### TTS フロー

```text
1. クライアントが type: "text" を WebSocket で送信
2. Go Runtime → Python AI Service が応答 JSON を生成
3. Go Runtime が type: "ai_response" を WebSocket で送信
4. Go Runtime が Python AI Service に TTS 生成をリクエスト
5. Go Runtime が type: "audio" を WebSocket で送信（base64 エンコード音声）
6. クライアント（Unity）が audio データを受信し、再生
7. Unity が再生完了後に `audio_playback_finished` を送信
8. Go Runtime が `state_change: IDLE` を送信
```

ai_response と audio の連続送信は同一 WebSocket 接続上で行われ、request_id で紐付けられる。

### `audio_playback_finished` メッセージ（Unity → Go Runtime）

Unity は `audio` メッセージで受け取った音声キューをすべて再生した後、受信元と同じ WebSocket 接続へ送信する。

```json
{
  "type": "audio_playback_finished",
  "request_id": "550e8400-e29b-41d4-a716-446655440000"
}
```

Go Runtime は request_id と接続が一致する通知を受けた場合だけ `SPEAKING` から `IDLE` へ遷移する。接続が切断された場合も `IDLE` へ戻す。

### Unity 受信仕様

- `ai_response` 受信後、字幕・表情・モーションを先に表示する
- `audio` メッセージ受信時に音声データをデコード・再生する
- 音声再生は `interruptible` フラグに従い、`true` の場合はユーザー入力で割り込み可能
- 再生完了後、IDLE 状態に遷移する

### TTS キャンセル制御

- TTS 生成中に新しい `type: "text"` を受信した場合、前の生成をキャンセルする
- TTS 再生中に割り込みが発生した場合、`interruptible: true` であれば再生を停止し新しいリクエストの処理を開始する
- キャンセル制御は Go Runtime が `context.Context` で管理する

## StateMachine API

会話状態を管理するステートマシン。許可された遷移のみを実行し、遷移時にコールバックを発火する。`Current` / `Transition` / `Reset` は goroutine-safe。

実装箇所: `internal/state/state.go`

### 状態一覧

| 定数 | 値 | 日本語ラベル | 説明 |
|------|-----|-------------|------|
| `IDLE` | 0 | 待機中 | 初期状態。会話待ち |
| `LISTENING` | 1 | 受信中 | ユーザー音声受信中 |
| `THINKING` | 2 | 思考中 | 応答生成中 |
| `SPEAKING` | 3 | 発話中 | 音声出力中 |

`State.String()` で日本語ラベルを取得可能。未定義値は `不明(N)` を返す。

### 状態遷移図

```
         ┌──────────────────┐
         │                  │
         ▼                  │
     ┌──────┐  cancel   ┌──┴───┐
     │ IDLE │◄──────────│LISTEN│
     └──┬───┘           └──┬───┘
        │                  │
        │ start            │ recognized
        │                  │
        ▼                  ▼
     ┌──────┐           ┌──────┐
     │LISTEN│           │THINK │
     └──────┘           └──┬───┘
                           │
                 cancel    │ generated
                    │      │
                    ▼      ▼
                 ┌──────┐┌──────┐
                 │ IDLE ││SPEAK │
                 └──────┘└──┬───┘
                            │
                            │ done
                            │
                            ▼
                         ┌──────┐
                         │ IDLE │
                         └──────┘
```

### 有効な遷移

| from | to | 説明 |
|------|-----|------|
| `IDLE` | `LISTENING` | ユーザー発話の聞き取り開始 |
| `LISTENING` | `THINKING` | 音声認識完了、応答生成開始 |
| `LISTENING` | `IDLE` | キャンセル（聞き取り中断） |
| `THINKING` | `SPEAKING` | 応答生成完了、発話開始 |
| `THINKING` | `IDLE` | キャンセル（生成中断） |
| `SPEAKING` | `IDLE` | 発話完了 |

同一状態への遷移（例: `IDLE` → `IDLE`）はエラーにならず、コールバックも発火しない（no-op）。

### 無効な遷移

以下の遷移はエラー（`invalid transition: X → Y`）になる:

- `IDLE` → `THINKING`, `SPEAKING`
- `LISTENING` → `SPEAKING`
- `THINKING` → `LISTENING`
- `SPEAKING` → `LISTENING`, `THINKING`

### API

```go
// 型
type State int  // iota: IDLE=0, LISTENING=1, THINKING=2, SPEAKING=3
type StateChangeCallback func(from, to State)
type StateMachine struct { /* unexported fields */ }

// コンストラクタ
func New(callback StateChangeCallback) *StateMachine

// メソッド
func (sm *StateMachine) Current() State
func (sm *StateMachine) Transition(to State) error
func (sm *StateMachine) Reset()
```

#### `New(callback) *StateMachine`

初期状態 `IDLE` で StateMachine を生成する。`callback` が非 nil の場合、有効な遷移時に `callback(from, to)` が呼ばれる。

#### `Current() State`

現在の状態を返す。複数 goroutine から同時に呼び出しても安全。

#### `Transition(to State) error`

`to` への遷移を試みる。状態の検証と更新は内部 mutex で保護される。

- 同一状態 → no-op（nil を返す、コールバック発火なし）
- 無効な遷移 → `fmt.Errorf("invalid transition: %s → %s", from, to)` を返す
- 有効な遷移 → 状態を更新し、`onChange` コールバックがあれば発火

コールバックは状態更新後、内部 mutex を解放してから呼び出される。コールバック内で `Current()` などの StateMachine メソッドを呼び出してもデッドロックしない。

#### `Reset()`

現在の状態にかかわらず `IDLE` に強制リセットする。実際に状態が変わった場合のみコールバックを発火する（`IDLE` からのリセットは no-op）。状態の読み取りと更新は内部 mutex で保護され、コールバックは mutex 解放後に呼び出される。

### コールバックの挙動

| 操作 | コールバック発火 | 備考 |
|------|:--:|------|
| 有効な遷移 (`IDLE`→`LISTENING`) | ✅ | `from=IDLE, to=LISTENING` |
| 同一状態遷移 (`IDLE`→`IDLE`) | ❌ | no-op |
| 無効な遷移 (`IDLE`→`THINKING`) | ❌ | エラーを返す |
| `Reset()` (非 IDLE から) | ✅ | `from=現在状態, to=IDLE` |
| `Reset()` (IDLE から) | ❌ | no-op |

実装箇所: `internal/state/state.go`

## TTS（v0.4）

v0.4 で導入される TTS（Text-to-Speech）の API 契約。

### 概要

Python AI Service が `assistant.text` から音声データを生成し、Go Runtime が WebSocket 経由で Unity クライアントに配送する。TTS 生成自体は Python AI Service の責務であり、Unity は音声再生のみを担当する。

### データフロー

```text
Python AI Service
  ↓ assistant response + audio data
Go Runtime
  ↓ WebSocket: ai_response → audio
Unity
  ↓ 音声再生
```

### WebSocket シーケンス

1. Unity が `{"type":"text", ...}` を送信
2. Runtime が `{"type":"state_change","state":"LISTENING"}` を返す
3. Runtime が `{"type":"state_change","state":"THINKING"}` を返す
4. Runtime が `{"type":"ai_response", ...}` を返す
5. Runtime が `{"type":"state_change","state":"SPEAKING"}` を返す
6. Runtime が `{"type":"audio", ...}` を返す（音声データ）
7. Unity が `{"type":"audio_playback_finished", ...}` を返す
8. Runtime が `{"type":"state_change","state":"IDLE"}` を返す

`audio` メッセージは `ai_response` の直後、`SPEAKING` 状態遷移の後に送信される。`ai_response` に続いて `audio` が送信されない場合、音声なし応答（テキストのみ）とみなす。

### 音声フォーマット

| 設定 | 値 |
|------|-----|
| フォーマット | WAV（PCM 16bit）または MP3 |
| サンプルレート | 24000 Hz（VOICEVOX デフォルト） |
| チャンネル | 1（モノラル） |
| エンコード | Base64 |

### Unity 側の受信仕様

Unity は `ai_response` 受信後、続く `audio` メッセージを受信して音声再生を開始する。

- `ai_response` の `assistant.text` を字幕表示
- 続く `audio` メッセージの Base64 データをデコードし、`AudioSource` で再生
- `interruptible: true` の場合、ユーザー入力で再生を中断可能
- `speak_style` に応じて再生速度・ピッチを調整可能（将来の拡張）

実装箇所（将来）: `unity/v0.4-tts-output/Assets/Scripts/AudioPresenter.cs`

### キャンセル

TTS 生成中・再生中にキャンセルが発生した場合:

- Go Runtime が TTS 生成をキャンセルする
- Unity は再生中の音声を停止し、キューをクリアする
- キャンセル後は `state_change: IDLE` が送信される


## Voice Input（v0.5）

v0.5 で導入される音声入力（Voice Input）の API 契約。

### 概要

Unity からマイク音声を PCM チャンク単位でストリーミングし、Go Runtime が VAD と STT をパイプラインで処理する。Unity は音声送信と認識結果の表示のみを担当する。

### データフロー

```text
Unity（マイク入力）
  ↓ WebSocket: audio_chunk (binary, 100ms PCM)
Go Runtime
  ↓ HTTP POST: /vad/chunk (octet-stream)
Python VAD Service (:8092)
  ↓ VAD イベント（speech_start / speech_end / idle）
Go Runtime
  ↓ VAD 終了後、WAV に結合
  ↓ HTTP POST: /v1/transcribe (multipart/form-data)
WinPC faster-whisper (:8093)
  ↓ 認識テキスト
Go Runtime
  ↓ WebSocket: vad_event → speech_recognized
Unity（認識結果表示）
```

### WebSocket メッセージ仕様

#### audio_chunk（Unity → Go）

**種別:** バイナリフレーム

音声データを 100ms 単位のチャンクに分割し、バイナリフレームとしてストリーミング送信する。

| オフセット | サイズ | 型 | フィールド | 説明 |
|-----------|--------|-----|-----------|------|
| 0 | 16 bytes | string | `request_id` | リクエスト識別子（null 終端 UTF-8） |
| 16 | 4 bytes | uint32 | `seq` | チャンクのシーケンス番号（0 始まり） |
| 20 | 2 bytes | uint16 | `sample_rate` | サンプルレート（Hz）。既定値 16000 |
| 22 | N bytes | int16[] | `samples` | PCM 16bit リトルエンディアンのサンプル配列 |

**制約:**

- 1 チャンクは 100ms 相当のサンプル数（16000 Hz の場合 1600 サンプル）
- `seq` は連続した整数。ギャップがある場合はエラー
- `seq` が 0 のチャンクで新しい発話セッションが開始される
- バイナリフレームの先頭 22 bytes がヘッダ、残りが PCM データ

**実装箇所（将来）:** `internal/api/websocket.go` → `HandleWS` 内のバイナリフレーム処理

#### vad_event（Go → Unity）

VAD（Voice Activity Detection）の判定結果を通知する。

```json
{
  "type": "vad_event",
  "request_id": "req-001",
  "event": "speech_start"
}
```

| フィールド | 型 | 必須 | 説明 |
|------------|-----|:--:|------|
| `type` | string | **必須** | `"vad_event"` 固定 |
| `request_id` | string | **必須** | `audio_chunk` ヘッダの `request_id` |
| `event` | string | **必須** | `"speech_start"` または `"speech_end"` |

**イベント:**

| event | 説明 |
|-------|------|
| `"speech_start"` | 音声区間の開始を検出 |
| `"speech_end"` | 音声区間の終了を検出。この後に STT が実行される |

**タイミング:**

- `speech_start` は最初の有効な音声チャンク受信時に送信
- `speech_end` は VAD が無音区間を検出した時点で送信

**実装箇所（将来）:** `internal/api/websocket.go` → VAD イベントハンドリング

#### speech_recognized（Go → Unity）

STT の認識結果を通知する。ユーザーは認識結果をキャンセル可能。

```json
{
  "type": "speech_recognized",
  "request_id": "req-001",
  "text": "こんにちは、元気ですか？",
  "cancelable": true
}
```

| フィールド | 型 | 必須 | 説明 |
|------------|-----|:--:|------|
| `type` | string | **必須** | `"speech_recognized"` 固定 |
| `request_id` | string | **必須** | 対応する `audio_chunk` の `request_id` |
| `text` | string | **必須** | STT による認識テキスト |
| `cancelable` | boolean | **必須** | 認識結果をキャンセル可能かどうか |

**制約:**

- `text` は空文字列不可（STT が無音または認識失敗の場合は送信しない）
- `cancelable: true` の場合、Unity は一定時間（例: 3 秒）キャンセル UI を表示する
- `cancelable: false` の場合、認識結果は確定済みで即座に AI 処理へ進む

**実装箇所（将来）:** `internal/api/websocket.go` → STT 結果の処理

#### cancel_speech（Unity → Go）

認識結果の確定前に、ユーザーがキャンセルを指示する。

```json
{
  "type": "cancel_speech",
  "request_id": "req-001"
}
```

| フィールド | 型 | 必須 | 説明 |
|------------|-----|:--:|------|
| `type` | string | **必須** | `"cancel_speech"` 固定 |
| `request_id` | string | **必須** | キャンセル対象の `request_id` |

**挙動:**

- Go Runtime が `request_id` に対応する STT 処理をキャンセルする
- キャンセル後、Go Runtime は `state_change: IDLE` を送信する
- 既に AI 処理が開始されている場合はキャンセル不可（エラー応答）

**エラー応答（キャンセル失敗時）:**

```json
{
  "type": "error",
  "request_id": "req-001",
  "error": "speech already processing, cannot cancel"
}
```

### WebSocket シーケンス（音声入力フロー）

```text
1. Unity → Go:  audio_chunk (binary, seq=0)  ── 発話開始
2. Go → Unity:  vad_event (speech_start)
3. Unity → Go:  audio_chunk (seq=1..N-1)     ── 継続
4. Go → Unity:  vad_event (speech_end)        ── 発話終了検出
5. Go → Python: POST /vad/chunk (蓄積した PCM)
6. Python → Go: {"event":"idle"} → 不要なチャンク破棄
7. Go → WinPC:  POST /v1/transcribe (WAV)     ── STT 実行
8. WinPC → Go:  {"text":"...", ...}
9. Go → Unity:  speech_recognized              ── 認識結果
10. Unity がキャンセル可能期間（cancelable: true の場合）
11. Unity が確定 or キャンセル
```

### HTTP API 仕様

#### Go → Python VAD /vad/chunk

**メソッド:** `POST`

**URL:** `http://127.0.0.1:8092/vad/chunk`

**Content-Type:** `application/octet-stream`

**リクエストボディ:** PCM int16 サンプル（リトルエンディアン、ヘッダなし raw PCM）

**レスポンス (200):**

```json
{
  "event": "speech_start"
}
```

**レスポンス（無音継続時）:**

```json
{
  "event": "idle"
}
```

| フィールド | 型 | 必須 | 説明 |
|------------|-----|:--:|------|
| `event` | string | **必須** | VAD 判定結果 |

**event 一覧:**

| event | 説明 |
|-------|------|
| `"speech_start"` | 音声区間の開始 |
| `"speech_end"` | 音声区間の終了 |
| `"idle"` | 無音区間（変化なし） |

**制約:**

- リクエストボディは 100ms 単位の PCM チャンク（16000 Hz の場合 3200 bytes）
- Python VAD Service は Silero VAD を使用
- 連続したチャンクの状態を内部で管理し、状態変化時にのみ `speech_start` / `speech_end` を返す

**エラーレスポンス (400):**

```json
{
  "error": "invalid chunk size: expected 3200 bytes, got 1600"
}
```

**エラーレスポンス (500):**

```json
{
  "error": "VAD model not loaded"
}
```

**実装箇所:** `src/local_ai_companion/server.py` (`/vad/chunk` エンドポイント)

#### Go Runtime → WinPC faster-whisper /v1/transcribe

**メソッド:** `POST`

**URL:** `http://192.168.12.107:8093/v1/transcribe`

**Content-Type:** `multipart/form-data`

**リクエストパラメータ:**

| パラメータ | 型 | 必須 | 説明 |
|-----------|-----|:--:|------|
| `file` | file | **必須** | WAV ファイル（PCM 16bit、モノラル、16000 Hz） |
| `language` | string | 任意 | 言語コード。既定値 `"ja"` |

**成功レスポンス (200):**

```json
{
  "text": "こんにちは、元気ですか？",
  "duration": 3.2,
  "error": null
}
```

| フィールド | 型 | 説明 |
|------------|-----|------|
| `text` | string | 認識テキスト。空文字列の場合は認識失敗 |
| `duration` | number | 音声の長さ（秒） |
| `error` | string\|null | エラー文言。正常時は `null` |

**エラーレスポンス (400):**

```json
{
  "text": "",
  "duration": 0,
  "error": "no audio data in file"
}
```

**エラーレスポンス (500):**

```json
{
  "text": "",
  "duration": 0,
  "error": "STT model not loaded"
}
```

**エラーレスポンス (503):**

```json
{
  "text": "",
  "duration": 0,
  "error": "STT service unavailable"
}
```

**制約:**

- 音声ファイルは WAV（PCM 16bit、モノラル、16000 Hz）
- `language` 未指定時は `"ja"`（日本語）
- STT モデル: faster-whisper small（CUDA、WinPC）
- タイムアウト: 10 秒（Go Runtime 側で管理）

**実装箇所:** Go Runtime `internal/stt/client.go`

### エラーレスポンス形式（統一）

v0.5 の全エンドポイントで使用するエラーレスポンスの基本形式:

```json
{
  "error": "エラーメッセージ"
}
```

WebSocket エラーの場合:

```json
{
  "type": "error",
  "request_id": "req-001",
  "error": "エラーメッセージ"
}
```

| フィールド | 型 | 説明 |
|------------|-----|------|
| `type` | string | WebSocket の場合 `"error"` 固定 |
| `request_id` | string | 対応するリクエストの `request_id`（追跡可能な場合） |
| `error` | string | 人間が読めるエラーメッセージ |

### キャンセル制御

音声入力パイプラインのキャンセルは Go Runtime が `context.Context` で管理する。

| キャンセル契機 | 動作 |
|---------------|------|
| Unity が `cancel_speech` 送信 | STT 処理をキャンセル、`state_change: IDLE` |
| 新しい `audio_chunk` (seq=0) 受信 | 前のセッションをキャンセル、新セッション開始 |
| WebSocket 切断 | 全処理をキャンセル、リソース解放 |

キャンセル後は必ず `state_change: IDLE` が送信され、Unity は待機状態に戻る。
