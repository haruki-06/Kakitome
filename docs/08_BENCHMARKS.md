# 08 — Benchmarks / Model Selection

## Principle

Model/provider names are not product invariants. The default is selected by reproducible benchmark on representative laptop hardware.

## Target environments

Benchmark at least:

1. x64 laptop without discrete GPU;
2. x64 laptop with discrete GPU;
3. Copilot+ / NPU-capable Windows 11 laptop when available;
4. ARM64 Windows laptop when dependency-compatible.

## ASR metrics

Measure at minimum:

- Japanese CER/WER;
- proper nouns;
- technical terms;
- numbers;
- mixed Japanese/English;
- filler handling;
- timestamps;
- speaker attribution when supported;
- throughput/latency;
- RAM/VRAM/NPU use;
- battery impact.

Representative corpus should include lectures, meetings, interviews, casual speech, system-audio-heavy material, multi-speaker material, and noisy recordings.

## Live ASR

Compare Windows AI Speech Recognition against any local fallback that meets the streaming requirement.

## Final ASR

Candidate pool may include Windows AI Speech, Whisper-family models, Qwen3-ASR, and other current local engines that satisfy licensing/deployment constraints.

## VAD / diarization / embeddings

Benchmark only if their quality materially changes the user experience. Do not add a model solely because it is fashionable or technically interesting.

## Local LLM

Evaluate:

- Japanese summary quality;
- structured-output reliability;
- latency;
- RAM/VRAM/NPU use;
- battery/thermal impact;
- user-facing interference.

Candidate runtimes may include Windows ML, Foundry Local, or another local runtime if it substantially improves the product.

## Decision engine

Evaluate rule-based decisions first. Add a local decision model only when rules are insufficient; it must be free, local and win against the rules.

## Selection rule

Prefer a hardware-aware Pareto choice over a universal single winner. The benchmark report must record:

- dataset/corpus version;
- hardware;
- software/model versions;
- metrics;
- known limitations;
- chosen defaults;
- rejected alternatives.

Record material decisions in ADRs.
