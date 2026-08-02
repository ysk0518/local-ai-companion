using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace AICompanion
{
    /// <summary>
    /// Unity v0.3 テキスト接続 UI マネージャ。
    /// InputField + Button + ScrollView の制御および
    /// WebSocketClient とのやり取りを担当する。
    /// </summary>
    [Serializable]
    public class MessageJson
    {
        public string type;
        public string payload;
        public string request_id;
    }

    [Serializable]
    public class ConversationRequestJson
    {
        public string message;
        public string request_id;
    }

    [Serializable]
    public class AiResponseJson
    {
        public string type;
        public string request_id;
        public string conversation_id;
        public AssistantJson assistant;
        public string text;
        public string audio;
        public string error;
    }

    [Serializable]
    public class AssistantJson
    {
        public string text;
        public string emotion;
        public string motion;
        public string speak_style;
        public bool interruptible;
    }

    [Serializable]
    public class StateChangeJson
    {
        public string type;
        public string state;
    }

    [Serializable]
    public class ErrorJson
    {
        public string type;
        public string request_id;
        public string error;
    }

    [Serializable]
    public class AudioMessageJson
    {
        public string type;
        public string request_id;
        public string data;
        public string audio;
        public string audio_base64;
        public string format;
        public string mime_type;
    }

    [Serializable]
    public class AudioControlJson
    {
        public string type;
        public string request_id;
        public string action;
    }

    [Serializable]
    public class AudioPlaybackFinishedJson
    {
        public string type;
        public string request_id;
    }

    [Serializable]
    public class SpeechRecognizedJson
    {
        public string type;
        public string request_id;
        public string text;
        public bool cancelable;
    }

    [Serializable]
    public class VADEventJson
    {
        public string type;
        public string request_id;
        public string event_name;
        public string @event;
    }

    public class UIManager : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private InputField _inputField;
        [SerializeField] private Button _sendButton;
        [SerializeField] private Button _micButton;
        [SerializeField] private Dropdown _micDeviceDropdown;
        [SerializeField] private Slider _micGainSlider;
        [SerializeField] private Button _cancelSpeechButton;
        [SerializeField] private Text _responseText;
        [SerializeField] private ScrollRect _scrollRect;
        [SerializeField] private Text _statusText;
        [SerializeField] private Text _titleText;
        [SerializeField] private Text _micText;
        [SerializeField] private Text _speechText;

        [Header("Settings")]
        [SerializeField] private string _wsUrl = "ws://192.168.12.112:8090/ws";
        [SerializeField] private string _voiceWsUrl = "";
        [SerializeField] private bool _useSeparateVoiceWebSocket = true;
        [SerializeField] private string _httpFallbackUrl = "http://192.168.12.112:8090/v1/conversation";
        [SerializeField] private bool _enableHttpFallback;
        [SerializeField] private int _maxResponseLines = 200;

        [Header("Audio")]
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private int _maxAudioQueueSize = 8;

        [Header("Mic Input")]
        [SerializeField] private bool _autoStartMic = true;
        [SerializeField] private int _micSampleRate = 16000;
        [SerializeField] private int _micChunkMs = 100;
        [SerializeField] private int _micBufferSeconds = 1;
        [SerializeField, Range(0.1f, 8f)] private float _micGain = 1f;

        private WebSocketClient _wsClient;
        private WebSocketClient _voiceWsClient;
        private const float ResponseScrollPadding = 16f;
        private const string SessionIdPlayerPrefsKey = "local_ai_companion.session_id";
        private readonly List<string> _responseLines = new();
        private readonly Queue<AudioClip> _audioQueue = new();
        private string _currentState = "UNKNOWN";
        private string _sessionId;
        private string _pendingSpeechRequestId;
        private string _playingAudioRequestId;
        private string _voiceRequestId;
        private string _selectedMicDevice;
        private string _lastVoiceError;
        private WebSocketClient _playingAudioClient;
        private bool _httpFallbackMode;
        private bool _micStreaming;
        private bool _updatingMicDeviceDropdown;
        private uint _voiceSeq;
        private uint _micChunksSent;
        private uint _micSendFailures;
        private int _micReadPosition;
        private float _lastMicSendTime;
        private float _lastMicDebugLogTime;
        private float _micLevel;
        private AudioClip _micClip;
        private Coroutine _audioPlaybackCoroutine;
        private Coroutine _speechCancelWindowCoroutine;
        private Coroutine _micStreamingCoroutine;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<UIManager>() != null)
                return;

            var go = new GameObject("AICompanionUI");
            go.AddComponent<UIManager>();
        }

        private void Start()
        {
            EnsureSceneCamera();
            EnsureUIReferences();

            // タイトル
            if (_titleText != null)
                _titleText.text = "AI Companion v0.3";

            if (string.IsNullOrEmpty(_wsUrl))
            {
                _httpFallbackMode = _enableHttpFallback && !string.IsNullOrEmpty(_httpFallbackUrl);
                UpdateStatus(_httpFallbackMode ? "HTTP fallback" : "WebSocket URL missing");
            }
            else
            {
                // WebSocket クライアント初期化
                _sessionId = GetOrCreateSessionId();
                var controlWsUrl = AppendSessionIdToWebSocketUrl(_wsUrl, _sessionId);
                var voiceWsUrl = AppendSessionIdToWebSocketUrl(FirstNonEmpty(_voiceWsUrl, _wsUrl), _sessionId);
                Debug.Log($"[UI] session_id={_sessionId}");

                _wsClient = new WebSocketClient(controlWsUrl, "control");
                _wsClient.OnConnected += HandleConnected;
                _wsClient.OnDisconnected += HandleDisconnected;
                _wsClient.OnMessageReceived += HandleMessageReceived;
                _wsClient.OnError += HandleError;

                if (_useSeparateVoiceWebSocket)
                {
                    _voiceWsClient = new WebSocketClient(voiceWsUrl, "voice");
                    _voiceWsClient.OnConnected += HandleVoiceConnected;
                    _voiceWsClient.OnDisconnected += HandleVoiceDisconnected;
                    _voiceWsClient.OnMessageReceived += HandleVoiceMessageReceived;
                    _voiceWsClient.OnError += HandleVoiceError;
                }
            }

            // 送信ボタン
            if (_sendButton != null)
                _sendButton.onClick.AddListener(OnSendClicked);
            if (_micButton != null)
                _micButton.onClick.AddListener(OnMicClicked);
            if (_micDeviceDropdown != null)
                _micDeviceDropdown.onValueChanged.AddListener(OnMicDeviceSelected);
            if (_micGainSlider != null)
            {
                _micGainSlider.value = _micGain;
                _micGainSlider.onValueChanged.AddListener(OnMicGainChanged);
            }
            if (_cancelSpeechButton != null)
                _cancelSpeechButton.onClick.AddListener(OnCancelSpeechClicked);
            UpdateMicDeviceDropdownOptions();

            // Enter キーでも送信
            if (_inputField != null)
                _inputField.onEndEdit.AddListener(OnInputEndEdit);

            if (_wsClient != null)
            {
                // 自動接続
                _ = _wsClient.ConnectAsync();
                UpdateStatus("接続中...");
            }
            if (_voiceWsClient != null)
                _ = _voiceWsClient.ConnectAsync();
        }

        private void OnDestroy()
        {
            StopMicStreaming();
            _ = _wsClient?.DisconnectAsync();
            _wsClient?.Dispose();
            _ = _voiceWsClient?.DisconnectAsync();
            _voiceWsClient?.Dispose();
            StopAudioPlayback();
            StopSpeechCancelWindow();
        }

        /// <summary>
        /// 送信ボタン押下時の処理。
        /// </summary>
        public void OnSendClicked()
        {
            SendMessage();
        }

        public void OnMicClicked()
        {
            if (_micStreaming)
                StopMicStreaming();
            else
                StartMicStreaming();
        }

        public void OnMicDeviceSelected(int index)
        {
            if (_updatingMicDeviceDropdown)
                return;

            var devices = Microphone.devices;
            if (devices == null || index < 0 || index >= devices.Length)
            {
                UpdateMicDeviceDropdownOptions();
                return;
            }

            var nextDevice = devices[index];
            if (_selectedMicDevice == nextDevice)
                return;

            var wasStreaming = _micStreaming;
            if (wasStreaming)
                StopMicStreaming();

            _selectedMicDevice = nextDevice;
            UpdateMicStatusText();
            AppendResponse($"<color=#aaaaaa>[Mic] device: {_selectedMicDevice}</color>");

            if (wasStreaming)
                StartMicStreaming();
        }

        public void OnMicGainChanged(float value)
        {
            _micGain = Mathf.Clamp(value, 0.1f, 8f);
            UpdateMicStatusText();
        }

        public async void OnCancelSpeechClicked()
        {
            if (string.IsNullOrEmpty(_pendingSpeechRequestId))
                return;

            var requestId = _pendingSpeechRequestId;
            ClearPendingSpeech();

            if (_wsClient == null || !_wsClient.IsConnected)
            {
                AppendResponse("<color=#ff6666>[Speech] cannot cancel: WebSocket is not connected</color>");
                return;
            }

            var json = JsonUtility.ToJson(new MessageJson
            {
                type = "cancel_speech",
                request_id = requestId
            });
            await _wsClient.SendAsync(json);
            AppendResponse("<color=#ffcc66>[Speech] cancelled</color>");
            UpdateStatus("Speech cancelled");
        }

        /// <summary>
        /// InputField で Enter キーが押された時の処理。
        /// </summary>
        private void OnInputEndEdit(string text)
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                SendMessage();
            }
        }

        private async void SendMessage()
        {
            if (_inputField == null) return;

            var text = _inputField.text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // 送信中は無効化
            SetSendingState(true);

            // ユーザー入力を表示
            AppendResponse($"<color=#888888>You:</color> {text}");

            var requestId = Guid.NewGuid().ToString();
            var json = JsonUtility.ToJson(new MessageJson
            {
                type = "text",
                payload = text,
                request_id = requestId
            });

            if (_httpFallbackMode)
            {
                StartCoroutine(SendHttpFallback(text, requestId));
            }
            else if (_wsClient == null || !_wsClient.IsConnected)
            {
                AppendResponse("<color=#ff6666>[Error] WebSocket is not connected</color>");
                UpdateStatus("WebSocket disconnected");
                SetSendingState(false);
            }
            else
            {
                await _wsClient.SendAsync(json);
            }

            // 入力フィールドをクリア
            _inputField.text = "";
            _inputField.ActivateInputField();
        }

        private IEnumerator SendHttpFallback(string text, string requestId)
        {
            var json = JsonUtility.ToJson(new ConversationRequestJson
            {
                message = text,
                request_id = requestId
            });
            var body = Encoding.UTF8.GetBytes(json);

            using (var req = new UnityWebRequest(_httpFallbackUrl, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 30;
                req.SetRequestHeader("Content-Type", "application/json");

                UpdateStatus("送信中...");
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    var detail = $"HTTP Error: {req.error} status={req.responseCode} url={_httpFallbackUrl}";
                    Debug.LogError($"[UI] {detail}");
                    AppendResponse($"<color=#ff6666>[{detail}]</color>");
                    if (!string.IsNullOrEmpty(req.downloadHandler.text))
                        AppendResponse($"<color=#ff9999>{req.downloadHandler.text}</color>");
                    UpdateStatus($"HTTP エラー {req.responseCode}: {req.error}");
                    SetSendingState(false);
                    yield break;
                }

                DisplayAiResponse(req.downloadHandler.text);
                UpdateStatus("HTTP fallback");
                SetSendingState(false);
            }
        }

        private void DisplayAiResponse(string json)
        {
            try
            {
                var aiResp = JsonUtility.FromJson<AiResponseJson>(json);
                if (aiResp != null && aiResp.assistant != null)
                {
                    AppendResponse($"<color=#66ccff>AI:</color> {aiResp.assistant.text}");
                    return;
                }

                if (aiResp != null && !string.IsNullOrEmpty(aiResp.error))
                {
                    AppendResponse($"<color=#ff6666>[AI Error] {aiResp.error}</color>");
                    return;
                }
            }
            catch
            {
                // Fall through to raw display.
            }

            AppendResponse($"<color=#aaaaaa>{json}</color>");
        }

        private void SetSendingState(bool sending)
        {
            if (_sendButton != null)
                _sendButton.interactable = !sending;

            if (_inputField != null)
                _inputField.interactable = !sending;
        }

        private void HandleConnected()
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                UpdateStatus("接続済み");
                AppendResponse("<color=#88cc88>[Connected]</color>");
                SetSendingState(false);
                UpdateMicButtonLabel();
                UpdateMicStatusText();
                if (_autoStartMic && !_micStreaming && VoiceSocketIsConnected())
                    StartMicStreaming();
            });
        }

        private void HandleDisconnected(string reason)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                UpdateStatus($"切断 ({reason})");
                AppendResponse($"<color=#ffcc66>[Disconnected] {reason}</color>");
                if (!_useSeparateVoiceWebSocket)
                    StopMicStreaming();
                UpdateMicStatusText();
                SetSendingState(false);
            });
        }

        private void HandleError(string error)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                _httpFallbackMode = _enableHttpFallback && !string.IsNullOrEmpty(_httpFallbackUrl);
                if (_httpFallbackMode)
                    UpdateStatus("HTTP fallback");
                else
                    AppendResponse($"<color=#ff6666>[Error] {error}</color>");
                SetSendingState(false);
            });
        }

        private void HandleVoiceConnected()
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                _lastVoiceError = null;
                AppendResponse("<color=#88cc88>[Voice WS Connected]</color>");
                UpdateMicButtonLabel();
                UpdateMicStatusText();
                if (_autoStartMic && !_micStreaming)
                    StartMicStreaming();
            });
        }

        private void HandleVoiceDisconnected(string reason)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                _lastVoiceError = reason;
                AppendResponse($"<color=#ffcc66>[Voice WS Disconnected] {reason}</color>");
                StopMicStreaming();
                UpdateMicStatusText();
            });
        }

        private void HandleVoiceError(string error)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                _lastVoiceError = error;
                AppendResponse($"<color=#ff6666>[Voice WS Error] {error}</color>");
                UpdateMicStatusText();
                if (_micStreaming && !VoiceSocketIsConnected())
                    StopMicStreaming();
            });
        }

        private void HandleVoiceMessageReceived(string json)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (TryHandleSpeechRecognized(json))
                    return;
                if (TryHandleAiResponse(json, _voiceWsClient))
                    return;
                if (TryHandleError(json))
                    return;
                if (TryHandleAudioMessage(json, _voiceWsClient))
                    return;
                if (TryHandleAudioControl(json))
                    return;

                Debug.Log($"[Voice WS] Ignored broadcast/control message: {json}");
            });
        }

        private void HandleMessageReceived(string json)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (TryHandleStateChange(json))
                    return;
                if (TryHandleVADEvent(json))
                    return;
                if (TryHandleSpeechRecognized(json))
                    return;
                if (TryHandleAudioMessage(json, _wsClient))
                    return;
                if (TryHandleAudioControl(json))
                    return;
                if (TryHandleAiResponse(json, _wsClient))
                    return;
                if (TryHandleError(json))
                    return;
                if (TryHandleAck(json))
                    return;

                Debug.LogWarning($"[UI] Unhandled WebSocket message: {json}");
                AppendResponse($"<color=#aaaaaa>{json}</color>");
            });
        }

        private bool TryHandleStateChange(string json)
        {
            try
            {
                var stateChange = JsonUtility.FromJson<StateChangeJson>(json);
                if (stateChange == null || stateChange.type != "state_change")
                    return false;

                _currentState = stateChange.state;
                UpdateStatus($"State: {_currentState}");
                if (_currentState == "SPEAKING")
                {
                    EnsureAudioPlayback();
                }
                else if (_currentState == "IDLE")
                {
                    RotateVoiceRequest();
                    UpdateMicStatusText();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryHandleVADEvent(string json)
        {
            try
            {
                var vadEvent = JsonUtility.FromJson<VADEventJson>(json);
                if (vadEvent == null || vadEvent.type != "vad_event")
                    return false;

                var eventName = FirstNonEmpty(vadEvent.event_name, vadEvent.@event);
                switch (eventName)
                {
                    case "speech_start":
                        UpdateStatus("Speech detected");
                        return true;
                    case "speech_end":
                        UpdateStatus("Recognizing speech");
                        RotateVoiceRequest();
                        return true;
                    default:
                        UpdateStatus($"VAD: {eventName}");
                        return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool TryHandleSpeechRecognized(string json)
        {
            try
            {
                var speech = JsonUtility.FromJson<SpeechRecognizedJson>(json);
                if (speech == null || speech.type != "speech_recognized")
                    return false;

                Debug.Log($"[UI] Speech recognized: request_id={speech.request_id} text={speech.text}");
                ShowRecognizedSpeech(speech.request_id, speech.text, speech.cancelable);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryHandleAudioMessage(string json, WebSocketClient sourceClient)
        {
            try
            {
                var audioResp = JsonUtility.FromJson<AudioMessageJson>(json);
                if (audioResp == null || audioResp.type != "audio")
                    return false;

                HandleAudioMessage(audioResp, sourceClient);
                return true;
            }
            catch (Exception ex)
            {
                AppendResponse($"<color=#ff6666>[Audio Error] {ex.Message}</color>");
                return true;
            }
        }

        private bool TryHandleAudioControl(string json)
        {
            try
            {
                var control = JsonUtility.FromJson<AudioControlJson>(json);
                if (control == null || control.type != "audio_control")
                    return false;

                switch (control.action)
                {
                    case "stop":
                        StopAudioPlayback(notifyRuntime: true);
                        AppendResponse("<color=#ffcc66>[Audio] stopped</color>");
                        return true;
                    case "clear_queue":
                        ClearAudioQueue();
                        AppendResponse("<color=#ffcc66>[Audio] queue cleared</color>");
                        return true;
                    default:
                        AppendResponse($"<color=#ff6666>[Audio Control Error] unsupported action: {control.action}</color>");
                        return true;
                }
            }
            catch (Exception ex)
            {
                AppendResponse($"<color=#ff6666>[Audio Control Error] {ex.Message}</color>");
                return true;
            }
        }

        private bool TryHandleAiResponse(string json, WebSocketClient sourceClient)
        {
            try
            {
                var aiResp = JsonUtility.FromJson<AiResponseJson>(json);
                if (aiResp == null || aiResp.type != "ai_response")
                    return false;

                Debug.Log($"[UI] ai_response received: request_id={aiResp.request_id} has_assistant={aiResp.assistant != null} text_len={(aiResp.text == null ? 0 : aiResp.text.Length)} error={aiResp.error}");
                if (!string.IsNullOrEmpty(aiResp.error))
                {
                    AppendResponse($"<color=#ff6666>[AI Error] {aiResp.error}</color>");
                }
                else if (aiResp.assistant != null)
                {
                    AppendResponse($"<color=#66ccff>AI:</color> {aiResp.assistant.text}");
                }
                else if (!string.IsNullOrEmpty(aiResp.text))
                {
                    AppendResponse($"<color=#66ccff>AI:</color> {ExtractAssistantText(aiResp.text)}");
                }
                else
                {
                    Debug.LogWarning($"[UI] ai_response had no assistant/text: {json}");
                }
                if (!string.IsNullOrEmpty(aiResp.audio))
                {
                    HandleAudioMessage(new AudioMessageJson
                    {
                        request_id = aiResp.request_id,
                        data = aiResp.audio
                    }, sourceClient);
                }
                SetSendingState(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractAssistantText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var trimmed = value.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
                return value;

            try
            {
                var assistant = JsonUtility.FromJson<AssistantJson>(trimmed);
                if (assistant != null && !string.IsNullOrEmpty(assistant.text))
                    return assistant.text;
            }
            catch
            {
            }
            return value;
        }

        private bool TryHandleError(string json)
        {
            try
            {
                var errResp = JsonUtility.FromJson<ErrorJson>(json);
                if (errResp == null || errResp.type != "error")
                    return false;

                var errMsg = errResp.error ?? "Unknown error";
                AppendResponse($"<color=#ff6666>[Server Error] {errMsg}</color>");
                SetSendingState(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryHandleAck(string json)
        {
            try
            {
                var msg = JsonUtility.FromJson<MessageJson>(json);
                if (msg == null || msg.type == null || !msg.type.EndsWith("_ack"))
                    return false;

                Debug.Log($"[UI] Ack: {msg.type} payload={msg.payload}");
                AppendResponse($"<color=#aaaaaa>[Ack] {msg.type}</color>");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void HandleAudioMessage(AudioMessageJson audioResp, WebSocketClient sourceClient)
        {
            var base64 = FirstNonEmpty(audioResp.data, audioResp.audio, audioResp.audio_base64);
            if (string.IsNullOrEmpty(base64))
            {
                AppendResponse("<color=#ff6666>[Audio Error] missing base64 data</color>");
                return;
            }

            try
            {
                var bytes = Convert.FromBase64String(base64);
                var clip = WavAudioClip.Create(bytes, $"AICompanionAudio-{audioResp.request_id}");
                _playingAudioRequestId = audioResp.request_id;
                _playingAudioClient = sourceClient;
                EnqueueAudio(clip);
                AppendResponse($"<color=#88ccff>[Audio] queued {clip.length:0.00}s</color>");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UI] Audio decode failed: {ex.Message}");
                AppendResponse($"<color=#ff6666>[Audio Error] {ex.Message}</color>");
            }
        }

        private void EnqueueAudio(AudioClip clip)
        {
            while (_audioQueue.Count >= _maxAudioQueueSize)
            {
                var dropped = _audioQueue.Dequeue();
                if (dropped != null)
                    Destroy(dropped);
            }

            _audioQueue.Enqueue(clip);
            EnsureAudioPlayback();
        }

        private void EnsureAudioPlayback()
        {
            if (_audioSource == null)
                _audioSource = gameObject.AddComponent<AudioSource>();

            if (_audioPlaybackCoroutine == null && _audioQueue.Count > 0)
                _audioPlaybackCoroutine = StartCoroutine(PlayQueuedAudio());
        }

        private IEnumerator PlayQueuedAudio()
        {
            while (_audioQueue.Count > 0)
            {
                var clip = _audioQueue.Dequeue();
                if (clip == null)
                    continue;

                _audioSource.clip = clip;
                _audioSource.Play();
                UpdateStatus("Audio playing");

                while (_audioSource != null && _audioSource.isPlaying)
                    yield return null;

                Destroy(clip);
            }

            _audioPlaybackCoroutine = null;
            NotifyAudioPlaybackFinished();
            if (_currentState != "SPEAKING")
                UpdateStatus($"State: {_currentState}");
        }

        private void NotifyAudioPlaybackFinished()
        {
            if (string.IsNullOrEmpty(_playingAudioRequestId) || _playingAudioClient == null)
                return;

            var requestId = _playingAudioRequestId;
            var client = _playingAudioClient;
            _playingAudioRequestId = null;
            _playingAudioClient = null;
            var json = JsonUtility.ToJson(new AudioPlaybackFinishedJson
            {
                type = "audio_playback_finished",
                request_id = requestId
            });
            _ = client.SendAsync(json);
        }

        private void StopAudioPlayback(bool notifyRuntime = false)
        {
            if (_audioPlaybackCoroutine != null)
            {
                StopCoroutine(_audioPlaybackCoroutine);
                _audioPlaybackCoroutine = null;
            }

            if (_audioSource != null)
            {
                _audioSource.Stop();
                if (_audioSource.clip != null)
                    Destroy(_audioSource.clip);
                _audioSource.clip = null;
            }

            ClearAudioQueue();
            if (notifyRuntime)
                NotifyAudioPlaybackFinished();
            _playingAudioRequestId = null;
            _playingAudioClient = null;
        }

        private void ClearAudioQueue()
        {
            while (_audioQueue.Count > 0)
            {
                var clip = _audioQueue.Dequeue();
                if (clip != null)
                    Destroy(clip);
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
            return null;
        }

        private static string GetOrCreateSessionId()
        {
            var sessionId = PlayerPrefs.GetString(SessionIdPlayerPrefsKey, "");
            if (!string.IsNullOrEmpty(sessionId))
                return sessionId;

            sessionId = "unity-" + Guid.NewGuid();
            PlayerPrefs.SetString(SessionIdPlayerPrefsKey, sessionId);
            PlayerPrefs.Save();
            return sessionId;
        }

        private static string AppendSessionIdToWebSocketUrl(string url, string sessionId)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sessionId))
                return url;
            if (UrlHasQueryKey(url, "session_id"))
                return url;

            var fragmentIndex = url.IndexOf('#');
            var baseUrl = fragmentIndex >= 0 ? url.Substring(0, fragmentIndex) : url;
            var fragment = fragmentIndex >= 0 ? url.Substring(fragmentIndex) : "";
            var separator = baseUrl.Contains("?") ? "&" : "?";
            return baseUrl + separator + "session_id=" + Uri.EscapeDataString(sessionId) + fragment;
        }

        private static bool UrlHasQueryKey(string url, string key)
        {
            var queryIndex = url.IndexOf('?');
            if (queryIndex < 0)
                return false;

            var fragmentIndex = url.IndexOf('#', queryIndex);
            var query = fragmentIndex >= 0
                ? url.Substring(queryIndex + 1, fragmentIndex - queryIndex - 1)
                : url.Substring(queryIndex + 1);
            var entries = query.Split('&');
            foreach (var entry in entries)
            {
                var equalsIndex = entry.IndexOf('=');
                var entryKey = equalsIndex >= 0 ? entry.Substring(0, equalsIndex) : entry;
                if (string.Equals(Uri.UnescapeDataString(entryKey), key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private WebSocketClient VoiceSocket()
        {
            return _voiceWsClient ?? _wsClient;
        }

        private bool VoiceSocketIsConnected()
        {
            var voiceClient = VoiceSocket();
            return voiceClient != null && voiceClient.IsConnected;
        }

        private void ShowRecognizedSpeech(string requestId, string text, bool cancelable)
        {
            _pendingSpeechRequestId = requestId;
            var displayText = string.IsNullOrEmpty(text) ? "(empty speech)" : text;
            if (_speechText != null)
            {
                _speechText.text = $"Recognized: {displayText}";
                _speechText.gameObject.SetActive(true);
            }
            AppendResponse($"<color=#ffcc66>[Speech]</color> {displayText}");

            if (_cancelSpeechButton != null)
                _cancelSpeechButton.gameObject.SetActive(cancelable);

            StopSpeechCancelWindow();
            if (cancelable)
                _speechCancelWindowCoroutine = StartCoroutine(SpeechCancelWindow());

            UpdateStatus(cancelable ? "Speech recognized: cancel available" : "Speech recognized");
        }

        private IEnumerator SpeechCancelWindow()
        {
            yield return new WaitForSeconds(3f);
            _pendingSpeechRequestId = null;
            if (_cancelSpeechButton != null)
                _cancelSpeechButton.gameObject.SetActive(false);
            _speechCancelWindowCoroutine = null;
        }

        private void ClearPendingSpeech()
        {
            StopSpeechCancelWindow();
            _pendingSpeechRequestId = null;
            if (_speechText != null)
            {
                _speechText.text = "";
                _speechText.gameObject.SetActive(false);
            }
            if (_cancelSpeechButton != null)
                _cancelSpeechButton.gameObject.SetActive(false);
        }

        private void StopSpeechCancelWindow()
        {
            if (_speechCancelWindowCoroutine != null)
            {
                StopCoroutine(_speechCancelWindowCoroutine);
                _speechCancelWindowCoroutine = null;
            }
        }

        private void StartMicStreaming()
        {
            if (!VoiceSocketIsConnected())
            {
                AppendResponse("<color=#ff6666>[Mic] voice WebSocket is not connected</color>");
                return;
            }
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                StartCoroutine(RequestMicAuthorizationAndStart());
                return;
            }
            if (_micStreaming)
                return;
            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                AppendResponse("<color=#ff6666>[Mic] no microphone device found</color>");
                return;
            }
            EnsureSelectedMicDevice();
            UpdateMicDeviceDropdownOptions();

            _voiceRequestId = Guid.NewGuid().ToString();
            _voiceSeq = 0;
            _micChunksSent = 0;
            _micSendFailures = 0;
            _micReadPosition = 0;
            _lastMicSendTime = 0f;
            _lastMicDebugLogTime = 0f;
            _micClip = Microphone.Start(_selectedMicDevice, true, Mathf.Max(1, _micBufferSeconds), _micSampleRate);
            if (_micClip == null)
            {
                AppendResponse("<color=#ff6666>[Mic] failed to start microphone</color>");
                return;
            }

            _micStreaming = true;
            UpdateMicButtonLabel();
            UpdateMicStatusText();
            UpdateStatus("Mic streaming");
            _micStreamingCoroutine = StartCoroutine(MicStreamingLoop());
        }

        private IEnumerator RequestMicAuthorizationAndStart()
        {
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                AppendResponse("<color=#ff6666>[Mic] microphone permission denied</color>");
                yield break;
            }
            StartMicStreaming();
        }

        private void StopMicStreaming()
        {
            if (_micStreamingCoroutine != null)
            {
                StopCoroutine(_micStreamingCoroutine);
                _micStreamingCoroutine = null;
            }
            if (_micClip != null)
            {
                Microphone.End(_selectedMicDevice);
                _micClip = null;
            }
            _micStreaming = false;
            _micReadPosition = 0;
            _micLevel = 0f;
            UpdateMicButtonLabel();
            UpdateMicStatusText();
        }

        private IEnumerator MicStreamingLoop()
        {
            var chunkSamples = Mathf.Max(1, _micSampleRate * _micChunkMs / 1000);
            var sampleBuffer = new float[chunkSamples];
            var wait = new WaitForSeconds(_micChunkMs / 1000f);

            while (_micStreaming)
            {
                if (!VoiceSocketIsConnected() || _micClip == null)
                {
                    StopMicStreaming();
                    yield break;
                }

                var micPosition = Microphone.GetPosition(_selectedMicDevice);
                if (micPosition < 0)
                {
                    yield return wait;
                    continue;
                }

                var available = SamplesAvailable(_micReadPosition, micPosition, _micClip.samples);
                while (available >= chunkSamples)
                {
                    ReadMicSamples(sampleBuffer, _micReadPosition, chunkSamples);
                    ApplyMicGain(sampleBuffer);
                    UpdateMicLevel(sampleBuffer);
                    _micReadPosition = (_micReadPosition + chunkSamples) % _micClip.samples;
                    available -= chunkSamples;

                    var seq = _voiceSeq++;
                    var frame = BuildAudioChunkFrame(_voiceRequestId, seq, _micSampleRate, sampleBuffer);
                    _ = SendAudioChunkAsync(frame, seq, sampleBuffer.Length);
                }

                yield return wait;
            }
        }

        private async Task SendAudioChunkAsync(byte[] frame, uint seq, int sampleCount)
        {
            bool ok;
            string error = null;
            try
            {
                var voiceClient = VoiceSocket();
                ok = voiceClient != null && await voiceClient.SendBinaryAsync(frame);
            }
            catch (Exception ex)
            {
                ok = false;
                error = ex.Message;
            }

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (ok)
                {
                    _micChunksSent++;
                    _lastVoiceError = null;
                    _lastMicSendTime = Time.time;
                    if (Time.time - _lastMicDebugLogTime >= 2f)
                    {
                        Debug.Log($"[Mic] sent audio chunk request_id={_voiceRequestId} seq={seq} sample_rate={_micSampleRate} samples={sampleCount} bytes={frame.Length} sent={_micChunksSent}");
                        _lastMicDebugLogTime = Time.time;
                    }
                }
                else
                {
                    _micSendFailures++;
                    if (!string.IsNullOrEmpty(error))
                        _lastVoiceError = error;
                    Debug.LogWarning($"[Mic] failed to send audio chunk request_id={_voiceRequestId} seq={seq} failures={_micSendFailures} error={_lastVoiceError}");
                }
                UpdateMicStatusText();
            });
        }

        private static int SamplesAvailable(int readPosition, int writePosition, int clipSamples)
        {
            if (writePosition >= readPosition)
                return writePosition - readPosition;
            return clipSamples - readPosition + writePosition;
        }

        private void ReadMicSamples(float[] target, int startPosition, int count)
        {
            if (startPosition + count <= _micClip.samples)
            {
                _micClip.GetData(target, startPosition);
                return;
            }

            var firstCount = _micClip.samples - startPosition;
            var secondCount = count - firstCount;
            var first = new float[firstCount];
            var second = new float[secondCount];
            _micClip.GetData(first, startPosition);
            _micClip.GetData(second, 0);
            Array.Copy(first, 0, target, 0, firstCount);
            Array.Copy(second, 0, target, firstCount, secondCount);
        }

        private void ApplyMicGain(float[] samples)
        {
            var gain = Mathf.Clamp(_micGain, 0.1f, 8f);
            if (Mathf.Approximately(gain, 1f))
                return;

            for (var i = 0; i < samples.Length; i++)
                samples[i] = Mathf.Clamp(samples[i] * gain, -1f, 1f);
        }

        private byte[] BuildAudioChunkFrame(string requestId, uint seq, int sampleRate, float[] samples)
        {
            if (string.IsNullOrEmpty(requestId))
                requestId = Guid.NewGuid().ToString();

            var requestBytes = Encoding.UTF8.GetBytes(requestId);
            var frame = new byte[4 + requestBytes.Length + 4 + 2 + samples.Length * 2];
            var offset = 0;
            WriteUInt32BE(frame, ref offset, (uint)requestBytes.Length);
            Array.Copy(requestBytes, 0, frame, offset, requestBytes.Length);
            offset += requestBytes.Length;
            WriteUInt32BE(frame, ref offset, seq);
            WriteUInt16BE(frame, ref offset, (ushort)sampleRate);

            for (var i = 0; i < samples.Length; i++)
            {
                var sample = (short)Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), short.MinValue, short.MaxValue);
                frame[offset++] = (byte)(sample & 0xff);
                frame[offset++] = (byte)((sample >> 8) & 0xff);
            }
            return frame;
        }

        private void RotateVoiceRequest()
        {
            _voiceRequestId = Guid.NewGuid().ToString();
            _voiceSeq = 0;
        }

        private void EnsureSelectedMicDevice()
        {
            var devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
            {
                _selectedMicDevice = null;
                return;
            }
            if (string.IsNullOrEmpty(_selectedMicDevice) || Array.IndexOf(devices, _selectedMicDevice) < 0)
                _selectedMicDevice = devices[0];
        }

        private void UpdateMicDeviceDropdownOptions()
        {
            if (_micDeviceDropdown == null)
                return;

            var devices = Microphone.devices;
            _updatingMicDeviceDropdown = true;
            _micDeviceDropdown.ClearOptions();

            if (devices == null || devices.Length == 0)
            {
                _selectedMicDevice = null;
                _micDeviceDropdown.AddOptions(new List<string> { "No microphone" });
                _micDeviceDropdown.value = 0;
                _micDeviceDropdown.interactable = false;
                _micDeviceDropdown.RefreshShownValue();
                _updatingMicDeviceDropdown = false;
                UpdateMicStatusText();
                return;
            }

            EnsureSelectedMicDevice();
            _micDeviceDropdown.AddOptions(new List<string>(devices));
            _micDeviceDropdown.value = Mathf.Max(0, Array.IndexOf(devices, _selectedMicDevice));
            _micDeviceDropdown.interactable = true;
            _micDeviceDropdown.RefreshShownValue();
            _updatingMicDeviceDropdown = false;
            UpdateMicStatusText();
        }

        private void UpdateMicLevel(float[] samples)
        {
            var peak = 0f;
            for (var i = 0; i < samples.Length; i++)
            {
                var value = Mathf.Abs(samples[i]);
                if (value > peak)
                    peak = value;
            }
            _micLevel = Mathf.Lerp(_micLevel, Mathf.Clamp01(peak), 0.35f);
            UpdateMicStatusText();
        }

        private void UpdateMicButtonLabel()
        {
            if (_micButton == null)
                return;
            var label = _micButton.GetComponentInChildren<Text>();
            if (label != null)
                label.text = _micStreaming ? "Mic On" : "Mic Off";
        }

        private void UpdateMicStatusText()
        {
            if (_micText == null)
                return;

            EnsureSelectedMicDevice();
            var device = string.IsNullOrEmpty(_selectedMicDevice) ? "No mic" : _selectedMicDevice;
            var bars = Mathf.Clamp(Mathf.RoundToInt(_micLevel * 12f), 0, 12);
            var meter = new string('|', bars).PadRight(12, '.');
            var age = _lastMicSendTime <= 0f ? "-" : $"{Mathf.Max(0f, Time.time - _lastMicSendTime):0.0}s";
            var voiceState = VoiceSocketIsConnected() ? "voice:ok" : "voice:down";
            var error = string.IsNullOrEmpty(_lastVoiceError) ? "" : $" | {_lastVoiceError}";
            _micText.text = $"{(_micStreaming ? "Streaming" : "Stopped")} | {voiceState} | {device} | {meter} | gain:{_micGain:0.0} tx:{_micChunksSent} err:{_micSendFailures} last:{age}{error}";
        }

        private static void WriteUInt32BE(byte[] buffer, ref int offset, uint value)
        {
            buffer[offset++] = (byte)((value >> 24) & 0xff);
            buffer[offset++] = (byte)((value >> 16) & 0xff);
            buffer[offset++] = (byte)((value >> 8) & 0xff);
            buffer[offset++] = (byte)(value & 0xff);
        }

        private static void WriteUInt16BE(byte[] buffer, ref int offset, ushort value)
        {
            buffer[offset++] = (byte)((value >> 8) & 0xff);
            buffer[offset++] = (byte)(value & 0xff);
        }

        /// <summary>
        /// 応答表示エリアにテキストを追記する。
        /// 古い行は自動的に削除される。
        /// </summary>
        private void AppendResponse(string line)
        {
            _responseLines.Add(line);

            // 行数制限
            while (_responseLines.Count > _maxResponseLines)
                _responseLines.RemoveAt(0);

            if (_responseText != null)
            {
                _responseText.text = string.Join("\n", _responseLines);
                UpdateResponseScrollContent();
            }

            // 自動スクロール
            if (_scrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                _scrollRect.verticalNormalizedPosition = 0f;
            }
        }

        private void UpdateResponseScrollContent()
        {
            if (_responseText == null || _scrollRect == null || _scrollRect.content == null)
                return;

            var viewport = _scrollRect.viewport != null
                ? _scrollRect.viewport
                : _scrollRect.GetComponent<RectTransform>();
            var viewportHeight = viewport != null ? viewport.rect.height : 0f;
            var textHeight = _responseText.preferredHeight;
            var contentHeight = Mathf.Max(viewportHeight, textHeight + (ResponseScrollPadding * 2f));

            _responseText.verticalOverflow = VerticalWrapMode.Overflow;
            _scrollRect.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);

            var textRect = _responseText.rectTransform;
            textRect.offsetMin = new Vector2(0f, ResponseScrollPadding);
            textRect.offsetMax = new Vector2(0f, -ResponseScrollPadding);
        }

        private void UpdateStatus(string status)
        {
            if (_statusText != null)
                _statusText.text = status;
        }

        private void EnsureUIReferences()
        {
            if (_inputField != null && _sendButton != null && _micButton != null && _micDeviceDropdown != null && _micGainSlider != null && _cancelSpeechButton != null && _responseText != null && _scrollRect != null && _micText != null && _speechText != null)
                return;

            var canvas = CreateCanvas();

            if (FindObjectOfType<EventSystem>() == null)
            {
                var eventSystem = new GameObject("EventSystem");
                eventSystem.AddComponent<EventSystem>();
                eventSystem.AddComponent<StandaloneInputModule>();
            }

            var root = CreateRect("AICompanionPanel", canvas.transform);
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            _titleText = CreateText("TitleText", root, "AI Companion v0.3", 28, TextAnchor.MiddleLeft);
            SetRect(_titleText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(24f, -72f), new Vector2(-24f, -24f));

            _statusText = CreateText("StatusText", root, "接続中...", 14, TextAnchor.MiddleLeft);
            _statusText.color = Color.gray;
            SetRect(_statusText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(24f, -104f), new Vector2(-128f, -76f));

            _micButton = CreateLabeledButton(root, "MicButton", "Mic Off", new Color(0.18f, 0.52f, 0.36f, 1f));
            SetRect(_micButton.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-112f, -108f), new Vector2(-24f, -76f));

            _micText = CreateText("MicText", root, "Stopped | No mic | ............", 13, TextAnchor.MiddleLeft);
            _micText.color = new Color(0.72f, 0.9f, 0.78f, 1f);
            _micText.supportRichText = false;
            _micText.horizontalOverflow = HorizontalWrapMode.Wrap;
            SetRect(_micText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(24f, -140f), new Vector2(-220f, -112f));

            _micDeviceDropdown = CreateDropdown(root, "MicDeviceDropdown");
            SetRect(_micDeviceDropdown.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-208f, -144f), new Vector2(-24f, -112f));

            _micGainSlider = CreateSlider(root, "MicGainSlider", 0.1f, 8f, _micGain);
            SetRect(_micGainSlider.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-208f, -176f), new Vector2(-24f, -152f));

            var scrollObject = new GameObject("ScrollView", typeof(RectTransform), typeof(Image), typeof(Mask), typeof(ScrollRect));
            scrollObject.transform.SetParent(root, false);
            var scrollRectTransform = scrollObject.GetComponent<RectTransform>();
            SetRect(scrollRectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(24f, 128f), new Vector2(-24f, -184f));
            var scrollImage = scrollObject.GetComponent<Image>();
            scrollImage.color = new Color(0.02f, 0.02f, 0.02f, 0.96f);
            scrollObject.GetComponent<Mask>().showMaskGraphic = true;

            var content = CreateRect("Content", scrollRectTransform);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = new Vector2(ResponseScrollPadding, 0f);
            content.offsetMax = new Vector2(-ResponseScrollPadding, 0f);

            _responseText = CreateText("ResponseText", content, "", 16, TextAnchor.UpperLeft);
            _responseText.supportRichText = true;
            _responseText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _responseText.verticalOverflow = VerticalWrapMode.Overflow;
            SetRect(_responseText.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, ResponseScrollPadding), new Vector2(0f, -ResponseScrollPadding));

            _scrollRect = scrollObject.GetComponent<ScrollRect>();
            _scrollRect.content = content;
            _scrollRect.viewport = scrollRectTransform;
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;

            _speechText = CreateText("SpeechText", root, "", 15, TextAnchor.MiddleLeft);
            _speechText.color = new Color(1f, 0.86f, 0.45f, 1f);
            _speechText.supportRichText = false;
            _speechText.horizontalOverflow = HorizontalWrapMode.Wrap;
            SetRect(_speechText.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(24f, 82f), new Vector2(-152f, 120f));
            _speechText.gameObject.SetActive(false);

            _cancelSpeechButton = CreateLabeledButton(root, "CancelSpeechButton", "Cancel", new Color(0.78f, 0.22f, 0.18f, 1f));
            SetRect(_cancelSpeechButton.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-136f, 82f), new Vector2(-24f, 120f));
            _cancelSpeechButton.gameObject.SetActive(false);

            _inputField = CreateInputField(root);
            SetRect(_inputField.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(24f, 24f), new Vector2(-128f, 72f));

            _sendButton = CreateButton(root);
            SetRect(_sendButton.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-112f, 24f), new Vector2(-24f, 72f));
        }

        private static Canvas CreateCanvas()
        {
            var canvasObject = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.matchWidthOrHeight = 0.5f;

            return canvas;
        }

        private static void EnsureSceneCamera()
        {
            if (Camera.main != null || FindObjectOfType<Camera>() != null)
                return;

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 0f, -10f);

            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private static Text CreateText(string name, Transform parent, string text, int fontSize, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.text = text;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = Color.white;
            return label;
        }

        private static InputField CreateInputField(Transform parent)
        {
            var go = new GameObject("InputField", typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = Color.white;

            var text = CreateText("Text", go.transform, "", 16, TextAnchor.MiddleLeft);
            text.color = Color.black;
            SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 6f), new Vector2(-12f, -6f));

            var placeholder = CreateText("Placeholder", go.transform, "メッセージを入力...", 16, TextAnchor.MiddleLeft);
            placeholder.color = new Color(0.45f, 0.45f, 0.45f, 1f);
            SetRect(placeholder.rectTransform, Vector2.zero, Vector2.one, new Vector2(12f, 6f), new Vector2(-12f, -6f));

            var input = go.GetComponent<InputField>();
            input.textComponent = text;
            input.placeholder = placeholder;
            return input;
        }

        private static Button CreateButton(Transform parent)
        {
            var go = new GameObject("SendButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.16f, 0.42f, 0.82f, 1f);

            var label = CreateText("Text", go.transform, "送信", 16, TextAnchor.MiddleCenter);
            SetRect(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return go.GetComponent<Button>();
        }

        private static Button CreateLabeledButton(Transform parent, string name, string labelText, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;

            var label = CreateText("Text", go.transform, labelText, 15, TextAnchor.MiddleCenter);
            SetRect(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return go.GetComponent<Button>();
        }

        private static Dropdown CreateDropdown(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Dropdown));
            go.transform.SetParent(parent, false);
            var background = go.GetComponent<Image>();
            background.color = Color.white;

            var dropdown = go.GetComponent<Dropdown>();
            dropdown.targetGraphic = background;

            var label = CreateText("Label", go.transform, "", 13, TextAnchor.MiddleLeft);
            label.color = Color.black;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            SetRect(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 2f), new Vector2(-28f, -2f));
            dropdown.captionText = label;

            var arrow = CreateText("Arrow", go.transform, "v", 13, TextAnchor.MiddleCenter);
            arrow.color = Color.black;
            SetRect(arrow.rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(-24f, 0f), Vector2.zero);

            var template = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            template.transform.SetParent(go.transform, false);
            var templateRect = template.GetComponent<RectTransform>();
            templateRect.pivot = new Vector2(0.5f, 1f);
            SetRect(templateRect, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, -128f), Vector2.zero);
            template.GetComponent<Image>().color = new Color(0.94f, 0.94f, 0.94f, 1f);

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.transform.SetParent(template.transform, false);
            var viewportRect = viewport.GetComponent<RectTransform>();
            SetRect(viewportRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            viewport.GetComponent<Image>().color = Color.white;
            viewport.GetComponent<Mask>().showMaskGraphic = false;

            var content = CreateRect("Content", viewport.transform);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = new Vector2(0f, -128f);
            content.offsetMax = Vector2.zero;

            var scrollRect = template.GetComponent<ScrollRect>();
            scrollRect.content = content;
            scrollRect.viewport = viewportRect;
            scrollRect.horizontal = false;

            var item = new GameObject("Item", typeof(RectTransform), typeof(Image), typeof(Toggle));
            item.transform.SetParent(content, false);
            var itemRect = item.GetComponent<RectTransform>();
            SetRect(itemRect, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -24f), Vector2.zero);
            var itemImage = item.GetComponent<Image>();
            itemImage.color = Color.white;

            var itemToggle = item.GetComponent<Toggle>();
            itemToggle.targetGraphic = itemImage;

            var itemLabel = CreateText("Item Label", item.transform, "Option", 13, TextAnchor.MiddleLeft);
            itemLabel.color = Color.black;
            SetRect(itemLabel.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 2f), new Vector2(-10f, -2f));

            dropdown.template = templateRect;
            dropdown.itemText = itemLabel;
            dropdown.itemImage = itemImage;
            template.SetActive(false);
            return dropdown;
        }

        private static Slider CreateSlider(Transform parent, string name, float minValue, float maxValue, float value)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);

            var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
            background.transform.SetParent(go.transform, false);
            var backgroundRect = background.GetComponent<RectTransform>();
            SetRect(backgroundRect, Vector2.zero, Vector2.one, new Vector2(0f, 8f), new Vector2(0f, -8f));
            background.GetComponent<Image>().color = new Color(0.22f, 0.22f, 0.22f, 1f);

            var fillArea = CreateRect("Fill Area", go.transform);
            SetRect(fillArea, Vector2.zero, Vector2.one, new Vector2(4f, 8f), new Vector2(-4f, -8f));

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillArea, false);
            var fillRect = fill.GetComponent<RectTransform>();
            SetRect(fillRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            fill.GetComponent<Image>().color = new Color(0.36f, 0.74f, 0.52f, 1f);

            var handleArea = CreateRect("Handle Slide Area", go.transform);
            SetRect(handleArea, Vector2.zero, Vector2.one, new Vector2(8f, 0f), new Vector2(-8f, 0f));

            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(handleArea, false);
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(16f, 24f);
            handle.GetComponent<Image>().color = Color.white;

            var slider = go.GetComponent<Slider>();
            slider.minValue = minValue;
            slider.maxValue = maxValue;
            slider.wholeNumbers = false;
            slider.value = Mathf.Clamp(value, minValue, maxValue);
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            return slider;
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }

    internal static class WavAudioClip
    {
        public static AudioClip Create(byte[] wavBytes, string name)
        {
            if (wavBytes == null || wavBytes.Length < 44)
                throw new ArgumentException("WAV data is too short");
            if (ReadString(wavBytes, 0, 4) != "RIFF" || ReadString(wavBytes, 8, 4) != "WAVE")
                throw new ArgumentException("Audio message must contain RIFF/WAVE data");

            var offset = 12;
            var audioFormat = 0;
            var channels = 0;
            var sampleRate = 0;
            var bitsPerSample = 0;
            var dataOffset = -1;
            var dataSize = 0;

            while (offset + 8 <= wavBytes.Length)
            {
                var chunkId = ReadString(wavBytes, offset, 4);
                var chunkSize = BitConverter.ToInt32(wavBytes, offset + 4);
                var chunkDataOffset = offset + 8;

                if (chunkDataOffset + chunkSize > wavBytes.Length)
                    throw new ArgumentException("WAV chunk extends past end of data");

                if (chunkId == "fmt ")
                {
                    audioFormat = BitConverter.ToInt16(wavBytes, chunkDataOffset);
                    channels = BitConverter.ToInt16(wavBytes, chunkDataOffset + 2);
                    sampleRate = BitConverter.ToInt32(wavBytes, chunkDataOffset + 4);
                    bitsPerSample = BitConverter.ToInt16(wavBytes, chunkDataOffset + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkDataOffset;
                    dataSize = chunkSize;
                }

                offset = chunkDataOffset + chunkSize + (chunkSize % 2);
            }

            if (dataOffset < 0)
                throw new ArgumentException("WAV data chunk was not found");
            if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0)
                throw new ArgumentException("WAV format chunk is invalid");
            if (audioFormat != 1 && audioFormat != 3)
                throw new ArgumentException($"Unsupported WAV format: {audioFormat}");

            var bytesPerSample = bitsPerSample / 8;
            var sampleCount = dataSize / bytesPerSample;
            var frameCount = sampleCount / channels;
            var samples = new float[sampleCount];

            for (var i = 0; i < sampleCount; i++)
            {
                var sampleOffset = dataOffset + (i * bytesPerSample);
                samples[i] = ReadSample(wavBytes, sampleOffset, bitsPerSample, audioFormat);
            }

            var clip = AudioClip.Create(string.IsNullOrEmpty(name) ? "AICompanionAudio" : name, frameCount, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static float ReadSample(byte[] bytes, int offset, int bitsPerSample, int audioFormat)
        {
            if (audioFormat == 3 && bitsPerSample == 32)
                return Mathf.Clamp(BitConverter.ToSingle(bytes, offset), -1f, 1f);

            switch (bitsPerSample)
            {
                case 8:
                    return (bytes[offset] - 128) / 128f;
                case 16:
                    return BitConverter.ToInt16(bytes, offset) / 32768f;
                case 24:
                    var value = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
                    if ((value & 0x800000) != 0)
                        value |= unchecked((int)0xff000000);
                    return value / 8388608f;
                case 32:
                    return BitConverter.ToInt32(bytes, offset) / 2147483648f;
                default:
                    throw new ArgumentException($"Unsupported WAV bit depth: {bitsPerSample}");
            }
        }

        private static string ReadString(byte[] bytes, int offset, int count)
        {
            return Encoding.ASCII.GetString(bytes, offset, count);
        }
    }

    /// <summary>
    /// WebSocket のコールバックをメインスレッドで処理するための
    /// 簡易ディスパッチャ。
    /// </summary>
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static readonly Queue<Action> _queue = new();
        private static UnityMainThreadDispatcher _instance;
        private static int _mainThreadId;

        private void Awake()
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (_instance != null)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            while (true)
            {
                Action action = null;
                lock (_queue)
                {
                    if (_queue.Count > 0)
                        action = _queue.Dequeue();
                }
                if (action == null)
                    return;
                Execute(action);
            }
        }

        public static void Enqueue(Action action)
        {
            if (_mainThreadId == 0 || System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            {
                Execute(action);
                return;
            }

            lock (_queue)
            {
                _queue.Enqueue(action);
            }
        }

        private static void Execute(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityMainThreadDispatcher] Action failed: {ex}");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (_instance == null)
            {
                var go = new GameObject("UnityMainThreadDispatcher");
                go.AddComponent<UnityMainThreadDispatcher>();
            }
        }
    }
}
