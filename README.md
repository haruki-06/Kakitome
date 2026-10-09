# Kakitome / カキトメ

<img src="tools/assets/Kakitome-icon.png" width="96" alt="Kakitome のアイコン" align="right">

録音を「書き留める」Windows アプリです。ノート PC で会議・講義・動画を録音し、**この PC の中だけで**文字起こし・要約・検索まで行います。
クラウドやアカウント、有料サービスは使いません。必要なモデルを一度入れれば、オフラインで動きます。

MIT ライセンスのオープンソースソフトウェアです。

## できること

- **録音**
  - マイクと PC の音を別々のファイルに同時に録音できます。PC の音は、全体または特定のアプリだけを選べます。
  - 一時停止・再開・停止・取り消しができます。取り消した録音はごみ箱に入るので、あとから戻せます。
  - マイクの抜き差しやスリープから自動で復帰し、録音を途中で失いません。
  - タスクトレイに常駐し、グローバルホットキー（既定は `Ctrl+Alt+Shift+R`）で開始・停止できます。
  - 録音中でもプロジェクトを変更できます（停止したときにそのプロジェクトへ保存されます）。
- **取り込み**：音声・動画ファイルのほか、YouTube などの URL から取り込めます（URL 取り込みは yt-dlp を使います）。
- **文字起こし**
  - 日本語（英語まじりの会話を含む）が主な対象で、英語にも対応しています。
  - 録音中は、文字起こしのライブプレビューを表示します。
  - 用語辞書に専門用語や人名、「誤 -> 正」の修正を登録すると、文字起こしの精度を上げられます。
- **要約**：文字起こしから要点・決定事項・ToDo（担当・期限）を抜き出します。PC 内の AI モデルで要約文も作れます。
- **整理と検索**
  - 録音はプロジェクトごとに分けて保存します。
  - 全文検索ができます。録音の画面では文字起こしと要約を読みやすい文章で表示し、クリックした位置から再生できます。
- **データの安全**
  - バックアップと復元ができます。
  - ライブラリはエクスプローラーやメモ帳でも読めるファイルで保存します。アンインストールしても、選ばない限りライブラリは残ります。
- **バックグラウンド処理**：電源・バッテリー・温度・空き容量を見ながら処理するので、操作や録音を邪魔しません。

## 動作環境

- Windows 11 24H2 以降（x64。ARM64 版もありますが、動作は未確認です）
- メモリ 8 GB 以上（PC 内の AI で要約するなら 16 GB を推奨）
- GPU・NPU は不要です（あれば使います）

## インストール

