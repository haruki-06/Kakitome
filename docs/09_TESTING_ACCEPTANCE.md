# 09 — Testing / Acceptance

## Core acceptance

A release candidate must prove:

- fresh install works;
- app runs without Internet after model installation;
- recording works on battery and AC;
- long recordings do not require all audio in RAM;
- device disconnect is recoverable;
- app restart recovers durable jobs;
- Summary failure never destroys a valid transcript;
- SQLite/index corruption does not erase the Library;
- Library can be opened without Kakitome;
- export files are readable outside Kakitome;
- Factory Reset keeps Library;
- uninstall flow does not silently delete Library;
- cache cleanup does not delete canonical data.

## Laptop acceptance

Test on realistic laptop scenarios:

- 8 GB RAM minimum boot/runtime scenario;
- 16 GB recommended scenario;
- no discrete GPU;
- battery power;
- Energy Saver;
- limited free disk;
- thermal throttling;
- sleep/wake;
- lid close behavior where supported;
- x64 and ARM64 when dependencies allow.

## Audio acceptance

Test:

- microphone only;
- microphone + system audio;
- application loopback when supported;
- device reconnect;
- multi-hour capture;
- pause/resume;
- cancellation;
- malformed media import.

## Data integrity

Power-loss/crash simulation should confirm that:

- no canonical Library file is silently lost;
- incomplete jobs are recoverable;
- temporary artifacts can be cleaned safely.

## UI acceptance

Keyboard navigation, screen reader semantics, focus visibility, light/dark mode, scaling, notifications, tray behavior, and single-instance activation must be tested.
