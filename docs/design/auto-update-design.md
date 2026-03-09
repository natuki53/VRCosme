# 自動更新機能 設計書（Issue #12）

## 0. 現状ステータス（2026-03-09 時点）

- 自動更新機能は未実装
- 起動時の更新チェックや更新通知 UI は存在しない
- 更新関連の設定キーは `settings.json` に存在しない
- 配布導線は GitHub Releases のリンクを README と `docs/index.html` で案内している
- インストーラー出力名は `VRCosme_Setup.exe`（Inno Setup: `Installer/VRCosme.iss`）
- バージョン表示は About 画面で `AssemblyInformationalVersion` を優先し、なければ `AssemblyVersion` を表示
- ログは起動時に `AssemblyVersion` を出力している（更新関連ログは未実装）

## 1. 目的（将来実装に向けた方針）

VRCosme に「起動時の新バージョン検知」と「ユーザー主導での簡易更新導線」を追加し、手動更新の負担を下げる。  
対象 Issue: <https://github.com/natuki53/VRCosme/issues/12>

## 2. スコープ（現状）

### 2.1 現状の実装範囲

- 自動更新機能は未実装
- 更新チェックや更新通知の導線は存在しない
- 更新関連の設定キーは存在しない

### 2.2 将来検討（未実装）

- 完全自動インストール（無人更新）
- 差分パッチ更新
- バックグラウンド常駐更新
- 署名検証の厳密化（後述の拡張案で対応）

## 3. 前提条件（将来案）

- 配布は GitHub Releases を利用（README と `docs/index.html` で最新リンクを案内）
- `releases/latest/download/VRCosme_Setup.exe` を更新導線として維持する想定
- 現在のアプリは WPF (`net10.0-windows`) で、設定は `%LocalAppData%\VRCosme\settings.json` に保存される

## 4. 要件定義（将来案・未実装）

### 4.1 機能要件

1. 起動後に自動チェックを行う（初期値 ON）
2. 更新がある場合、ユーザーに新旧バージョンと概要を通知する
3. ユーザーが「ダウンロードして更新」を選べる
4. ダウンロード完了後、インストーラー起動前に最終確認を行う
5. 「今回はスキップ」「後で」を選べる
6. スキップしたバージョンは次回起動時に再通知しない（より新しい版が出たら通知再開）

### 4.2 非機能要件

- 起動体験を悪化させない（タイムアウト短め、非同期）
- 失敗時はサイレント劣化（アプリ本体機能は継続）
- ログで原因追跡できる（通信、パース、比較、DL、起動）

## 5. 取得元仕様（GitHub Releases・将来案）

### 5.1 参照 API

- エンドポイント: `GET https://api.github.com/repos/natuki53/VRCosme/releases/latest`
- 想定利用フィールド:
  - `tag_name`（例: `v1.0.2`）
  - `body`（リリースノート）
  - `html_url`（ブラウザ表示用）
  - `assets[]`（ダウンロード対象探索）

### 5.2 ダウンロード対象の選定ルール

優先順:

1. asset 名が `VRCosme_Setup.exe` と完全一致
2. 見つからない場合は `html_url` を「手動更新へ」導線として表示（DL ボタンを無効化）

## 6. バージョン比較仕様（将来案）

- 現在版は `AssemblyInformationalVersion` を優先して取得、なければ `AssemblyVersion`
- `v` プレフィックス（`v1.0.2`）は比較前に除去
- 比較は `System.Version` 互換形式を基本とする
- プレリリース表記（`-beta` 等）は今回対象外とし、検出時は「比較不可」として更新通知を出さない

## 7. アーキテクチャ設計（将来案）

## 7.1 追加コンポーネント（将来案）

- `Services/Update/UpdateCheckService`
  - チェック起点、オーケストレーション
- `Services/Update/GitHubReleaseClient`
  - Releases API 呼び出し、DTO 変換
- `Services/Update/VersionComparer`
  - 現在版と最新版の比較
- `Services/Update/UpdateDownloadService`
  - インストーラーのダウンロードと保存
- `Models/UpdateInfo`
  - 通知表示に必要な情報の集約

## 7.2 既存への組み込み点（将来案）

- `App.xaml.cs`
  - 起動後に fire-and-forget で更新チェックを開始
- `MainWindow`（または専用ダイアログ）
  - 更新通知 UI を表示
- `ThemeService`（settings.json）
  - 更新設定の保存/取得 API を追加

## 8. 設定項目（settings.json 拡張・将来案）

