# 05 — Library / Data Contract

## Canonical principle

The filesystem Library is the canonical user-owned content. Kakitome must remain useful when the user opens the Library outside the app.

## Recommended structure

```text
Library/
  Projects/
    <Project Name>/
      <Project Name>_<YYYY-MM-DD_HH-mm-ss>/
        audio.<retained-format>
        transcript.md
        transcript.json
        transcript.txt        # optional
        summary.md
        summary.json
        summary.txt          # optional
        metadata.json
```

The implementation may introduce additional safe files/directories if they are clearly derived and rebuildable.

## File generation

Transcript and summary formats are user-configurable and may be generated in multiple formats:

- md
- txt
- json

Default recommendation:

- Markdown + JSON
- TXT optional

## Transcript Markdown

Human-oriented format should use:

- readable paragraphs;
- speaker labels when available;
- timestamp links in a simple `[HH:MM:SS]` form;
- no custom syntax required to understand the file.

The app may use a custom internal URI only for in-app navigation; exported Markdown must remain readable without the app.

## JSON

JSON is structured and machine-readable. It should contain enough information for re-import/migration without requiring the SQLite database.

Transcript JSON should include, when available:

- language;
- segments;
- word timing;
- speaker IDs;
- confidence/quality metadata;
- engine/model lineage.

Summary JSON should include the structured summary sections and generation lineage.

## metadata.json

Contains recording/import/processing metadata, not secrets.

Examples:

- title/project
- created/recorded times
- source type
- duration
- retained audio format
- model/provider IDs and versions
- processing profile
- schema versions

## Naming

Recording directory name:

`<Project Name>_<timestamp>`

Invalid Windows filename characters are sanitized. User-facing Japanese is preserved when safe.

## External editing

v1 should detect external changes at safe synchronization points (for example app startup or Library refresh). If a conflict may overwrite user work, do not silently overwrite; offer a reload/reconcile path.

Continuous filesystem mirroring is not required.

## SQLite

SQLite stores:

- app state;
- durable jobs;
- fast indexes/search metadata;
- rebuildable derived information.

SQLite corruption must not make the canonical Library unrecoverable.
