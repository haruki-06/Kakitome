using System.Text;
using LLama;
using LLama.Common;
using Kakitome.Application.Models;
using Kakitome.Infrastructure.Summaries;

namespace Kakitome.Bench;

/// <summary>Experiment (ADR-031, not adopted): drafts a glossary from a transcript with the installed local LLM.</summary>
internal sealed class LlamaGlossaryDrafter(IModelStore models, string? modelId, bool useGpu, Action<string>? rawOutput)
{
    /// <summary>Smaller than the summary chunks: the model has to look at every word, not only the gist.</summary>
    private const int ChunkTokens = 3_000;

    public async Task<GlossaryDraft> DraftAsync(string title, IReadOnlyList<string> lines, int threads)
    {
        var model = ModelCatalog.SummaryModels.Where(m => modelId is null || m.Id == modelId)
            .FirstOrDefault(m => models.GetState(m.Id) == ModelState.Installed) ?? throw new InvalidOperationException("No local LLM is installed.");
        LlamaRuntime.ConfigureNative();
        var parameters = new ModelParams(Path.Combine(models.GetDirectory(model.Id), model.Files[0].Name))
        {
            ContextSize = 6_144,
            GpuLayerCount = useGpu ? 99 : 0,
            Threads = threads,
            BatchSize = 512,
        };
        using var weights = await LLamaWeights.LoadFromFileAsync(parameters);
        var answers = new List<ChunkAdvice>();
        foreach (var chunk in Chunk(lines, line => weights.Tokenize(line, false, false, Encoding.UTF8).Length + 2))
        {
            var json = await LlamaRuntime.InferAsync(weights, parameters, GlossaryDraftFormat.SystemPrompt, GlossaryDraftFormat.UserPrompt(title, chunk),
                GlossaryDraftFormat.Grammar, 1_600, CancellationToken.None);
            rawOutput?.Invoke(json);
            answers.Add(GlossaryDraftFormat.Parse(json));
        }

        return GlossaryDraftFormat.Combine(answers, string.Concat(lines), $"llama.cpp ({(useGpu ? "Vulkan GPU" : "CPU")}) / {model.Id}");
    }

    private static List<List<string>> Chunk(IReadOnlyList<string> lines, Func<string, int> tokens)
    {
        var chunks = new List<List<string>>();
        var current = new List<string>();
        var size = 0;
        foreach (var line in lines)
        {
            var cost = tokens(line);
            if (current.Count > 0 && size + cost > ChunkTokens)
            {
                chunks.Add(current);
                current = [];
                size = 0;
            }

            current.Add(line);
            size += cost;
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }
}
