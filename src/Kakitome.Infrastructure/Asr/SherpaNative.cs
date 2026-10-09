using System.Runtime.InteropServices;

namespace Kakitome.Infrastructure.Asr;

/// <summary>
/// Makes sherpa-onnx use the ONNX Runtime it was built against. The Windows App SDK runtime package ships its own
/// onnxruntime.dll (an older version, for Windows ML) and its folder comes first in the DLL search path of a WinUI
/// process, so sherpa-onnx-c-api.dll bound to it and crashed the app with an access violation. Loading Kakitome's copy by
/// full path first makes the loader reuse that module for sherpa's import.
/// </summary>
internal static class SherpaNative
{
    private static readonly Lazy<bool> Loaded = new(Load);

    public static void EnsureLoaded() => _ = Loaded.Value;

    private static bool Load()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll"), // published app (flattened)
            Path.Combine(AppContext.BaseDirectory, "runtimes", arch, "native", "onnxruntime.dll"), // dev / test builds
        ];
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                NativeLibrary.Load(path);
                return true;
            }
        }

        return false;
    }
}
