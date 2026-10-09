# Changelog

All notable changes to Kakitome (named SmartRec before 2.0.0) are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [2.0.1] - 2026-10-09

### Fixed

- Auto processing mode no longer waits for AC power from the first percent on battery: heavy work continues with a
  reduced budget down to 30 % and Home says why work waits (ADR-042).
- A pipeline cut short by a crash while it was being queued is completed at the next start (transcription never ran).
- Diagnostics include Windows' crash and hang records for Kakitome; YouTube "HTTP 403" downloads are retried.

## [2.0.0] - 2026-10-09

### Changed

- Renamed from SmartRec to Kakitome (ADR-033): app, executable, installer, data folders (`%LOCALAPPDATA%\Kakitome`,
  `Documents\Kakitome\Library`), backup extension `.kakitome-backup` and environment variables `KAKITOME_*`.
  Nothing is migrated from SmartRec; it installs as a separate app.
- Open source under the MIT License (`LICENSE`, `SECURITY.md`, CI build).
- Recording page: transcript and summary read as plain text — no time stamps, sentence ends added at pauses, short
  paragraphs (`ReadingLayout`); click-to-play and the highlight stay. Position / length shown below the seek bar.

### Added

- Update notice: a daily check of the latest GitHub release, shown on Home and as a notification (ADR-040).
- Log files and Settings › Data › "Report a problem" to save a diagnostics zip for issues (ADR-041).
- Missing recommended ASR model, summary AI (16 GB class memory) and yt-dlp are downloaded automatically at startup
  (ADR-034); Settings › Models groups models by purpose with notes on what each is for.

### Fixed

- Transcriptions queued before the first ASR model finished downloading failed for good; they are retried when a
  model is installed. Job errors are shown in the UI language. A crash when the app was closed by the installer after
  copying text.
- Summaries no longer force the meeting template on other recordings: sections follow the kind of recording, 3+ key
  points, speakers by name (ADR-038).
- sherpa-onnx (ReazonSpeech) could bind to the Windows App SDK's older onnxruntime.dll; the bundled copy is loaded
  first. Crash when opening a recording deleted outside Kakitome.
- GPU use (ADR-035): GPUs are detected through Vulkan; only a discrete GPU with 2 GB+ is used (pinned via
  GGML_VK_VISIBLE_DEVICES), integrated GPUs stay on the CPU; the summary model is offloaded only with 6 GB+ GPU memory
  and retried on the CPU after a GPU failure.
- MSI upgrades reset settings and the processing history (the old product's uninstall cleanup also ran during an
  upgrade, since 1.1.0). The cleanup now runs only on a real uninstall.
- Pre-releases sharing the MSI version (2.0.0-beta.N) were installed side by side instead of replacing each other
  (same-version major upgrades are allowed now).

## [1.6.0] - 2026-10-08

### Changed

- Recording page: the transcript reads as flowing paragraphs (like transcript.md) with the segment being heard
  highlighted; click plays from there, double-click/F2 corrects. Transcript and summary are switched with tabs
  (ADR-032).
- Settings: tabs (General, Recording, Transcription & summary, Models, Data & backup), cards with short descriptions,
  rarely changed options under Advanced, maintenance actions under Troubleshooting.
- Processing queue: one card per recording/file with overall progress; in-progress/failed first, finished below.

### Fixed

- The processing queue showed no title for recordings created after the page was opened.

## [1.5.4] - 2026-10-08

### Fixed

- The MSI did not contain the Assets folder (icons), so the notification-area icon never appeared and the window had
  no icon in the title bar or taskbar preview. The window icon is also loaded from an absolute path now.

## [1.5.3] - 2026-10-08

### Changed

- App icon replaced by the user's feather art on a white rounded tile (window, taskbar, Start menu, tray, installer).

## [1.5.2] - 2026-10-07

### Changed

- New app icon from the user's art (transparent background) for the window, taskbar, Start menu, tray and installer;
  the recording tray icon adds a red dot.

## [1.5.1] - 2026-10-04

### Fixed

- URL import: non-ASCII video titles were garbled because yt-dlp wrote to the pipe in the ANSI code page; it is now
  run with `--encoding utf-8`.

## [1.5.0] - 2026-10-04

### Added

- Home: the project can be changed while recording (drop-down and "New…" stay enabled). The recording folder moves
  to the chosen project when the recording stops; a generated title follows the new project name, a typed title is
  kept, and processing starts after the move. If the folder cannot be moved it stays in its original project.

## [1.4.0] - 2026-10-04

### Added

- Glossary tips: recordings of 5+ minutes in a project without a glossary suggest one (copy a request for an AI
  assistant, import a glossary and re-transcribe). The completion notification mentions it; it can be turned off.

### Fixed

- File and URL imports went to Inbox instead of the project selected on Home.

## [1.3.1] - 2026-10-04

### Fixed

- Projects: "Create project" looked unusable (it stayed disabled until a name was typed, and Japanese input counted
  only after it was committed). The button is now always available, explains an empty or existing name, and Enter
  creates the project.

### Changed

- Home: the project is chosen from a drop-down of existing projects; "New…" next to it creates one and selects it.

## [1.3.0] - 2026-10-03

### Added

