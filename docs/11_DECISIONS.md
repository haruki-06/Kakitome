# 11 — Decisions / ADR Summary

## ADR-001 — Clean-room rebuild

The new application must not reuse the legacy source architecture.

## ADR-002 — Windows 11 24H2+

Chosen to target modern Windows AI and platform capabilities rather than retain older Windows compatibility.

## ADR-003 — WinUI 3 / Windows App SDK

Chosen as the native Windows UI/application layer. Current Stable as of 2026-09-30: Windows App SDK 2.5.1. [Source](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads) / [Release](https://github.com/microsoft/WindowsAppSDK/releases/tag/v2.5.1)

## ADR-004 — .NET 10 LTS

Chosen over short-term releases. As of 2026-09-30, .NET 10 is LTS and supported through 2028-11-14. [Source](https://dotnet.microsoft.com/en-us/platform/support/policy)

## ADR-005 — Laptop-first

Battery, thermals, RAM, disk, sleep/wake, and foreground responsiveness are part of the product definition rather than afterthoughts.

## ADR-006 — Zero-cost local AI

The product has no mandatory paid AI service, cloud AI, or server dependency. Cloud AI is not part of the current release.

## ADR-007 — Library as canonical portable data

Markdown/JSON/TXT/audio artifacts are user-owned canonical data. SQLite is rebuildable state/indexing, not the sole copy.

## ADR-008 — Live ASR vs Final ASR

Live transcription prioritizes responsiveness; final transcription prioritizes quality. The providers may differ.

## ADR-009 — Benchmark-based model choice

ASR, VAD, diarization, embedding, decision models, and local LLMs are selected by measured quality/performance on representative laptop hardware.

## ADR-010 — Optional Decision Engine

`IDecisionEngine` is the product contract. A free/local decision model may replace the rule-based engine if it wins
the benchmark (ADR-039).

## ADR-011 — No Full Reset

Maintenance actions are Clear Cache, Reset Config, Factory Reset (keeps Library), and Uninstall. A separate Full Reset is intentionally omitted.

## ADR-012 — Local-first AI runtime

Windows ML and Foundry Local are preferred evaluation paths for local AI because they align with current Windows AI capabilities and hardware acceleration.

## ADR-013 — Windows notifications

New WinUI 3 / Windows App SDK apps use `AppNotificationManager` rather than legacy UWP toast APIs. [Source](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/)

## ADR-014 — Storage separation

Library is movable; Models are separate and normally fixed/default; Cache is rebuildable; AppData is resettable.

## ADR-015 — No application-level Library encryption in v1

Protect credentials/secrets strongly; rely on OS/device protections for user Library data.

## ADR-016 — Product name is "SmartRec"

"SmartRec vNext" was a working title used only while the requirements were being defined. The product, app,
package identity, assembly/namespace prefix, display names, documentation, and release assets all use
**SmartRec**. (User decision, 2026-09-30.)

## ADR-017 — Solution layout and build tooling (M0)

- Projects: `SmartRec.Domain` (net10.0, BCL only), `SmartRec.Application` (net10.0, Microsoft.Extensions
  abstractions only), `SmartRec.Infrastructure` and `SmartRec.App` (`net10.0-windows10.0.26100.0`, min 26100 =
  Windows 11 24H2), `SmartRec.Tests`. Dependency direction is enforced by `DependencyDirectionTests`.
- Central package management (`Directory.Packages.props`); SDK pinned by `global.json` (10.0.401, latestFeature).
- `.slnx` solution with x64 and ARM64 platforms; both build from the CLI.
- Tests: xunit.v3 on Microsoft.Testing.Platform (the .NET 10 `dotnet test` mode; VSTest is not supported by
  xunit.v3 4.x on .NET 10). Run with `dotnet test --solution SmartRec.slnx -p:Platform=x64`.
- Warnings are errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`).
- Developer launch uses an unpackaged build (`tools/Run-Dev.ps1`, `SmartRecUnpackaged=true` → isolated
  `bin/unpackaged`, `obj/unpackaged`) so no Developer Mode or package registration is needed; the shipped
  product remains MSIX. `dotnet build -o` is avoided because it corrupts the PRI paths of compiled XAML.
- MSIX identity `Name="SmartRec"`, `Publisher="CN=SmartRec"`; the self-signed certificate created in M8 MUST
  use the subject `CN=SmartRec` so upgrades stay in place.

## ADR-018 — Library file format details (M1)

Extends (does not change) the canonical Library contract in `docs/05`:

- Every JSON file carries `schema` (`smartrec.metadata` / `smartrec.transcript` / `smartrec.summary`) and
  `schemaVersion` (1). JSON is indented UTF-8 without BOM, `\n` line endings, camelCase names, enums as
  camelCase strings, non-ASCII (Japanese) unescaped. Readers tolerate a BOM and `//` comments; unknown properties
  are preserved on rewrite (`[JsonExtensionData]`).
- `transcript.json` / `summary.json` are **always** written; Markdown (default on) and TXT (default off) are the
  user-configurable readable views. JSON is what lets the Library be re-indexed without SQLite.
- Raw / Clean / Edited lineage lives inside `transcript.json`: `kind`, `revision`, `lineage[]`, and per segment
  `rawText` (original ASR text when changed) + `edited` (user edit; cleanup must not overwrite it).
- Times are seconds (`startSeconds`, `durationSeconds`, …); readable files use `[HH:MM:SS]`.
- Additional audio streams are independent files `audio.<suffix>.<ext>` (e.g. `audio.system.flac`) listed in
  `metadata.json` `audio[]` with a `role`; no forced single mix.
- Readable files are written in the transcript language (ja labels for `ja*`, otherwise en).
- Atomic writes: `.smartrec-tmp-*` file in the same folder → flush → replace. Stale temp files are the only
  thing SmartRec deletes during scans.
- External edits: SmartRec stores a SHA-256 stamp of every file it writes. JSON edited outside the app is adopted
  (it is canonical); an edited `.md`/`.txt` is never overwritten silently — the save is refused, and if the user
  chooses to overwrite, the edited file is first kept as `<name>.conflict-<yyyyMMdd-HHmmss>.<ext>`.
- Recording identity is `metadata.json` `id` (UUIDv7); folders may be renamed/moved by the user and are
  re-associated on the next sync. Problem folders (no/invalid metadata, duplicate id) are reported, never modified.
- Default Library root: `Documents\SmartRec\Library` (outside MSIX package storage).

## ADR-019 — Portable storage project and database recovery (M1)

- Library file I/O and SQLite live in `SmartRec.Storage` (`net10.0`, no Windows APIs), separate from the
  Windows-specific `SmartRec.Infrastructure`. This also lets `dotnet ef` (local tool in `.config/dotnet-tools.json`)
  generate migrations: `dotnet ef migrations add <Name> --project src/SmartRec.Storage --startup-project
  src/SmartRec.Storage --output-dir Persistence/Migrations`.
- SQLite (EF Core 10, WAL) at `%LOCALAPPDATA%\SmartRec\Data\smartrec.db`. At startup `PRAGMA quick_check` runs;
  a corrupt/non-database file is moved to `smartrec.db.corrupt-<time>` (never deleted), a fresh database is
  created and the index is rebuilt from the Library. Library files are untouched throughout (tested).

## ADR-020 — Recording architecture (M2)

- **Capture**: own event-driven shared-mode WASAPI loop over NAudio 3.1 `AudioClient` (NAudio.Wasapi only) on a
  dedicated MMCSS "Audio" thread. Windows converts to the canonical format (AUTOCONVERTPCM + SRC_DEFAULT_QUALITY):
  microphone 48 kHz mono float32, system/app loopback 48 kHz stereo float32. Per-application capture uses process
  loopback (`ActivateProcessLoopbackAsync`, build ≥ 20348).
- **Real-time safety**: the capture callback only copies into an unbounded channel (pooled buffers); one writer
  task per stream performs all file I/O.
- **Canonical capture file**: float32 WAV (`audio.wav`, extra streams `audio.system.wav` / `audio.app.wav`),
  checkpointed every second (fsync + header rewrite), promoted to RF64 past 4 GiB. `WavRepair` fixes headers after
  a crash without touching audio bytes. Compression to the user's retention format (FLAC/MP3/M4A) is a later
  durable job, never done at capture time.
- **Timeline**: a session clock that excludes pauses and sleep. Streams that go idle (device loss, silent loopback)
  are padded with silence against that clock, so independent streams stay aligned; continuous capture is never
  altered (no drift correction).
- **Device loss**: the stream keeps its file and retries every second; after 5 s without the chosen device it falls
  back to the default device. Events (`deviceLost/Restored/Switched`, pauses, sleep/wake, low disk, recovered) are
  logged in `metadata.json` `capture.events` with timeline offsets.
- **Power/disk**: an idle-sleep power request (`PowerCreateRequest`) is held while recording (setting); suspend
  pauses and resume continues the same files. Start is refused below 256 MB free; below 64 MB during recording it
  stops and keeps the audio.
- **Cancel** moves the folder to the Recycle Bin (fixed local drives only); otherwise the audio is kept and marked
  `cancelled`. A start that fails before capturing removes only its own empty files/folder.
- **Crash recovery** runs at startup under the same lock as Start, so a recording being created is never "recovered".
- **Tray/hotkey**: a hidden Win32 window on the UI thread (Shell_NotifyIcon v4, RegisterHotKey, TaskbarCreated
  re-registration); default toggle hotkey `Ctrl+Alt+Shift+R`. Closing the window while recording hides it to the
  notification area; Exit stops and saves first.

## ADR-021 — Durable jobs and resource-aware scheduling (M3)

- **Model**: one job per stage per recording (`audio.analyze` → `asr` → `cleanup` → `summary` → `index`), chained by
  `DependsOn` and grouped by `PipelineId`. Rows live in SQLite (`Jobs` table, app state). Every transition is
  persisted before it takes effect; handlers persist resume points via `JobContext.SaveCheckpointAsync` and must be
  idempotent. Stage outcomes are mirrored into `metadata.json` `processing[]` so the Library records what ran.
- **States**: Pending / Running / Paused (user) / Succeeded / Failed / Cancelled, plus a `WaitReason` explaining why
  a pending job is not running (dependency, retry backoff, on battery, Energy Saver, low battery, system busy, low
  memory, low disk, recording in progress, concurrency limit).
- **Failures**: `TransientJobException` and ordinary exceptions retry with backoff 30 s × 4ⁿ (cap 1 h) up to
  `MaxAttempts` (3); `PermanentJobException` fails immediately. Cancel cascades to dependents; Retry restores them.
- **Crash recovery**: jobs found `Running` at startup return to `Pending` and resume from their checkpoint.
  Recordings that finished but were never queued (app closed right after Stop) are queued at startup.
- **Policy** (`ResourcePolicy`, pure + unit-tested): Light jobs run except on low disk (< 1 GB). Heavy jobs never run
  while recording; stop at ≤ 15 % battery on battery power in every mode; `Auto` defers on battery or Energy Saver;
  `BatterySaver` also needs ≥ 50 % battery; `AlwaysProcess` runs on battery. CPU > 85 % (other processes, smoothed)
  or < 1.5 GB free RAM only block new heavy starts (no preemption, to avoid thrashing); power/recording/disk
  changes preempt running heavy jobs, which go back to Pending without consuming an attempt. Concurrency: 1 heavy,
  2 light. Budget: on AC `cores−2` threads (max 8), otherwise `cores/4` and "prefer efficiency".
- **Thermal**: Windows has no unprivileged thermal-sensor API. Energy Saver (engaged by Windows under battery/thermal
  pressure) and sustained foreground CPU load are used as the proxy; recorded as a known limitation.
- **Signals**: `GetSystemPowerStatus` (AC, battery %, Energy Saver flag), `GlobalMemoryStatusEx`, `GetSystemTimes`
  minus own-process CPU, sampled every 3 s; transitions wake the scheduler immediately.
- Finished job history is purged after 30 days. The Processing Queue page is M6.

## ADR-022 — Final ASR engines and default model (M4, BENCHMARK)

- Capability: `IFinalAsrProvider`/`IAsrSession` (Application); engines in Infrastructure: whisper.cpp via
  Whisper.net 1.9.1 (MIT; Vulkan → CPU runtime order, GPU skipped when the scheduler asks for efficiency) and
  sherpa-onnx 1.13.8 (Apache-2.0). Models are downloaded only on explicit user action, pinned by size + SHA-256
  (git blob SHA-1 for small non-LFS files), verified before use, stored in `%LOCALAPPDATA%\SmartRec\Models`.
- Benchmark v1 (`docs/benchmarks/asr-v1/`): defaults are hardware-aware — discrete GPU → Whisper large-v3-turbo
  q5_0; CPU-only/iGPU → Whisper small q5_1; ReazonSpeech k2 v2 int8 is the fast Japanese-only option (and the
  fallback when it is the only Japanese model installed). A user-selected model always wins.
- The `asr` job transcribes each audio source separately (one speaker per source), in VAD chunks of 25–30 s cut
  at pauses with silent chunks skipped, checkpointing after every chunk.
- Known limitation: measured on a desktop only; laptop/NPU/ARM64 matrix untested (see DECISION.md).

## ADR-023 — Cleanup, summary and search (M5)

- `IDecisionEngine` (rules-v1): bounded outputs only (edit candidates, flags, recording type); never acts.
  Low-risk edits (standalone hesitations, immediate repetitions, extra spaces) are applied to the Clean transcript
  with the original kept in `rawText`; ambiguous fillers (あの/その/まあ…) become `suggestions[]`; user-edited
  segments are never changed; segments are never removed.
- Summary: `ISummaryProvider`; the default `extractive-v1` provider builds the structured summary only from sentences
  actually said (key points by term salience, decision/action/question cues, owner/due extraction, topics). It is
  offline, deterministic and Light. A local LLM provider is a later, benchmark-gated addition (needs a model
  download approved by the user). Summary failures never touch the transcript.
- Search: SQLite FTS5 with the trigram tokenizer (Japanese substring search without segmentation); queries
  shorter than 3 characters fall back to a substring scan; terms are passed as quoted phrases (no FTS syntax
  injection). The index is derived (`index` pipeline stage) and rebuilt from the Library after database loss.

## ADR-024 — UX shell, import, notifications and single instance (M6)

- Presentation layer: `SmartRec.Presentation` (net10.0) holds the view models and UI-facing policies
  (`NotificationPolicy`, `NoticeArguments`) behind small UI service interfaces (`IUiDispatcher`, `ILocalizer`,
  `INavigationService`, `IShellService`, `INotificationSink`). The WinUI app only binds and adapts; the logic is
  unit-tested without WinUI.
- File import decodes with Windows Media Foundation (WAV reader first, then MF); FFmpeg is **not** shipped. The
  original is only read; the copy lands as `audio.<ext>` via a temporary name in an `import.file` job, then the normal
  pipeline runs. Formats MF cannot decode fail the job permanently with a readable message (the recording stays).
  Retention conversion that needs an encoder is revisited with M7 maintenance (MF encoders first).
- URL import: `import.url` job using **yt-dlp**, which SmartRec does not bundle. It runs only from an executable the
  user selected in Settings > Import (never searched on PATH) or from a future managed install pinned by SHA-256
  through the model store (pending: pinning requires reading/downloading the release, which needs the user's
  approval). Invocation is an argument list with `--ignore-config` (user config cannot inject `--exec` etc.),
  `--no-playlist`, and `--` before the URL; URLs are validated (http/https, host, no credentials, no whitespace);
  only single-file formats are requested (no FFmpeg merge). Download errors are classified permanent/transient.
  Error text (may contain the URL) goes to the job, not to logs. A dropped link only prefills the URL dialog.
- Notifications: `AppNotificationManager`; completion (summary ready) and terminal failures are coalesced for 3 s,
  suppressed while SmartRec is in the foreground or when turned off; clicks only navigate (recording detail, Library,
  Queue, Settings) — arguments are parsed defensively and unknown actions open Home. MSIX declares the toast COM
  activator. Unpackaged dev builds need the Windows App Runtime main/singleton packages; without them registration
  fails and the app logs a warning and continues (in-app views still show results).
- Single instance: custom `Main` (`DISABLE_XAML_GENERATED_MAIN`) registers an `AppInstance` key (one per AppData root
  so isolated test instances do not collide) and redirects other launches, which bring the window forward.
- Recording detail arrangement (transcript | summary, summary | transcript, summary above) is a cycling command (no
  flyout: identical for mouse, keyboard and screen readers), persisted in settings. F2 edits the focused line.
- UI verification: `tools/Test-UiPages.ps1` drives every page with UI Automation and targeted window messages only
  (no global keystrokes, screenshots via `PrintWindow`), runs the real pipeline on an imported file, checks search,
  layout switching, URL-import guidance, and that every keyboard-focusable control exposes an accessible name.
  `tools/Test-SingleInstance.ps1` checks activation redirection.

## ADR-025 — Maintenance, retention, backup, live transcript (M7)

- Data locations (docs/06, docs/10): MSIX keeps its default file-system virtualization, so AppData and Cache are
  package-private and removed by Windows on uninstall. Models live under AppData and are therefore removed with the
  package as well; the uninstall flow says so honestly (they are re-downloadable). Backups default to
  `Documents\SmartRec\Backups` next to the default Library, so a "keep" choice is honored. Unpackaged (dev) builds
  delete models themselves when the user chooses to.
- Maintenance: Clear Cache deletes only `Cache\` (in-use files skipped); Reset Config resets settings only; Factory
  Reset writes a marker and restarts — at the next start, before the database/settings/logs are opened, Data, Cache,
  Models, Logs and settings are deleted; the Library is untouched and re-indexed. Uninstall: Library and Backups are
  kept unless the user chooses Recycle Bin; then the Windows uninstall page opens and SmartRec exits.
- Retention (`audio.retain`): raw (lossless WAV) is the default. M4A (AAC) / MP3 at 192 kbps use the Media Foundation
  encoders that ship with Windows (no FFmpeg). Conversion runs only after every processing step succeeded, writes a
  temporary file, verifies it decodes with a matching duration, updates `metadata.json`, then deletes the WAV.
  Delete-after-processing is an explicit opt-in with a confirmation; `audioRemoval` is written to `metadata.json`
  before files are deleted. Imports keep the user's file as imported.
- Backup: `.smartrec-backup` = ZIP + versioned `manifest.json` (per-file size + SHA-256); written as `.partial`,
  verified, then published. Restore verifies everything first, rejects unsafe paths and newer versions, never
  overwrites (same recording id skipped; a taken folder name is restored beside it), stages then renames. Automatic
  weekly backups are opt-in; due-ness comes from the newest backup file; the latest 3 automatic ones are kept and
  manual backups are never rotated.
- Live transcript (docs/04 "Live ASR", BENCHMARK): Windows App SDK 2.5.5 exposes no on-device speech-recognition API
  (its AI package has text/imaging/vision only), so the "local streaming-capable fallback" is used: per-utterance
  decoding with the installed local models behind an energy VAD (partial text every 2 s, final at 0.6 s pauses or
  10 s), capture-thread taps that only copy, and dropping audio when decoding falls behind. Model choice favours
  responsiveness: GPU turbo on AC power, otherwise ReazonSpeech (Japanese), otherwise Whisper small. Whisper runs in a
  short-utterance mode (encoder window sized to the input). Measured on the dev PC (Ryzen 7 9700X, 2 threads, CPU),
  synthetic corpus v1: ReazonSpeech CER 11.1 % at decode RTF 0.06 (lecture-01); Whisper small CER 14.8 % at RTF 0.48
  (was 2.6 with the full 30 s window); English Whisper small CER 0 % at RTF 0.32. Laptop/NPU figures are unmeasured
  (no such hardware here) — recorded in the benchmark matrix as untested.
- Per-application capture: the picker lists processes that own an audio session (WASAPI session manager), playing
  first; the process loopback backend (ADR-020) captures the chosen process tree.
- Local LLM summary: not adopted yet. The rule/extractive summary (ADR-023) satisfies the summary contract. Windows'
  built-in Phi Silica needs a Copilot+ NPU (absent here); any other local LLM needs a model download, which requires
  the user's approval — the benchmark (Japanese quality, structured output, latency, RAM/VRAM, battery) runs once a
  candidate is approved (HANDOFF question).

## ADR-026 — Packaging, signing and the first release (M8)

- One signed `.msixbundle` with x64 and ARM64 packages (`tools/Build-Release.ps1`: per-architecture `dotnet publish`
  with MSIX generation, `makeappx bundle`, `signtool sign`). Package version = `VersionPrefix.0` from
  `Directory.Build.props`; the script refuses a manifest whose version or Publisher does not match.
- The package is framework-dependent on `Microsoft.WindowsAppRuntime.2` (≥ 2.5.1). Bundling a self-contained
  Windows App SDK was not chosen: it would add size to every package and AppNotifications/AppLifecycle rely on the
  framework. The release notes explain installing the runtime when Windows asks for it.
- Native payload trimming: Whisper.net's Vulkan package adds Linux libraries unconditionally, and its CPU runtime
  folders for every Windows architecture are copied; build/publish targets in `SmartRec.App.csproj` drop the Linux
  files and other architectures' folders (bundle 142.5 MB → 84.8 MB). Each package was unpacked and its native PE
  machine types checked (x64 / ARM64).
- Signing (docs/10): self-signed `CN=SmartRec`, RSA 3072/SHA-256, valid until 2031-10-03, created once by
  `tools/New-SigningCertificate.ps1`; `.pfx`/`.cer` in `%USERPROFILE%\SmartRec-signing`, the random password only in
  Windows Credential Manager (`SmartRec:code-signing`). Without the one-time admin import into Trusted People,
  `signtool verify /pa` can only fail with the untrusted-root error; the build accepts exactly that case after
  checking the signer thumbprint.
- Versioning: the first public build is `0.9.0-beta.1` (package 0.9.0.0) published by the agent as a GitHub
  pre-release; `1.0.0` (package 1.0.0.0, strictly greater) is cut as a draft Stable release after the user has
  verified installation, upgrade and uninstall on a machine that trusts the certificate.
- Third-party notices: `THIRD-PARTY-NOTICES.md` and the `licenses/` texts ship in the package and are shown in
  Settings > About.

## ADR-027 — Local LLM summaries (BENCHMARK, docs/08 "Local LLM")

- Candidates (user-approved downloads): Qwen3-4B-Instruct-2507 Q4_K_M (Apache-2.0) and Phi-4-mini-instruct Q4_K_M
  (MIT), unsloth GGUF builds pinned to repository commit + SHA-256, run with llama.cpp through LLamaSharp 0.27.0 (MIT;
  CPU AVX/AVX2/AVX512 and Vulkan backends). Phi Silica was not available (no NPU).
- Method: `benchmarks/summary-v1/cases.json` (7 synthetic transcripts — weekly meeting, lecture, 1on1, customer call,
  interview, English stand-up, 72-line multi-chunk planning meeting) with reference decisions, actions (owner/due),
  open questions and key facts; `SmartRec.Bench summary [--cpu]` scores keyword-group recall, spurious items,
  invented numbers (kanji numerals normalized), citation coverage, output language, latency and peak memory.
  Output is constrained by a GBNF grammar (always valid JSON) and cites numbered transcript lines; timestamps come
  only from cited lines.
- Result (`docs/benchmarks/summary-v1/README.md`, Ryzen 7 9700X / RTX 5070 Ti): Qwen3-4B leads — decisions 75 %,
  actions 75–83 %, owners 64–82 %, dues 100 %, questions 80 %, facts 100 %, no invented numbers, every item cited;
  GPU 5 s per case (9 s for the long meeting), CPU 39 s (92 s long), ~4 GB peak process memory. Phi-4-mini is faster on
  CPU (21 s) but weaker on decisions/actions. The extractive summary: decisions 38 %, owners 27 %.
- Decision: the local LLM summary is offered as an optional model in Settings › Models ("Summary AI"); when installed
  it is preferred (Qwen3-4B first, then Phi-4-mini). The summary job is Heavy (deferred on battery per the processing
  mode), uses the GPU only on AC power, splits long transcripts into ~4.5k-token chunks with a reduce pass, and falls
  back to the extractive summary if the LLM fails. Settings › AI › "Summaries" can force extraction only. Nothing is
  downloaded without the user's confirmation.
- Limits: 8 GB laptops are tight with a 4 GB model in memory; CPU-only laptops will be slower than the desktop figures
  (untested hardware). The reference set is small and synthetic; it is a regression baseline, not a general quality claim.

## ADR-028 — Distribution as a per-user MSI instead of MSIX (user decision, 2026-10-03)

- Context: the self-signed MSIX (ADR-026) can only be installed after importing `SmartRec_signing.cer` into Trusted
  People with administrator rights. The user asked for "just run the installer". Options presented: per-user MSI
  (chosen), portable ZIP, MSIX plus a one-click script (still UAC). Without a paid certificate no option avoids the
  SmartScreen prompt for unsigned downloads; this was accepted. **This deviates from docs/10 "Primary package: MSIX"
  with the user's explicit approval**; docs/10 is not edited (protected), this ADR records the change.
- Package: WiX 5.0.2 (MS-RL; WiX 6+ moved to the Open Source Maintenance Fee EULA and is not used). Per-user scope
  (`%LOCALAPPDATA%\Programs\SmartRec`, no administrator rights), fixed UpgradeCode (major upgrades in place), Start
  menu shortcut, one MSI per architecture (`SmartRec_<version>_x64.msi` / `_arm64.msi`, the release script verifies
  the PE machine type inside each). The app is the unpackaged, self-contained build (`SmartRecUnpackaged=true`).
- Windows App Runtime 2.5.1: the Microsoft redistributable installer (user-approved download/redistribution) is
  embedded and run per user (`--quiet`, no elevation) during install; pinned by SHA-256 and required to carry a valid
  Microsoft signature. It provides the framework and the main/singleton packages, so Windows notifications work in
  the unpackaged app (verified: AppUserModelId registration appears after the runtime install).
- Data: unpackaged apps are not virtualized — AppData/Models/Cache live in the real `%LOCALAPPDATA%\SmartRec`.
  MSI uninstall removes app state (Data, Cache, Logs, settings) but never Models, Backups or the Library; the in-app
  uninstall flow removes models only when chosen (ADR-025 fix). Verified on the dev PC: install → run → uninstall
  (Library unchanged, models kept, app state removed).
- MSIX: no longer published. The code still supports packaged runs (manifest kept) should a trusted certificate or the
  Store be adopted later (Decision Gate). Users of 1.0.0 (MSIX) uninstall it first (Library kept); its package-private
  settings and any models downloaded inside the package are not carried over.

## ADR-029 — Reading versions of transcript and summary (user request, 2026-10-03)

- `transcript.txt` / `summary.txt` (already optional artifacts of the Library contract, docs/05) become reading
  versions and are written by default: no timestamps; the transcript is split into short paragraphs at pauses, speaker
  changes, topic openers (「次に、」「最後に、」「結論として、」… / "Next,", "Finally,") and every ≤3 sentences
  (≈120 characters Japanese, 320 otherwise); a short closing sentence before a pause is joined to what the same
  speaker says next; speakers are named only when they change and only when there are several; one header line with
  date, duration and project. The summary uses 【見出し】 sections and natural owner/due wording, hiding "null"-like
  values (also normalized when parsing LLM output).
- JSON stays the structured source; Markdown keeps the timestamps for navigation. No file is removed or renamed.
- Existing recordings get the reading versions at start-up (`LibraryService.RefreshReadableTextAsync`): missing files
  are created; an existing file is replaced only if it is SmartRec's earlier timestamped rendering and unchanged —
  text files were optional before, so others are treated as the user's and left alone.
- Found while testing: the pipeline wrote "pending" into metadata.json after queueing, which could overwrite the
  result of a stage that had already finished; pending is now written before the jobs are queued.

## ADR-030 — User glossaries, prompt style and decoding (user request + BENCHMARK, 2026-10-03)

- Context: on a real 87-minute Japanese lecture, Whisper large-v3-turbo got the content right but mis-wrote many domain
  terms as homophones (「信教の自由」→「新居の自由」, 「争議権」→「葬儀権」) and produced almost no punctuation; the
  previous-text prompt (unpunctuated) propagated that style.
- Decision (user direction): **no domain dictionary is built in**. Users import or write plain-text glossaries:
  `Library/Projects/glossary.txt` (all recordings) and `Library/Projects/<Project>/glossary.txt` (one project; wins
  on conflicts). One entry per line: a term (ASR hint) or `wrong -> right` (also `→`, `=>`; a correction applied in
  `cleanup`, original kept in `rawText`); `#` comments. They are ordinary user files in the canonical Library
  (portable, in backups, editable in any editor); a file SmartRec replaces or stops using is renamed, never deleted.
  docs/05 allows additional safe files; a glossary is user-authored, so it lives at project level, outside recording
  folders, and is read only by ASR/cleanup. Settings › Transcription › Glossary imports/edits/stops using a file and
  shows the format plus a copyable request text for an AI assistant (making the list with an AI is the user's choice;
  SmartRec sends nothing).
- Prompt: `AsrPromptBuilder` gives Whisper a punctuated hint list (≈110 chars ja / 400 en) followed by the end of the
  recognized text (≈90 / 300). Larger glossaries are chosen per chunk by topic: terms recognized in the last ~3 min,
  then terms sharing distinctive character bigrams (IDF-weighted) with the recent text, then rotation. With no hints
  and no previous text, Japanese gets a neutral punctuated opening 「はい。では、始めます。」 (a general style cue,
  not a dictionary).
- Decoding: beam search (width 5) was measured and **not adopted** — no accuracy gain on the lecture, +40 % time. The
  provider keeps the option (`AsrSessionOptions.BeamSize`) for future benchmarks. Back-to-back repeats of a 4–15
  character phrase (3+ times, a decoder loop or stammer) are collapsed by the rule-based cleanup (low risk, raw kept).
- Benchmark: docs/benchmarks/asr-glossary-v1. Glossary with topic selection: domain-term accuracy 60 % → 72 %,
  「。」 1.7 → 14.7 and 「、」 3.3 → 60 per 1000 chars, no decoder loops, RTF 0.036 → 0.044 (RTX 5070 Ti, Vulkan).

## ADR-031 — Glossary suggestions instead of automatic glossaries (user proposal + BENCHMARK, 2026-10-04)

- Context: the user proposed that SmartRec detect a poor transcript after recognition and then either build a glossary
  itself and transcribe again, or tell the user how to make one (with a prompt) and how to apply it.
- Benchmark (docs/benchmarks/asr-glossary-v2, 87-min lecture, 62 known misrecognition pairs): recognizer confidence
  does not reveal the errors (mean 0.89, no low-confidence segment despite ~90 wrong terms). The local Qwen3-4B
  "Summary AI" proposed no usable "wrong -> right" fixes (meaning-changing and reversed ones instead); its drafted
  terms used as hints raised accuracy 25 % → 34 % but also reinforced misrecognized forms it listed as terms, and cost
  a full second ASR pass. A correct glossary (terms only) raised it to 48 %.
- Decision: SmartRec does not build or apply glossaries on its own. `GlossaryTips` marks every transcript of at least
  5 minutes whose project has no glossary (shared or project). The recording page shows a tip: copy a request for an
  AI assistant (title and summary topics filled in; the user attaches `transcript.txt` — SmartRec sends nothing), open
  the folder, and "Import glossary and transcribe again" (imports into the recording's project glossary, keeping a
  previous file, then the usual re-transcribe confirmation). The completion notification adds a one-line hint. The tip
  can be turned off (Settings › Transcription › Glossary; `processing.glossaryTips`).
- The local draft experiment (`SmartRec.Bench glossary-draft`) stays in the benchmark tool; `LlamaRuntime` is now the
  shared llama.cpp helper. A reviewable local draft (the user edits it before use) can be reconsidered with a stronger
  local model.

## ADR-032 — Recording page: transcript or summary, as flowing text (user request, 2026-10-08)

- Context: docs/02 asks for a split view (player, transcript, summary) whose arrangement can be changed. The user found
  the line-per-segment list hard to read and asked for a reader like a transcript.md viewer, with transcript and
  summary switched rather than shown together, keeping the highlight of the part being heard.
- Decision (user-directed deviation from the docs/02 split view; docs/02 itself is unchanged): the page keeps the
  player on top and shows either the transcript or the summary, chosen with tabs and remembered
  (`general.detailLayout` = `transcript` | `summary`; old `summaryFirst` maps to summary, other old values to the
  transcript). The transcript is one selectable RichTextBlock of reading paragraphs that break exactly like
  transcript.md/.txt (`TranscriptRenderer.GroupParagraphs`, shared by the renderers and the reader), each with a quiet
  time stamp and the speaker. Every segment is a run: the one being heard is highlighted (TextHighlighter, ranges by
  character index) and scrolled into view; clicking text plays from that segment; double-click or F2 corrects it.
  Edit suggestions moved into a collapsed section above the transcript.
- Settings and the processing queue were reorganized in the same release (tabs/cards; per-recording cards).

## ADR-033 — Rename to Kakitome and open source (user decision, 2026-10-08)

- Context: the user wants to publish the app as open source. "SMARTREC" is a registered US software trademark of
  another company (Amilia, recreation management SaaS), and "Smart Recorder" is an existing recorder app. The private
  repository's history holds personal data (commit e-mail, local paths, a work log, details of the user's own
  recordings) and history rewriting is not allowed.
- Decision: rename to **Kakitome** (書き留め, "to write down") everywhere: projects/namespaces, executable,
  installer (new UpgradeCode — a separate product), `%LOCALAPPDATA%\Kakitome`, `Documents\Kakitome\Library`,
  `.kakitome-backup`, `KAKITOME_*` variables, notification/instance identities. The user is the only user so far:
  **nothing is migrated** from SmartRec and no compatibility code is kept (Decision Gate 4 approved by the user).
  The existing self-signed MSIX certificate keeps its subject `CN=SmartRec` and credential name (MSIX is not shipped).
- License: MIT. Public repository `haruki-06/Kakitome`, separate from the private development repository
  (kept private). `tools/Export-Public.ps1` publishes a tagged latest release as one commit (noreply
  author) without the paths in `.publicignore` (agent contract, session log, legacy reference, pre-rename records) and
  refuses to commit when personal/private markers are found.
- Release flow: user-facing changes are released as **pre-releases** on the private repository (`vX.Y.Z-beta.N` from
  `dev`). Only when the user explicitly asks for latest: `dev` → `main`, tag `vX.Y.Z`, latest release on both
  repositories (docs/10 Beta/Stable channels).

## ADR-034 — Download the recommended model and yt-dlp when missing (user request, 2026-10-08)

- Context: a fresh install cannot transcribe until the user finds Settings › Models; the user asked Kakitome to fetch
  the recommended model (and yt-dlp) by itself.
- Decision: at startup `AutoModelInstaller` queues ordinary `model.install` jobs for (a) the ASR model recommended for
  this PC (`AsrDefaults.RecommendedModel`, ADR-022) when no ASR model is installed and (b) yt-dlp when neither the
  managed copy nor a user-selected exe exists. A download already queued is not queued again. It does nothing on a
  metered connection (Windows connection cost; unknown counts as metered) and can be turned off
  (`processing.autoDownloadModels`, Settings › Models). Since 2.0.0-beta.3 (user request) also (c) the recommended
  summary model (`SummaryDefaults`: Qwen3-4B, only with 12 GB+ physical memory) when no summary model is installed
  and summaries are not set to extraction only. Automation runs (temporary app data) never download.
- Not silent (docs/10): the jobs are visible and cancelable in the queue, Home says which model is downloading, the
  completion notification is the usual one, files are verified against pinned hashes before use, and only missing
  items are fetched — nothing already installed is ever updated automatically.

## ADR-035 — GPU detection through Vulkan; integrated GPUs stay on the CPU (2026-10-08)

- Context (code review, user request): the GPU check read display-driver names from the registry (stale adapters,
  missed names, no memory size); whisper.cpp used any Vulkan device — including integrated GPUs — whenever not in
  efficiency mode, against ADR-022; llama.cpp offloaded the whole summary model without checking GPU memory and did not
  retry on the CPU.
- Decision: `GpuAccelerationProbe` enumerates Vulkan physical devices (the API both native runtimes use): type
  (discrete/integrated) and device-local memory. A discrete GPU with ≥ 2 GB is "capable" (largest wins) and is pinned
  for ggml through `GGML_VK_VISIBLE_DEVICES`. Integrated GPUs are not used (they report shared memory as device-local,
  e.g. 32 GB on the dev PC's Radeon iGPU) until benchmarked. whisper.cpp uses the GPU only with a capable GPU and not in
  efficiency mode; the summary model is offloaded only with ≥ 6 GB GPU memory on AC power and falls back to the CPU if
  the GPU run fails. `Kakitome.Bench hardware` prints what the probe sees.
- Verified on the dev PC (RTX 5070 Ti 16 GB + Radeon iGPU): the RTX is chosen; ASR and summary ran on Vulkan.
  Not verified: laptops with a small (2–4 GB) dGPU, iGPU-only PCs, ARM64 (no Vulkan runtime is bundled there).

## ADR-036 — NPU: Windows AI Speech Recognition is the target path, not usable yet (2026-10-08)

- Context: the user wants NPU support but has no NPU PC. Per-vendor routes (Windows ML execution providers QNN /
  OpenVINO / VitisAI with NPU-specific Whisper models) cannot be verified without that hardware.
- Finding: the Windows AI Speech Recognition API (`Microsoft.Windows.AI.Speech`) runs on the NPU of Copilot+ PCs and on
  the CPU elsewhere, accepts `ja-JP`, and reports per-phrase `Offset`/`Duration` in streaming mode — a good fit for a
  future `IFinalAsrProvider` that needs no model packaging by Kakitome. Experiment on branch
  `experiment/windows-ai-speech` (`experiments/WindowsAiSpeech/README.md`).
- Blockers: (1) only in Windows App SDK Experimental releases (not in stable 2.4/2.5); the 2.5.4-experimental runtime
  framework even misses class registrations; (2) needs package identity with `systemAIModels` — for the MSI that means
  a sparse package signed with a certificate users' PCs trust (as `CN=Kakitome`); (3) model preparation failed with
  `0x8000FFFF` on Windows 11 25H2 26200 (docs target 26226+), so even the CPU path could not be measured here.
- Decision: no NPU support in the product for now. Revisit when the API is in a stable Windows App SDK and the OS
  delivers the model; then benchmark it against Whisper (accuracy on the Japanese corpus, RTF, power) on CPU here and
  on a Copilot+ PC, and solve signing (free OSS signing or Store distribution).

## ADR-037 — Speaker diarization with sherpa-onnx, chosen by benchmark (user request, 2026-10-08) — WITHDRAWN

- Context: the user asked who-said-what in transcripts. Constraints: local, free, CPU-friendly, Japanese, no new
  native runtime.
- Decision: a `diarize` job after `asr` (`DiarizationJobHandler`, Heavy) using sherpa-onnx 1.13.8 (already bundled)
  offline diarization: pyannote segmentation 3.0 (MIT) + 3D-Speaker CAM++ zh/en "advanced" embeddings (Apache-2.0),
  34 MB in one catalog package `speaker-diarization-v1`, pinned by commit and SHA-256, auto-downloaded while the
  feature is on (default). Each audio stream is diarized on its own, so microphone and system audio never mix; a
  stream is split only when two or more voices are found. Segments get the speaker they overlap most; speakers are
  numbered "Speaker 1…" in order of appearance, written to transcript JSON/Markdown (Library stays canonical), and can
  be renamed on the recording page (a user revision). Settings › Transcription: on/off and an optional speaker count.
- Benchmark (`docs/benchmarks/diarization-v1`, synthetic Japanese conversations, 4 embeddings × thresholds): CAM++ at
  threshold 0.7 — 93 % mean turn accuracy, count right 6/7, RTF ≈ 0.05 on CPU. Forcing a given number of clusters was
  worse (76 %), so a known count instead steers the threshold in 0.05 steps (93 %, all counts right, RTF ≈ 0.07).
- Limits: synthetic voices only — real meetings (overlap, distant microphones) are not measured; overlapping speech
  gets one speaker. Revisit with a real-recording corpus.
- Update (2026-10-09, user report: "2 speakers shown as 95"): on a real 57-minute video the one-stage clustering found
  189 groups, and no threshold worked (0.9 → 76, 1.1 → 17, 1.3 → 1). Added a second stage (`SpeakerClustering`): one
  embedding per group from up to 20 s of its audio, the recording's mean voice removed (all groups were 0.5–0.9
  similar before), centroid-linkage merging at ≥ 0.1, scraps under 4 % joined to the closest speaker, and clusters
  whose original voices are ≥ 0.85 similar merged. Result: 2 speakers on the video, 90 % on the synthetic set (93 %
  with a known count, which now merges down to the count instead of steering the threshold). The 0.85 limit rests on
  one real recording; revisit with more (single-speaker lectures).

- Withdrawn (2026-10-09, user decision after testing beta.8 on real recordings: "not separated at all"): the feature
  is removed — `diarize` stage, settings, speaker models, rename dialog. Lesson: synthetic TTS conversations were far
  too easy and did not predict real audio; a real-recording corpus with known turns is needed before trying again.
  Queued `diarize` jobs from older versions are finished as skipped (`JobScheduler.RetiredKinds`) so later stages run.
  The sherpa-onnx ONNX Runtime preload (`SherpaNative`) stays for ReazonSpeech. docs/benchmarks/diarization-v1 is kept
  as the record.

## ADR-038 — Summary sections follow the kind of recording (user report, 2026-10-09)

- Context: the summary always filled a meeting template. On a variety video the 4B model produced "ate one more" as
  an action item, "owner: audio-50" (an internal speaker id) and "owner: nobody specified", and the final pass of a
  long recording kept a single key point.
- Decision: the model first states `kind` (meeting / lecture / conversation / other; enforced by the grammar). Decisions
  are kept only for meetings, action items and open questions for meetings and lectures (`LlmSummaryFormat.ApplyKind`);
  "meeting" covers any work talk (1-on-1s, calls, consultations, interviews). Passes over 10+ lines must give 3–8 key
  points (grammar). Speakers reach the prompt by name, not id; placeholder owners/dues ("指定なし", "未定"…) are
  dropped. The kind is stored as the summary's `profile`.
- Benchmark (summary-v1, Qwen3-4B GPU): decisions 75 → 88 %, actions 75 → 75 %, owners 64 → 73 %, due 100 → 89 %,
  questions 80 → 80 %, facts 100 → 100 %, spurious items 1 → 0. On the video: classified as conversation, 6–7 key
  points, no meeting sections.

## ADR-039 — No user-provided decision model (user decision, 2026-10-09)

- The specification allowed an optional user-provided decision model. The user dropped it: keeping it would conflict
  with the zero-running-cost principle. The rule-based `IDecisionEngine` stays; another decision model may replace it
  only if it is free, local and wins the benchmark. Mentions removed from docs/04, docs/08 and ADR-010 with the user's
  approval.

## ADR-040 — Update notice through the GitHub releases API (user request, 2026-10-09)

- Context: users of an unsigned MSI distributed on GitHub have no way to learn about new versions.
- Decision: `UpdateChecker` asks `GitHubReleaseFeed` once a day (and on "Check now") for
  `api.github.com/repos/haruki-06/Kakitome/releases/latest` — anonymous, no user data, pre-releases excluded by the
  API. A newer version (SemVer precedence; a release beats its betas) shows a Home notice and a Windows notification
  (once per version; closing the notice hides that version). The button opens the release page — only a page on the
  repository's own releases path is accepted; Kakitome never downloads or installs updates itself. On by default,
  `general.checkForUpdates` turns it off; automation runs never check. The network-use architecture test now allows
  this one file.

## ADR-041 — Log files and a diagnostics export (user request, 2026-10-09)

- Context: issues and a friend's field test need logs; the app logged only to the Windows event log (hard to find,
  mixed with other apps) plus `crash.log`.
- Decision: `FileLoggerProvider` writes `%LOCALAPPDATA%\Kakitome\Logs\kakitome-yyyyMMdd.log` (Kakitome Information+,
  other libraries Warning+ so EF Core SQL never lands there; background writer, 14 days, 20 MB/day) and replaces the
  default providers (Debug kept). `LogRedactor` replaces profile, Documents, LocalAppData and Library paths and the
  user/computer names in logs, crash.log and the export. Settings › Data › "Report a problem" saves one zip
  (`DiagnosticsService`): logs, system.txt (version, OS/CPU/GPU/memory, power, free space, audio devices, models),
  jobs.txt (last 300 steps with errors), settings.json — never audio, transcripts, summaries or titles; project and
  file names can appear in log lines (said in the UI). GitHub issue template and `docs/testing/field-test.md` ask for
  that zip.
