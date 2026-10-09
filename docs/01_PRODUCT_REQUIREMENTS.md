# 01 — Product Requirements

## Recording

MUST:

- start, pause, resume, stop, cancel;
- select a microphone;
- capture the microphone by default;
- capture system audio when the user enables it (off by default; see `03_AUDIO_CAPTURE.md`);
- allow application-scoped capture as an Advanced feature where supported;
- stream long recordings to disk;
- show the active capture source;
- preserve a recoverable state when a device disconnects.

Recording entry points:

- main app;
- system tray;
- configurable global hotkey.

The default experience should not pop a floating window over other applications. Compact recording/live-transcript windows are optional and user-configurable.

## Import

MUST support:

- drag/drop of supported audio/video files;
- Windows file picker;
- supported media URL input.

Do not monitor the clipboard continuously.

## Projects and organization

- Projects are the primary organizational unit.
- Tags are supported.
- Recording Profiles may be provided as presets (for example Lecture/Meeting/Quick Note) but must not be mandatory.

## Processing

After recording, Kakitome can automatically:

1. normalize/preprocess audio;
2. run final ASR;
3. run transcript cleanup;
4. optionally run diarization/quality analysis;
5. generate a local summary;
6. update search indexes.

Each stage is independently retryable when feasible.

## Transcript

The transcript supports:

- language;
- segment timestamps;
- optional word timestamps;
- optional speakers;
- confidence/quality signals;
- raw/clean/manual-edit lineage.

Low-risk cleanup may be automatic. Higher-risk rewriting should be presented as a user-approvable suggestion.

## Summary

Summary generation is optional and configurable per recording/profile. The UI renders structured sections such as key points, decisions, action items, and questions. The underlying result remains structured data.

## Search

Phase 1: local SQLite FTS5.
Later: local embeddings/vector search if benchmark and scale justify it.
No hosted vector database.

## Queue

The processing queue is durable and supports:

- pending/running/paused/succeeded/failed/cancelled;
- cancel;
- retry;
- recovery after crash;
- resource-aware scheduling.

## Notifications

Use Windows notifications for background completion/failure and an in-app processing view. Notification actions should only open the relevant Kakitome screen; they do not perform destructive actions.
