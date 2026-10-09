namespace Kakitome.Application.Models;

/// <summary>
/// One file of a model package, pinned by size and hash (verified before the model is ever loaded). Large files are
/// pinned by SHA-256 (Hugging Face LFS oid); small non-LFS files by their git blob SHA-1 (Hugging Face file oid).
/// </summary>
public sealed record ModelFile(string Name, Uri Url, long Size, string? Sha256, string? GitBlobSha1 = null);

/// <summary>A downloadable local model. Models live outside the Library and can be removed without touching recordings.</summary>
public sealed record ModelDescriptor(
    string Id,
    string ProviderId,
    string DisplayName,
    IReadOnlyList<ModelFile> Files,
    string License,
    Uri Source,
    IReadOnlyList<string> Languages,
    long ApproximateRamBytes)
{
    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>
/// The models Kakitome knows how to obtain. Hashes and sizes come from the publishers' repositories (Hugging Face LFS
/// metadata) and pin exact files. Defaults are chosen by benchmark (docs/benchmarks, ADR-022), not by this list order.
/// </summary>
public static class ModelCatalog
{
    public const string WhisperLargeV3TurboQ5 = "whisper-large-v3-turbo-q5_0";
    public const string WhisperSmallQ5 = "whisper-small-q5_1";
    public const string ReazonSpeechK2V2Int8 = "reazonspeech-k2-v2-int8";

    private const string WhisperRepo = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    private const string ReazonRepo = "https://huggingface.co/reazon-research/reazonspeech-k2-v2/resolve/main/";

    private static readonly string[] Multilingual = ["ja", "en", "*"];

    public static IReadOnlyList<ModelDescriptor> All { get; } =
    [
        new(
            WhisperLargeV3TurboQ5,
            "whisper.cpp",
            "Whisper large-v3-turbo (q5_0)",
            [new("ggml-large-v3-turbo-q5_0.bin", new Uri(WhisperRepo + "ggml-large-v3-turbo-q5_0.bin"), 574_041_195,
                "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")],
            "MIT (OpenAI Whisper weights; ggml conversion by whisper.cpp)",
            new Uri("https://huggingface.co/ggerganov/whisper.cpp"),
            Multilingual,
            1_000L * 1024 * 1024),
        new(
            WhisperSmallQ5,
            "whisper.cpp",
            "Whisper small (q5_1)",
            [new("ggml-small-q5_1.bin", new Uri(WhisperRepo + "ggml-small-q5_1.bin"), 190_085_487,
                "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb")],
            "MIT (OpenAI Whisper weights; ggml conversion by whisper.cpp)",
            new Uri("https://huggingface.co/ggerganov/whisper.cpp"),
            Multilingual,
            450L * 1024 * 1024),
        new(
            ReazonSpeechK2V2Int8,
            "sherpa-onnx",
            "ReazonSpeech k2 v2 (Japanese, int8)",
            [
                new("encoder-epoch-99-avg-1.int8.onnx", new Uri(ReazonRepo + "encoder-epoch-99-avg-1.int8.onnx"), 154_670_139,
                    "2c7bd08a8a99f9ddd0d9e458456577b1f6279214e51426f114f9eced44c54e1d"),
                new("decoder-epoch-99-avg-1.int8.onnx", new Uri(ReazonRepo + "decoder-epoch-99-avg-1.int8.onnx"), 2_959_337,
                    "8f0bff94d38797b03b762634ed03211a8e303d06cc4603cdd0cf4199d6eb1485"),
                new("joiner-epoch-99-avg-1.int8.onnx", new Uri(ReazonRepo + "joiner-epoch-99-avg-1.int8.onnx"), 2_696_970,
                    "49cc7ea1d3d35a40a27442db5e89996da64bf0e683a903dce76e99e57a12e4de"),
                new("tokens.txt", new Uri(ReazonRepo + "tokens.txt"), 45_754, Sha256: null,
                    GitBlobSha1: "e2c46c26b8e3e01d8e7bbcf5d2f95b1270a0ab7e"),
            ],
            "Apache-2.0 (Reazon Holdings)",
            new Uri("https://huggingface.co/reazon-research/reazonspeech-k2-v2"),
            ["ja"],
            600L * 1024 * 1024),
    ];

    /// <summary>yt-dlp for URL import, installed only when the user asks (Settings › Import).</summary>
    public const string YtDlp = "yt-dlp";

    private const string YtDlpRelease = "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/";

    /// <summary>
    /// Tools Kakitome can install on request. They are not speech models (never listed or selected as such) but are
    /// pinned and verified exactly like models. Hashes from the publisher's release metadata and SHA2-256SUMS.
    /// </summary>
    public static IReadOnlyList<ModelDescriptor> Tools { get; } = [YtDlpFor(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture)];

    internal static ModelDescriptor YtDlpFor(System.Runtime.InteropServices.Architecture architecture) => new(
        YtDlp,
        "tool",
        "yt-dlp 2026.08.19",
        [architecture == System.Runtime.InteropServices.Architecture.Arm64
            ? new ModelFile("yt-dlp.exe", new Uri(YtDlpRelease + "yt-dlp_arm64.exe"), 21_159_286,
                "05b438997bafc3affdfda9d041353c9d73e04dc842207254b655b0887c4445b0")
            : new ModelFile("yt-dlp.exe", new Uri(YtDlpRelease + "yt-dlp.exe"), 17_840_399,
                "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a")],
        "Unlicense (public domain)",
        new Uri("https://github.com/yt-dlp/yt-dlp"),
        [],
        0);

    public const string Qwen3Instruct4B = "qwen3-4b-instruct-2507-q4_k_m";
    public const string Phi4MiniInstruct = "phi-4-mini-instruct-q4_k_m";

    private const string QwenRepo = "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/a06e946bb6b655725eafa393f4a9745d460374c9/";
    private const string PhiRepo = "https://huggingface.co/unsloth/Phi-4-mini-instruct-GGUF/resolve/78eb92a46fc37e6b524df991ed9aca9bc6aa7b80/";

    /// <summary>
    /// Local LLMs for summaries (llama.cpp GGUF, 4-bit). Pinned to the repository commit and the LFS SHA-256. Optional:
    /// the extractive summary works without them; adoption is benchmark-driven (docs/benchmarks/summary-v1).
    /// </summary>
    public static IReadOnlyList<ModelDescriptor> SummaryModels { get; } =
    [
        new(
            Qwen3Instruct4B,
            "llama.cpp",
            "Qwen3 4B Instruct 2507 (Q4_K_M)",
            [new("Qwen3-4B-Instruct-2507-Q4_K_M.gguf", new Uri(QwenRepo + "Qwen3-4B-Instruct-2507-Q4_K_M.gguf"), 2_497_281_120,
                "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597")],
            "Apache-2.0 (Qwen team, Alibaba Cloud; GGUF by Unsloth)",
            new Uri("https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF"),
            ["ja", "en", "*"],
            4_000L * 1024 * 1024),
        new(
            Phi4MiniInstruct,
            "llama.cpp",
            "Phi-4-mini-instruct (Q4_K_M)",
            [new("Phi-4-mini-instruct-Q4_K_M.gguf", new Uri(PhiRepo + "Phi-4-mini-instruct-Q4_K_M.gguf"), 2_491_874_272,
                "88c00229914083cd112853aab84ed51b87bdf6b9ce42f532d8c85c7c63b1730a")],
            "MIT (Microsoft; GGUF by Unsloth)",
            new Uri("https://huggingface.co/unsloth/Phi-4-mini-instruct-GGUF"),
            ["ja", "en", "*"],
            4_000L * 1024 * 1024),
    ];

    /// <summary>Every downloadable item: ASR, tools and summary models (what the model store can install).</summary>
    public static IEnumerable<ModelDescriptor> Everything => All.Concat(Tools).Concat(SummaryModels);

    public static ModelDescriptor? Find(string id) => Everything.FirstOrDefault(m => m.Id == id);
}

public enum ModelState
{
    NotInstalled,
    Installed,

    /// <summary>Files exist but do not match the pinned size/hash; never loaded.</summary>
    Invalid,
}

/// <summary>Local model files: install (download + verify), state, location, removal.</summary>
public interface IModelStore
{
    /// <summary>Root folder for models (outside the Library).</summary>
    string RootPath { get; }

    ModelState GetState(string modelId);

    /// <summary>Folder containing the model's files. Only meaningful when <see cref="ModelState.Installed"/>.</summary>
    string GetDirectory(string modelId);

    /// <summary>Downloads missing files (resumable), verifies sizes and SHA-256, then marks the model installed.</summary>
    Task InstallAsync(string modelId, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Re-hashes installed files; marks the model invalid if anything changed.</summary>
    Task<bool> VerifyAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a model's files (never Library content).</summary>
    Task RemoveAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>Total bytes used by installed and partial models (Storage settings).</summary>
    long GetUsedBytes();
}