- Glossaries: import or write a plain-text list of terms and "wrong -> right" corrections for all recordings or for
  one project (Settings › Transcription › Glossary, see docs/glossary.md). Terms are given to speech recognition as
  hints, chosen by the current topic; corrections are applied after transcription (the original wording is kept).
  No glossary is built in; Settings shows the format and a request you can give an AI assistant to write one.

### Changed

- Japanese transcripts now come with punctuation (「、」「。」) much more often.
- Phrases repeated three or more times back to back (a speech-recognition loop) are reduced to one in the cleaned
  transcript.

## [1.2.1] - 2026-10-03

### Fixed

- Reading versions (`transcript.txt`) of long recordings whose transcript has little punctuation (common for lectures)
  produced very long paragraphs; such stretches are now split at the pauses marked by the speech recognizer.
  Reading files written by 1.2.0 are updated automatically on the next start (files you edited are left alone).

## [1.2.0] - 2026-10-03

### Added

- Each recording folder now also has reading versions without timestamps: `transcript.txt` (short paragraphs,
  broken at pauses, topic changes and speaker changes) and `summary.txt` (sections with owner and due date in plain
  words). Existing recordings get them automatically on the next start; text files you edited are left alone.

### Fixed

- A processing step that finished very quickly could still be shown as "pending" in metadata.json.
- Summaries no longer show "null" as an owner or due date.

## [1.1.0] - 2026-10-03

### Changed

- SmartRec is now installed with a regular installer (`SmartRec_1.1.0_x64.msi` / `_arm64.msi`): double-click, no
  certificate import and no administrator rights. Windows SmartScreen may ask once (More info → Run anyway) because
  the installer is not signed with a paid certificate. The installer also installs the Microsoft Windows App Runtime
  that SmartRec needs (including Windows notifications).
- Uninstall from Windows Settings › Apps removes SmartRec's settings, index, cache and logs; your Library, backups
  and downloaded models stay.

### Upgrading from 1.0.0 (MSIX)

- Uninstall the 1.0.0 app first (your Library is kept), then install 1.1.0. Settings start from defaults and the
  Library is indexed again automatically; models downloaded by the 1.0.0 app may need to be installed again.

## [1.0.0] - 2026-10-03

First stable release.

### Added

- Local AI summaries (optional): install "Qwen3 4B Instruct" (or "Phi-4-mini-instruct") in Settings › Models and
  summaries find decisions, action items with owner and due date, and open questions far more reliably than the
  extractive summary (see `docs/benchmarks/summary-v1`). Runs on the GPU when plugged in; falls back to the extractive
  summary automatically; Settings › Summaries can keep extraction only.
- yt-dlp for URL import can be installed from Settings › Import (pinned version, checked against its SHA-256).

### Fixed

- The uninstall flow of the installed app could delete a models folder shared with another SmartRec build even when
  "keep" was chosen. Models are now removed only when you choose to remove them.

### Changed

- Third-party notices list LLamaSharp/llama.cpp and the optional summary models and yt-dlp.

## [0.9.0-beta.1] - 2026-10-03

First beta of SmartRec: a local, offline recorder and transcriber for Windows 11.

### Added

- Recording: microphone (default) plus optional PC audio or a single application's audio, as separate aligned
  lossless streams written straight to disk; pause/resume, stop, discard (Recycle Bin), device selection,
  disconnect/reconnect, sleep handling, low-disk protection, crash recovery; tray icon, global hotkey
  (Ctrl+Alt+Shift+R) and close-to-tray while recording.
- Live transcript preview while recording, using local models (marked as a preview; nothing is stored).
- Local speech recognition after recording with whisper.cpp (Whisper large-v3-turbo / small) or sherpa-onnx
  (ReazonSpeech for Japanese), chosen for the PC's hardware from benchmarks; models are downloaded only when you
  install them in Settings, verified by hash, and can be verified/repaired/removed.
- Rule-based cleanup with your corrections preserved, extractive local summary (key points, decisions, action items,
  questions, topics), Japanese-capable full-text search.
- Library of plain files (`metadata.json`, transcript and summary as Markdown/JSON/TXT, audio) that stays readable
  without SmartRec; the database is only an index and is rebuilt from the Library when needed.
- Pages: Home, Library, Projects, Processing Queue, Search, Recording detail (player, transcript, summary; switchable
  arrangement; inline correction with double-click or F2), Settings.
- Import of audio/video files (drag and drop or picker) and of media URLs through a yt-dlp.exe you select.
- Durable background jobs that respect battery, Energy Saver, CPU/RAM/disk pressure and active recordings.
- Recording retention: lossless (default), M4A or MP3 after processing, or delete audio after processing (opt-in).
- Backup and restore (`.smartrec-backup`), optional weekly automatic backups.
- Maintenance: storage usage, Clear cache, Reset settings, Factory reset (keeps the Library), guided uninstall.
- Windows notifications for finished/failed processing, single-instance activation, Japanese and English UI,
  light/dark theme, accessible names for every control.

### Known limitations

- Beta: installation of this package, notifications in the installed app, and the uninstall → reinstall flow have not
  yet been verified on a machine that trusts the SmartRec certificate (see release notes).
- Not yet measured on battery laptops, ARM64 devices or PCs with an NPU.
- Summaries are extractive (sentences from the transcript); a local LLM summary is under evaluation.
- URL import needs a yt-dlp.exe that you download yourself.
