# Kakitome — Third-party notices

Kakitome includes or uses the components below. Full license texts are in the `licenses` folder installed with
Kakitome (and in the source repository). Components marked "not included" are obtained separately by the user.

## Included in the Kakitome package

| Component | Version | License | Copyright / source |
|---|---|---|---|
| Windows App SDK (WinUI 3, App lifecycle, notifications) | 2.5.1 | Microsoft Software License Terms (redistributable); its own third-party notices in `licenses/WindowsAppSDK-NOTICE.txt` | Microsoft Corporation — https://github.com/microsoft/windowsappsdk |
| .NET runtime libraries, Microsoft.Extensions.* (Hosting, DI, Logging, Options) | 10.0 | MIT; notices in `licenses/dotnet-THIRD-PARTY-NOTICES.txt` | .NET Foundation and Contributors — https://dot.net |
| Entity Framework Core (SQLite) | 10.0.12 | MIT | .NET Foundation and Contributors — https://github.com/dotnet/efcore |
| SQLitePCLRaw (bundle_e_sqlite3) | 2.1.12 | Apache-2.0 | SourceGear, LLC — https://github.com/ericsink/SQLitePCL.raw |
| SQLite | (bundled with SQLitePCLRaw) | Public domain | https://sqlite.org/copyright.html |
| CommunityToolkit.Mvvm | 8.4.2 | MIT; notices in `licenses/CommunityToolkit-ThirdPartyNotices.txt` | .NET Foundation and Contributors — https://github.com/CommunityToolkit/dotnet |
| NAudio (NAudio.Core, NAudio.Wasapi) | 3.1.0 | MIT | Copyright (c) 2020 Mark Heath — https://github.com/naudio/NAudio |
| Whisper.net (+ runtimes: CPU, Vulkan) | 1.9.1 | MIT (`licenses/Whisper-net-LICENSE.txt`) | Copyright (c) 2024 sandrohanea — https://github.com/sandrohanea/whisper.net |
| whisper.cpp / ggml (inside the Whisper.net runtimes) | as shipped by Whisper.net 1.9.1 | MIT | Copyright (c) 2023-2024 The ggml authors — https://github.com/ggml-org/whisper.cpp |
| sherpa-onnx (C API + .NET bindings) | 1.13.8 | Apache-2.0 | Xiaomi Corporation and the k2-fsa contributors — https://github.com/k2-fsa/sherpa-onnx |
| LLamaSharp (+ CPU and Vulkan backends) | 0.27.0 | MIT | Copyright (c) 2023 Rinne and the SciSharp contributors — https://github.com/SciSharp/LLamaSharp |
| llama.cpp / ggml (inside the LLamaSharp backends) | as shipped by LLamaSharp 0.27.0 | MIT | Copyright (c) 2023-2024 The ggml authors — https://github.com/ggml-org/llama.cpp |
| ONNX Runtime (inside the sherpa-onnx runtime) | as shipped by sherpa-onnx 1.13.8 | MIT | Copyright (c) Microsoft Corporation — https://github.com/microsoft/onnxruntime |

Audio capture, decoding and encoding use Windows components (WASAPI, Media Foundation) that are part of Windows.
Kakitome does not include FFmpeg.

## Not included — downloaded only when the user chooses to

| Component | License | Source |
|---|---|---|
| Whisper large-v3-turbo / small model weights (ggml conversions) | MIT (OpenAI Whisper weights; conversion by whisper.cpp) | https://huggingface.co/ggerganov/whisper.cpp |
| ReazonSpeech k2 v2 model | Apache-2.0 (Reazon Holdings) | https://huggingface.co/reazon-research/reazonspeech-k2-v2 |
| Qwen3-4B-Instruct-2507 (summary AI, GGUF Q4_K_M by Unsloth) | Apache-2.0 (Qwen team, Alibaba Cloud) | https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF |
| Phi-4-mini-instruct (summary AI, GGUF Q4_K_M by Unsloth) | MIT (Microsoft) | https://huggingface.co/unsloth/Phi-4-mini-instruct-GGUF |
| yt-dlp 2026.08.19 (URL import; installed on request, or the user's own copy) | Unlicense | https://github.com/yt-dlp/yt-dlp |

Models are verified against pinned SHA-256 / git blob hashes before they are used.

## License texts

- MIT: `licenses/MIT.txt` (template; the copyright line for each component is listed above)
- Apache License 2.0: `licenses/Apache-2.0.txt`
- Microsoft Software License Terms — Windows App SDK: `licenses/WindowsAppSDK-LICENSE.txt`
