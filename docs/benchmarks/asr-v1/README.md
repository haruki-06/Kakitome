# ASR benchmark — corpus v1 (synthetic)

- Hardware: AMD Ryzen 7 9700X 8-Core Processor, 16 logical cores, 61.4 GB RAM; GPU: NVIDIA GeForce RTX 5070 Ti / AMD Radeon(TM) Graphics
- OS: Microsoft Windows NT 10.0.26200.0; power: AC; run: 2026-10-01
- Corpus: `benchmarks/corpus-v1.json`, Windows OneCore voices, noise variants clean / 15 dB / 5 dB SNR. Japanese scored by CER, English by WER (normalized; see `AsrMetrics`).
- RTF = processing time / audio time through the product path (reader → 16 kHz → VAD chunks → engine); lower is faster. Peak RAM is the benchmark process' peak working set.

| Model | Accel. | ja CER clean | ja CER 15 dB | ja CER 5 dB | en WER clean | RTF | Peak RAM | Load |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| whisper-large-v3-turbo-q5_0 | auto | 4.1 % | 4.9 % | 5.0 % | 0.0 % | 0.039 | 748 MB | 0.7 s |
| whisper-large-v3-turbo-q5_0 | cpu | 4.2 % | 4.9 % | 5.0 % | 0.0 % | 1.636 | 1163 MB | 0.6 s |
| whisper-small-q5_1 | cpu | 6.7 % | 7.6 % | 8.7 % | 0.0 % | 0.357 | 692 MB | 0.4 s |
| whisper-small-q5_1 | auto | 6.8 % | 7.6 % | 8.7 % | 0.0 % | 0.029 | 598 MB | 0.4 s |
| reazonspeech-k2-v2-int8 | auto | 7.6 % | 19.6 % | 22.2 % | 100.0 % | 0.027 | 472 MB | 3.3 s |

## ja CER by category (clean)

| Model | Accel. | casual | english | filler | interview | lecture | long | meeting | mixed-ja-en | numbers | proper-nouns | technical |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| reazonspeech-k2-v2-int8 | auto | 1.3 % | 100.0 % | 5.1 % | 9.5 % | 4.1 % | 7.4 % | 3.2 % | 19.3 % | 18.0 % | 3.4 % | 4.8 % |
| whisper-large-v3-turbo-q5_0 | auto | 0.0 % | 0.0 % | 0.0 % | 2.1 % | 1.5 % | 1.7 % | 0.8 % | 10.4 % | 39.3 % | 0.0 % | 1.6 % |
| whisper-large-v3-turbo-q5_0 | cpu | 0.0 % | 0.0 % | 0.0 % | 2.1 % | 1.5 % | 2.0 % | 0.8 % | 10.4 % | 39.3 % | 0.0 % | 1.6 % |
| whisper-small-q5_1 | auto | 5.0 % | 0.0 % | 2.6 % | 0.0 % | 5.1 % | 5.2 % | 0.8 % | 12.6 % | 39.3 % | 0.0 % | 7.1 % |
| whisper-small-q5_1 | cpu | 5.0 % | 0.0 % | 2.6 % | 0.0 % | 5.1 % | 5.2 % | 0.8 % | 11.1 % | 39.3 % | 0.0 % | 7.1 % |

Engines: `sherpa-onnx 1.13.8 (ONNX Runtime, CPU) / reazonspeech-k2-v2-int8`; `whisper.cpp (Whisper.net 1.9.1, Vulkan) / whisper-large-v3-turbo-q5_0`; `whisper.cpp (Whisper.net 1.9.1, Vulkan, CPU) / whisper-large-v3-turbo-q5_0`; `whisper.cpp (Whisper.net 1.9.1, Vulkan) / whisper-small-q5_1`; `whisper.cpp (Whisper.net 1.9.1, Vulkan, CPU) / whisper-small-q5_1`
