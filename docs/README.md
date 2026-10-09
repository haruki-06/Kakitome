# Kakitome Specification

This directory contains product constraints, user-facing behavior, data contracts, benchmark rules, and
acceptance criteria for a clean-room rebuild of Kakitome.

## Authority

Read these in order:

1. `00_PRODUCT_INVARIANTS.md`
2. `01_PRODUCT_REQUIREMENTS.md`
3. `02_UX.md`
4. `03_AUDIO_CAPTURE.md`
5. `04_AI_PIPELINE.md`
6. `05_LIBRARY_DATA.md`
7. `06_STORAGE_POWER_RECOVERY.md`
8. `07_SECURITY_PRIVACY.md`
9. `08_BENCHMARKS.md`
10. `09_TESTING_ACCEPTANCE.md`
11. `10_RELEASE.md`
12. `11_DECISIONS.md`

Documents about the development process itself (agent workflow, implementation contract, session handoff, the
legacy-behavior reference) are kept in the development repository only.

## Priority vocabulary

- **MUST** — fixed requirement; do not change without user approval.
- **SHOULD** — strong default; may change with a documented material improvement.
- **BENCHMARK** — decide through reproducible measurement.
- **AGENT DECIDES** — implementation detail delegated to Claude Code.
- **DEFERRED** — intentionally postponed; do not implement unless required by a MUST.

## Scope discipline

The specification intentionally does not prescribe every class, database index, UI coordinate, retry constant,
or internal algorithm. Those are implementation decisions and should be left to the coding agent unless they
materially affect product behavior.
