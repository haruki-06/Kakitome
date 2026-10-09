# 03 — Audio Capture

## Platform

Windows 11 24H2+.

## Capture sources

Default:

- Microphone

Advanced:

- System audio
- Application-scoped loopback for supported Windows configurations
- Multiple sources as independent streams where practical

Do not force microphone/system audio into a single lossy mixed file as the only representation.

## Canonical processing format

Use an internal lossless floating-point PCM representation for processing. The exact bit depth/sample rate is AGENT DECIDES based on the capture and processing pipeline.

Original/canonical capture and ASR processing copies are separate concerns.

## Audio pipeline

```text
Capture
  -> durable raw/canonical artifact
  -> VAD / preprocessing copy
  -> ASR-ready audio
```

Noise reduction and echo cancellation should be applied only to processing copies unless a benchmark proves capture-time processing is safe and beneficial.

## VAD

Use VAD for:

- speech activity detection during recording;
- ASR chunking and workload reduction.

Never use VAD as an excuse to delete portions of the canonical recording.

## AEC

Use acoustic echo cancellation only when the selected capture configuration needs it and when the implementation improves microphone/system-audio separation without corrupting the stored recording.

## Retention

Recording retention is configurable:

- Raw
- MP3
- M4A
- Delete after all dependent processing succeeds

Do not delete the canonical input before all required jobs have completed.

The default retention is a lossless or user-chosen retained audio format — never deletion.
"Delete after all dependent processing succeeds" is an explicit user opt-in (per profile or global setting)
with a clear confirmation explaining that audio will no longer be available. When audio is removed this way,
`metadata.json` records that the audio was intentionally removed, when, and by which setting, so the Library
remains self-describing. This is user-directed deletion, not silent deletion.

## Source-aware ASR

Audio sources may be processed separately and mixed only when useful to the selected ASR strategy.