1. [Releases](https://github.com/haruki-06/Kakitome/releases) から、PC に合った `.msi` をダウンロードします。
   - `Kakitome_<バージョン>_x64.msi`：通常の Windows PC（Intel / AMD）用
   - `Kakitome_<バージョン>_arm64.msi`：ARM 版 Windows（Snapdragon など）用
2. `.msi` をダブルクリックします。「Windows によって PC が保護されました」と出たら、［詳細情報］→［実行］を選びます。
   管理者権限や証明書の登録は不要です。
3. スタートメニューの「Kakitome」から起動します。初回の起動時に、この PC に合った音声認識モデルと URL 取り込み用の
   yt-dlp（メモリ 16 GB 以上なら要約用 AI も）を自動でダウンロードします（従量制の接続では行いません）。
   進み具合は［キュー］で見られます。モデルは［設定］→［モデル］で選び直せます。

アップデートするときは、新しい `.msi` を実行するだけです。ライブラリ・設定・モデルはそのまま残ります。
新しいバージョンが公開されると、ホーム画面と Windows の通知でお知らせします（1 日 1 回 GitHub に確認します。［設定］→［全般］で止められます）。

### 使うモデル

モデルは同梱していません。初回の起動時に推奨のものを自動でダウンロードし、ほかのモデルはアプリ内のモデル管理から選んでダウンロードします（自動ダウンロードは設定で止められます）。ダウンロードしたファイルは、ハッシュで正しいものか確認してから使います。

| 用途 | モデル | ライセンス |
|---|---|---|
| 文字起こし | Whisper large-v3-turbo / Whisper small（whisper.cpp） | MIT |
| 文字起こし | ReazonSpeech k2 v2（日本語、sherpa-onnx） | Apache-2.0 |
| 要約（任意） | Qwen3 4B Instruct 2507（llama.cpp） | Apache-2.0 |
| 要約（任意） | Phi-4-mini-instruct（llama.cpp） | MIT |
| URL 取り込み（任意） | yt-dlp | Unlicense |

- モデルとツールは、それぞれの配布元のライセンスに従って利用してください。
- どのモデルを使うかは、実測ベンチマークで決めています（`docs/benchmarks/`）。
- URL からの取り込みは、取り込み元のサイトの利用規約と著作権の範囲で行ってください。

### インターネットへの接続

録音・文字起こし・要約・検索はすべてこの PC の中で行い、録音や文章を外部に送ることはありません。インターネットに接続するのは次のときだけです。

- モデルと yt-dlp のダウンロード（初回の自動ダウンロードと、モデル管理から選んだとき）
- URL からの取り込み（yt-dlp が取り込み元のサイトに接続します）
- 新しいバージョンの確認（GitHub に最新のバージョンを問い合わせるだけ。オフにできます）

## 不具合の報告

1. ［設定］→［データとバックアップ］→［不具合の報告］の［診断情報を保存…］で zip を保存します。
2. [Issues](https://github.com/haruki-06/Kakitome/issues) で「不具合の報告」を選び、起きたことと手順を書いて、zip を添付します。

zip には、ログ（14 日分）・PC の情報（OS・CPU・メモリ・GPU・電源・空き容量・音声デバイス）・設定・処理の履歴が入ります。
録音・文字起こし・要約・タイトルは含まず、ユーザー名やフォルダー名は `%USERPROFILE%` などに置き換えます。
ログにはプロジェクト名やファイル名が含まれることがあるので、気になる場合は添付する前に中身を確認してください。
ログは `%LOCALAPPDATA%\Kakitome\Logs` にもあります。

## ライブラリ

録音データは `ドキュメント\Kakitome\Library` に保存します。

```text
Library/
  Projects/
    <プロジェクト>/
      <プロジェクト>_<日時>/
        audio.wav            録音（マイク。PC の音は別ファイル）
        metadata.json        タイトル・日時・処理の記録
        transcript.md/.json  文字起こし（タイムスタンプ付き）
        transcript.txt       読みやすい文章版
        summary.md/.json     要約
        summary.txt          読みやすい文章版
```

これらのファイルが正本です。アプリ内のデータベース（SQLite）は検索用の索引にすぎず、なくなってもライブラリから作り直せます。

## 開発

### 必要なもの

- Windows 11 24H2 以降
- .NET 10 SDK
- Windows App SDK / WinUI 3 のビルド環境（Visual Studio 2026 の WinUI ワークロードなど）
- Git、PowerShell 7、Python 3（UI 文字列とアイコンの生成に使います）

### ビルド・テスト・実行

```powershell
dotnet build Kakitome.slnx -c Debug -p:Platform=x64
dotnet test --solution Kakitome.slnx -c Debug -p:Platform=x64
pwsh tools/Run-Dev.ps1                                  # 開発版をビルドして起動
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Build-Release.ps1   # リリース用 MSI（x64 / ARM64）
```

UI の画面操作テスト（UI Automation）は `tools/Test-*.ps1` にあります。各スクリプトの先頭に実行方法を書いています。

### 構成

| プロジェクト | 役割 |
|---|---|
| `src/Kakitome.App` | WinUI 3 の画面、アプリの組み立て |
| `src/Kakitome.Presentation` | 画面のビューモデル |
| `src/Kakitome.Application` | 録音・取り込み・処理ジョブ・ライブラリなどのユースケース |
| `src/Kakitome.Domain` | ライブラリのデータ形式などの中核モデル |
| `src/Kakitome.Infrastructure` | 音声入出力、音声認識・要約エンジン、OS 連携 |
| `src/Kakitome.Storage` | ファイル保存、SQLite、設定 |
| `tests/Kakitome.Tests` | 自動テスト |
| `tools/` | ビルド・テスト用スクリプト、ベンチマーク（`Kakitome.Bench`）、アイコンの元画像 |
| `installer/` | MSI インストーラー（WiX） |

- UI の文字列は `tools/strings.py` で管理します（`.resw` は直接編集しません）。
- アイコンは、`tools/assets/icon-original.png` から `tools/assets/prepare_icon.py` と `tools/New-AppAssets.ps1` で作ります。

### ドキュメント

- `docs/00`〜`docs/10`：仕様
- `docs/11_DECISIONS.md`：設計上の判断（ADR）
- `docs/benchmarks/`：ベンチマーク結果
- `docs/release-notes/`：リリースノート
- `CHANGELOG.md`：変更履歴
- `THIRD-PARTY-NOTICES.md`：サードパーティーのライセンス

## 貢献

不具合の報告や改善の提案は Issue でお願いします。プルリクエストも歓迎します。

- 変更には、なるべくテスト（`tests/Kakitome.Tests`）を付けてください。
- 録音データ・モデル・個人情報はリポジトリに含めないでください。
- 脆弱性は Issue ではなく、`SECURITY.md` の方法で報告してください。

### 今後の課題

- インストーラーへのコード署名（OSS 向けの無料署名サービスの利用を検討中）。署名がないため、初回に SmartScreen の確認が表示されます。

## ライセンス

[MIT License](LICENSE)。同梱・利用しているサードパーティー製品のライセンスは `THIRD-PARTY-NOTICES.md` と `licenses/` にあります。
