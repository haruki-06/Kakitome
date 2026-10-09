# 用語辞書 / Glossary

Kakitome の文字起こしは、専門用語・人名・固有名詞を同音の別の語に書き間違えることがあります
（例：「信教の自由」→「新居の自由」）。用語辞書を用意すると、

- 辞書の語が文字起こしのヒントとして音声認識に渡され、正しく書かれやすくなります。句読点も付きやすくなります。
- 「誤 -> 正」の修正が、文字起こしの後に自動で適用されます（元の文は `transcript.json` の `rawText` に残ります）。

辞書はアプリには含まれていません。分野ごとに、ご自身や AI に作ってもらったテキストファイルを取り込みます。

## 書式

UTF-8 のテキストファイル（`.txt`）で、1 行に 1 項目を書きます。

```text
# 日本国憲法（人権）の講義      ← # で始まる行はメモ（無視されます）
罪刑法定主義                    ← 用語：文字起こしのヒント
堀木訴訟
新居の自由 -> 信教の自由        ← 修正：「誤 -> 正」（→ や => でも可）
```

- 重要な語を上に書いてください。ヒントとして一度に渡せるのは 20 語ほどです。それより多い場合は、直前の話題に
  関係する語（直前に出てきた語や、文字が共通する語）が優先され、残りは順番に使われます。
- 修正の「誤」は 2 文字以上にしてください。修正は辞書のすべての行がいつも適用されます。
- 英数字だけの修正は単語単位で置き換えます（`sequel -> SQL` は `sequels` を変えません）。
- 1 項目は 40 文字まで、ファイルは 512 KB までです。Shift_JIS のファイルも読み込めます。

## 置き場所と適用範囲

| ファイル | 使われる録音 |
|---|---|
| `Library\Projects\glossary.txt` | すべての録音 |
| `Library\Projects\<プロジェクト>\glossary.txt` | そのプロジェクトの録音（共通の辞書と併用。同じ「誤」はプロジェクト側が優先） |

設定 › 文字起こし › 用語辞書 で、適用先を選んで「テキストを取り込む…」「編集」「使わない」を操作できます。
エクスプローラーやメモ帳で直接作成・編集しても構いません。取り込みで上書きするときや「使わない」を選んだときも、
以前のファイルは `glossary.<日時>.txt` / `glossary.removed-<日時>.txt` として同じフォルダーに残ります。

辞書はこれからの文字起こしに使われます。既存の録音に使うには、録音の詳細で「再文字起こし」を実行してください。

## AI に作ってもらう

設定画面の「書式と AI での作り方」にある依頼文をコピーし、［内容］の部分を書き換えて AI アシスタントに渡します。

```text
次の内容の音声を文字起こしするための用語辞書を作ってください。
内容：［ここに分野・授業名・会議の内容などを書く］

書式（プレーンテキスト、1 行に 1 項目、番号や説明は付けない）：
- この分野でよく出る専門用語・人名・固有名詞・判例名・製品名などを、重要なものから順に、多くても 200 行程度
- 音声認識が間違えそうな書き方がわかる場合は「誤 -> 正」の形で 1 行に書く（例：新居の自由 -> 信教の自由）
- # で始まる行はコメント
```

シラバスや資料の目次を一緒に渡すと、より的確な辞書になります。返ってきた内容をメモ帳に貼り付けて `.txt` で保存し、
取り込んでください。文字起こしを読んで間違いに気づいたら、「誤 -> 正」の行を書き足していくと精度が上がります。

> 辞書の作成に外部の AI を使うかどうかは利用者の判断です。Kakitome 自身は辞書や録音の内容を外部に送信しません。

### 録音ごとのおすすめ

辞書のないプロジェクトで 5 分以上の録音の文字起こしができると、録音の画面に「用語辞書で精度を上げられます」と
表示されます（完了通知にも一言添えます）。

1. 「AI への依頼文をコピー」を押します。録音のタイトルと要約の話題が入った依頼文がコピーされます。
2. 「transcript.txt の場所を開く」で文字起こしファイルを確認し、依頼文と一緒に AI アシスタントに渡します。
   文字起こし全体を読ませると、実際に起きた誤変換を「誤 -> 正」で書いてもらえます。
3. 返ってきた内容を `.txt` で保存し、「辞書を取り込んで再文字起こし…」を選びます。辞書はその録音の
   プロジェクトに取り込まれ、続けて再文字起こしできます。

音声認識の確信度では同音の誤変換を見分けられず、PC 内の小さな AI では正しい辞書を作れないことを測定で確かめたため
（docs/benchmarks/asr-glossary-v2）、Kakitome が自動で辞書を作ったり書き換えたりはしません。おすすめが不要なら
「今後このおすすめを表示しない」を選ぶか、設定 › 文字起こし › 用語辞書 のチェックを外します。

---

## English

A glossary is a UTF-8 text file with one entry per line: a term (given to speech recognition as a hint), a correction
`wrong -> right` (applied after transcription; the original wording stays in `transcript.json`), or a `#` note.
`Library\Projects\glossary.txt` applies to every recording; `Library\Projects\<Project>\glossary.txt` to one project.
Manage them in Settings › Transcription › Glossary, or edit the files directly. About 20 terms are offered at a time,
chosen by the current topic; put the most important ones first. Use Re-transcribe to apply a new glossary to an
existing recording. The Settings page has a request text you can give an AI assistant to write a glossary for a subject. Longer transcripts without a glossary show a tip on the recording page: copy a request, give it to an AI assistant together with transcript.txt, then "Import glossary and transcribe again". Kakitome never builds or applies a glossary by itself (docs/benchmarks/asr-glossary-v2).