現状の `settings.json` には以下のキーは存在しない。

追加キー案:

- `AutoUpdateEnabled` : `bool`（既定 `true`）
- `AutoUpdateCheckOnStartup` : `bool`（既定 `true`）
- `SkippedUpdateVersion` : `string`（既定 `""`）
- `LastUpdateCheckUtc` : `string` ISO 8601（既定 `""`）

補足:

- 後方互換は既存 `Settings` クラスのデフォルト値で吸収する
- 将来的な定期チェックに備えて `LastUpdateCheckUtc` を先行保持

## 9. UI/UX 設計（将来案）

### 9.1 通知ダイアログ

表示条件:

- 更新あり かつ
- `latestVersion != SkippedUpdateVersion`

表示内容:

- タイトル: 「新しいバージョンがあります」
- 現在版 / 最新版
- リリースノート（折りたたみ可）
- ボタン:
  - `ダウンロードして更新`
  - `このバージョンをスキップ`
  - `後で`
  - `リリースページを開く`

### 9.2 設定画面（将来実装時）

`SettingsDialog` に以下を追加:

- `起動時に更新をチェック`（チェックボックス）
- `更新を自動で確認する`（チェックボックス。将来の定期チェック用）
- `今すぐ確認`（手動チェックボタン）

## 10. シーケンス（将来案）

1. アプリ起動完了
2. `AutoUpdateCheckOnStartup == true` ならチェック開始
3. Releases API 取得（タイムアウト 3-5 秒）
4. 取得成功時にバージョン比較
5. 更新ありなら通知ダイアログ表示
6. ユーザーが更新実行を選択した場合:
   - インストーラーを `%LocalAppData%\VRCosme\updates\` に保存
   - 完了後、起動確認ダイアログ
   - `Process.Start(..., UseShellExecute = true)` でセットアップを起動
   - 本体終了を促す

## 11. 例外・エラー処理（将来案）

- 通信不可: 通知なし（ログのみ）
- JSON パース失敗: 通知なし（ログのみ）
- 対象 asset 不在: `リリースページを開く` 導線のみ提示
- ダウンロード失敗: エラーダイアログ表示 + 再試行導線
- インストーラー起動失敗: エラーダイアログ表示

## 12. ログ設計（将来案）

`LogService` へ以下を記録:

- 更新チェック開始/終了
- 最新版取得結果（tag、asset 有無）
- バージョン比較結果（update available/not available）
- ダウンロード開始/完了/失敗
- インストーラー起動結果

機微情報（アクセストークン等）は出力しない。

## 13. セキュリティ考慮（将来案）

最低限:

- HTTPS のみ利用
- ダウンロード先はアプリ専用ディレクトリに限定

推奨拡張:

- asset に SHA-256 を併記し照合
- Authenticode 署名検証
- 許可ホスト固定（`github.com`, `api.github.com`）

## 14. 受け入れ基準（Definition of Done・将来案）

1. 起動時に自動チェックされる
2. 新バージョン時に通知ダイアログが表示される
3. `VRCosme_Setup.exe` をダウンロードできる
4. インストーラーを起動できる
5. スキップした版は再通知されない
6. 通信失敗時でもアプリ利用に影響しない

## 15. 実装ステップ案（チケット分割・将来案）

1. ドメイン層: `UpdateInfo`/比較ロジック追加
2. インフラ層: GitHub API クライアント追加
3. アプリ層: 起動時チェック導線追加
4. UI 層: 更新通知ダイアログ追加
5. DL 層: インストーラー取得・起動処理追加
6. 設定層: `ThemeService` と `settings.json` 拡張
7. 文言: 多言語リソース追加
8. テスト: 比較ロジック・失敗系・手動確認

## 16. テスト観点（将来案）

- バージョン比較:
  - `1.0.1` vs `1.0.2` -> 更新あり
  - `1.0.2` vs `1.0.2` -> 更新なし
  - `v1.0.2` 正常化
- API 異常:
  - タイムアウト
  - 404/403/500
  - body 欠損
- asset 異常:
  - `VRCosme_Setup.exe` 不在
- 操作:
  - スキップ保存/再起動後の再通知抑止
  - ダウンロード後の起動確認

## 17. 未決事項

1. 「後で」選択時の再通知タイミング（次回起動のみ or 当日抑止）
2. チェック頻度（起動時のみ or n 時間ごと）
3. プレリリースを通知対象に含めるか
4. setup 実行前に未保存作業の保存確認を行うか（現行終了確認との整合）
