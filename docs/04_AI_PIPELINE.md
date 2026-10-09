# 04 — AI Pipeline

## Capability model

The product uses capability interfaces rather than a single AI vendor:

```text
ILiveAsrProvider
IFinalAsrProvider
IDecisionEngine
ISummaryProvider
IEmbeddingProvider
```

Exact interface names are AGENT DECIDES.

## Live ASR

Preferred path: Windows AI Speech Recognition when supported and benchmarked for the target environment.
Fallback: another local streaming-capable ASR provider.

Live ASR is a preview and should optimize for responsiveness rather than maximum final accuracy.

## Final ASR

Benchmark current local candidates. Candidate set may include:

- Windows AI Speech Recognition (where suitable);
- Whisper/Whisper-family models;
- Qwen3-ASR and other current local ASR models when deployment/licensing/performance are acceptable.

Do not hard-code a model name into product policy until benchmark results are recorded.

## Decision / cleanup

Use a rule-first, local-model-second strategy.

Good decision tasks:

- identify filler candidates;
- identify repetition/false-start candidates;
- classify recording type;
- detect suspicious transcript segments;
- select a summary profile;
- route between bounded local processing choices.

A decision model is optional. It is not a required dependency and must never be treated as a general autonomous agent.

## Transcript cleanup

Pipeline:

```text
Raw transcript
  -> candidate detection
  -> bounded local decisions
  -> low-risk deterministic cleanup
  -> Clean transcript
  -> high-risk edit suggestions (optional)
```

Raw, Clean, and user-edited states remain distinguishable.

## Summary

Summary is local-only in the current product.
The result is structured data rendered into Markdown and UI sections.

Recommended schema concepts:

- title
- overview
- keyPoints
- decisions
- actionItems
- questions
- topics

The exact JSON schema is finalized during implementation and benchmark work.

## Embeddings / semantic search

Deferred until SQLite FTS5 and core search are complete. Use only local embedding models and a local vector index if later benchmarks show meaningful benefit.

## Model Manager

Models are downloaded only when needed or explicitly requested. Model packages are stored outside the Library. The user can remove installed models without deleting recordings.
