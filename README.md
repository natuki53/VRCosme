# VRCosme Classic

> [!IMPORTANT]
> このアプリは旧版となり、ユーザー向け名称を **VRCosme Classic** に変更しました。
> 後継の **VRCosme** は Web・Windows 向けに <https://vrcosme.com/> で公開しています。

VRCosme Classic は、VRChat 向けの写真レタッチに特化した従来の Windows アプリです。

読み込み、補正、比較、書き出しまでを 1 画面で完結できます。

## 主な機能

- 基本補正: 明るさ / コントラスト / ガンマ / 露出 / 彩度 / 色温度 / 色かぶり
- 詳細補正: シャドウ / ハイライト / 明瞭度 / シャープ / 周辺減光
- 構図調整: 比率トリミング、90 度回転、左右・上下反転
- 比較表示: 補正前 / 補正後 / 分割比較
- 作業支援: Undo / Redo、三分割グリッド、ルーラー、最近使った画像
- 書き出し: PNG / JPEG（品質指定）
- 多言語 UI: 日本語 / 英語 / 韓国語 / 中国語（簡体字 / 繁体字）

## 動作環境

- OS: Windows 10 / 11（x64）
- 開発環境: .NET SDK 10（`net10.0-windows` をターゲット）

## ダウンロード

- 後継版 VRCosme: <https://vrcosme.com/>
- VRCosme Classic 最新版: <https://github.com/natuki53/VRCosme/releases/latest/download/VRCosme_Setup.exe>
- VRCosme Classic リリース一覧: <https://github.com/natuki53/VRCosme/releases>

新しく利用を始める場合は、後継版 VRCosme をおすすめします。

## 開発手順

```powershell
dotnet restore
dotnet build
dotnet run
```

### 配布ビルド（自己完結）

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
./scripts/Generate-ThirdPartyNotices.ps1
Copy-Item THIRD-PARTY-NOTICES.txt bin/Release/net10.0-windows/win-x64/publish/
Copy-Item third_party_notices bin/Release/net10.0-windows/win-x64/publish/ -Recurse
```

### インストーラー作成（Inno Setup）

```powershell
iscc Installer/VRCosme.iss
```

出力先: `Installer/Output/VRCosme_Setup.exe`

## ドキュメント

- プロジェクト同梱ドキュメント: `docs/index.html`

## ライセンス

本プロジェクトは [MIT License](LICENSE) の下で公開されています。  
Windows インストーラーで配布するバイナリにも、同じ `LICENSE` が同梱されます。

## Third-party notices

配布物に含まれる NuGet 依存ライブラリのライセンス情報は `THIRD-PARTY-NOTICES.txt` を参照してください。  
上流パッケージ同梱の NOTICE は `third_party_notices/` に抽出して配布物へ同梱してください。

## フィードバック

- Issues: <https://github.com/natuki53/VRCosme/issues>
