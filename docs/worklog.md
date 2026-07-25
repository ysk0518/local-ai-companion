# 作業日報

## 2026-07-23 〜 07-24: Codex レビュー #4-7 + v0.5 完了 + マージキュー整備

### Codex レビュー #4-7 (PR #159, #163, #164, #165)
- [x] #4: 排他制御をセッション単位に（agentMu → wsConnState.mu）
- [x] #5: 履歴保存トランザクション化（SaveTurn を BEGIN/COMMIT でラップ）
- [x] #6: web_fetch SSRF 対策（DNS解決 + IP検査 + リダイレクト先検証）
- [x] #7: 細部改善（MaxToolLoops コメント + ツール一覧ソート）

### v0.5 完了
- [x] 実装・ドキュメント全件完了
- [x] architecture.md, decisions.md の STT 担当を Python→Go に更新
- [x] v0.5_progress.md を完了表記に更新
- [x] #114, #115 クローズ

### マージキュー整備
- [x] `codex-approved` ラベルベースの再承認ループ防止
- [x] merge-queue cron（2分間隔、ready-to-merge + codex-approved の PR を順次マージ）
- [x] gh 2.63.2 GraphQL バグ回避（REST API でラベル付与）
- [x] auto-merge 無効 → 直接マージに変更

### 発見・修正したバグ
- gh 2.63.2: `--submit` フラグ廃止 → スクリプト修正
- gh 2.63.2: `gh pr edit --add-label` が GraphQL 警告で exit 1 → REST API に切り替え
- gh 2.63.2: `gh pr list --json reviewDecision` が APPROVED 後も空文字 → ラベルベース判定に変更
- リポジトリで auto-merge 無効 → 直接 merge に変更
- Codex cron が同一 PR を 30 回以上再承認 → `codex-approved` ラベルで防止

### PR 一覧
| PR | 内容 | 状態 |
|----|------|------|
| #156 | Agent Loop 統合 | ✅ merged |
| #157 | Codex レビュー #1-3 | ✅ merged |
| #159 | 排他制御セッション単位 | ✅ merged |
| #163 | 履歴保存トランザクション化 | ✅ merged |
| #164 | web_fetch SSRF 対策 | ✅ merged |
| #165 | 細部改善 | ✅ merged |

### 現在の cron 構成
| cron | 間隔 | 役割 |
|------|------|------|
| codex-auto-work | 10分 | ready-to-merge ラベル付き PR を approve + codex-approved ラベル付与 |
| merge-queue | 2分 | ready-to-merge + codex-approved の最古 PR を 1 件マージ |

### 環境メモ
- ThinkPad X1C6: Go 1.26.4, gh 2.63.2 (GraphQL バグ注意), Hermes CLI
- WinPC: Ollama g4v100, ComfyUI, faster-whisper :8093
- VOICEVOX: 127.0.0.1:50021, speaker=3 (ずんだもん)

### 次回
- v0.6: Character Control（表情・モーション・口パク）
