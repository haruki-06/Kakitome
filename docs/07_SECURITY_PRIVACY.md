# 07 — Security / Privacy

## Privacy baseline

No cloud AI is part of the current product. Audio, transcripts, summaries, and metadata remain local unless the user explicitly uses a future feature that is separately approved.

## Secrets

Core product has no required AI API key.

Keep an `ISecretStore` abstraction only as future-proofing. If credentials are later introduced, use Windows Credential Manager and/or DPAPI. Never store secrets in Library, JSON, SQLite, logs, or source control.

## Library security

Do not add application-level encryption to the Library in the initial release. Rely on Windows user permissions, NTFS, BitLocker, and user-controlled storage security.

## External processes

Use safe process invocation with argument lists. Do not construct shell command strings from user input.

Validate all paths. Prevent path traversal and unsafe output destinations.

## Model integrity

Downloaded models should be validated with checksum/metadata verification. Never load a partially downloaded or failed-verification model.

## Diagnostic data

Crash/diagnostic export is manual and user-initiated. Diagnostics must exclude:

- audio
- transcript contents
- summaries
- Library files
- credentials

unless the user explicitly chooses a separate sanitized export flow.

## Notifications

Use `AppNotificationManager` for Windows notifications in a new WinUI 3 / Windows App SDK app. Notifications should open the relevant Kakitome screen and should not perform destructive actions.
