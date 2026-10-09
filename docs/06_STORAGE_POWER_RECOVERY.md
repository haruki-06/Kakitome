# 06 — Storage / Power / Recovery

## Data roots

Recommended separation:

```text
Library/    user-owned, movable
Models/     downloaded models, normally fixed/default location
Cache/      rebuildable, fixed/default location
AppData/    settings, local app state, logs
```

Because the app ships as MSIX, package-private storage is removed on uninstall. Library (and Models/Backups
when the user chooses to keep them) must live outside package-private storage; see `10_RELEASE.md` →
"Package data location vs. uninstall".

Library location is user-changeable. Model relocation may be provided later as an Advanced feature only if move/recovery safety is proven.

## Cache

Cache contains only rebuildable data:

- temporary audio conversions;
- preprocessing artifacts;
- derived embeddings;
- thumbnails;
- temporary model/runtime files where safe.

Users can clear cache at any time.

## Storage behavior

Show storage usage by category:

- Library
- Models
- Cache
- AppData
- Backups

Never auto-delete canonical Library data to solve storage pressure.

On low disk space:

1. reduce/stop rebuildable background work;
2. suggest cache cleanup;
3. pause jobs that cannot safely complete;
4. refuse a new recording only when there is genuinely insufficient space to record safely.

## Battery / thermal behavior

Laptop-first policy:

- Auto: normal on AC; defer/reduce heavy background work on battery.
- Always Process: user opt-in; still respect OS critical power behavior.
- Battery Saver: aggressive deferral of heavy background AI.

Recording reliability outranks post-processing throughput.

Use Windows power APIs and resource signals where available. Background work should yield to foreground interaction and thermal/resource pressure.

## Sleep / lid

Recording must not silently fail because the app assumed a desktop power profile. Respect explicit user preferences where technically possible and clearly communicate when OS sleep policies prevent continued capture.

## Reset

### Clear Cache

Deletes rebuildable data only.

### Reset Config

Resets application settings without touching Library.

### Factory Reset

Resets app state, local indexes, models, cache, and credentials while keeping Library.

### Uninstall

Before opening the Windows uninstall UI, show a data breakdown and allow safe choices for Library/Models/Backups. Do not expose a separate Full Reset action.

## Durable jobs

States:

`Pending -> Running -> Paused/Succeeded/Failed/Cancelled`

Persist step state before and after major transitions.

## Crash recovery

On startup, recover nonterminal jobs by validating existing artifacts and resuming or offering retry. Do not blindly restart completed steps.

## Backup

Provide a portable backup container (recommended `.kakitome-backup`) containing a versioned manifest and canonical Library data. Models are not part of the backup because they are re-downloadable.

Automatic backups are optional and can be disabled.
