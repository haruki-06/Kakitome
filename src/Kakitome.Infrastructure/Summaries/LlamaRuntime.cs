using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Kakitome.Infrastructure.Summaries;

/// <summary>llama.cpp plumbing shared by the local-LLM features (summaries, glossary drafts).</summary>
internal static class LlamaRuntime
{
    private static readonly Lock NativeGate = new();
    private static bool _nativeConfigured;

    /// <summary>One grammar-constrained chat turn (low temperature, fixed seed for reproducible output).</summary>
    public static async Task<string> InferAsync(
        LLamaWeights weights, ModelParams parameters, string system, string user, string grammar, int maxTokens, CancellationToken cancellationToken)
    {
        var template = new LLamaTemplate(weights) { AddAssistant = true };
        template.Add("system", system);
        template.Add("user", user);
        var prompt = Encoding.UTF8.GetString(template.Apply());

        var executor = new StatelessExecutor(weights, parameters);
        using var sampling = new DefaultSamplingPipeline
        {
            Temperature = 0.2f,
            Seed = 42,
            Grammar = new Grammar(grammar, "root"),
        };
        var inference = new InferenceParams { MaxTokens = maxTokens, SamplingPipeline = sampling };
        var output = new StringBuilder();
        await foreach (var piece in executor.InferAsync(prompt, inference, cancellationToken).ConfigureAwait(false))
        {
            output.Append(piece);
        }

        return output.ToString();
    }

    /// <summary>Native backends are chosen once per process: Vulkan when available, CPU (best AVX) otherwise.</summary>
    public static void ConfigureNative()
    {
        lock (NativeGate)
        {
            if (_nativeConfigured)
            {
                return;
            }

            NativeLibraryConfig.All.WithVulkan(true).WithAutoFallback(true).WithLogCallback(OnNativeLog);
            _nativeConfigured = true;
        }
    }

    /// <summary>llama.cpp is verbose at info level; only its warnings and errors are kept (in the Debug output).</summary>
    private static void OnNativeLog(LLamaLogLevel level, string message)
    {
        if (level is LLamaLogLevel.Warning or LLamaLogLevel.Error)
        {
            Debug.Write(message);
        }
    }
}
