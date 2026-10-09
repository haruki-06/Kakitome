# Live transcript benchmark v1

Reproduce: `dotnet test --project tests/Kakitome.Tests -p:Platform=x64 --filter-class Kakitome.Tests.Asr.RealLiveTests --output Detailed`
(needs the synthetic corpus `benchmarks/corpus/v1` from `Kakitome.Bench corpus` and the models installed).

Method: the corpus WAV is pushed through the real `LiveTranscriber` (capture-format input → 16 kHz, energy VAD,
partial + final decodes) with the model opened exactly as the app does for the live preview (2 threads, CPU,
short-utterance mode). CER is computed on the final lines against the corpus text; decode RTF = time spent decoding
(partials included) / audio duration. RTF < 1 is required for a live preview.

| Model | Utterance mode | Clip | CER | Decode RTF | Decodes |
|---|---|---|---|---|---|
| ReazonSpeech k2 v2 int8 (sherpa-onnx 1.13.8) | per utterance | lecture-01 (ja, 13 s) | 11.1 % | 0.06 | 7 |
| Whisper small q5_1 (whisper.cpp / Whisper.net 1.9.1) | full 30 s window | lecture-01 | 14.8 % | 2.61 | 7 |
| Whisper small q5_1 | encoder window = input (chosen) | lecture-01 | 14.8 % | 0.48 | 7 |
| Whisper small q5_1 | full 30 s window | english-01 (en, 6 s) | 0.0 % | 2.90 | 4 |
| Whisper small q5_1 | encoder window = input (chosen) | english-01 | 0.0 % | 0.32 | 4 |

Hardware: AMD Ryzen 7 9700X (8 cores), 61 GB RAM, NVIDIA GeForce RTX 5070 Ti, Windows 11 26200 — a desktop, not the
laptop reference class. Whisper large-v3-turbo on the GPU (Vulkan) is the live model on AC power when a capable GPU is
present; it was exercised end to end in `tools/Test-LiveTranscript.ps1` but not separately timed here.

Untested hardware matrix (no access): battery laptops (thermal/battery impact during recording), ARM64, NPU/Copilot+
PCs (Windows' built-in models), integrated-GPU-only laptops.

Windows AI speech recognition: not available as an API in Windows App SDK 2.5.5 (the AI package has no speech
namespace), so it could not be compared; recorded in ADR-025.
