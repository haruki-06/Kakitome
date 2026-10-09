using System.Text.Json;
using Kakitome.Application.Jobs;

namespace Kakitome.Application.Models;

/// <summary>
/// <c>model.install</c>: downloads and verifies a model as a durable job (resumable after deferral/crash via the
/// store's partial files). Enqueued by the user (Model Manager) or, for a missing recommended ASR model / yt-dlp, by
/// <see cref="AutoModelInstaller"/> at startup (ADR-034) — always as a visible, cancelable job; never as an update.
/// </summary>
public sealed class ModelInstallJobHandler(IModelStore store) : IJobHandler
{
    public const string JobKind = "model.install";

    public string Kind => JobKind;

    public JobResourceClass ResourceClass => JobResourceClass.Light;

    public static string PayloadFor(string modelId) => JsonSerializer.Serialize(new Payload(modelId));

    /// <summary>The model a <c>model.install</c> job installs (null when the payload is not one).</summary>
    public static string? ModelIdOf(string? payload)
    {
        try
        {
            return payload is null ? null : JsonSerializer.Deserialize<Payload>(payload)?.ModelId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = context.Job.Payload is { } p ? JsonSerializer.Deserialize<Payload>(p) : null;
        var model = payload?.ModelId is { } id ? ModelCatalog.Find(id) : null;
        if (model is null)
        {
            throw new PermanentJobException("Unknown model.");
        }

        if (store.GetState(model.Id) == ModelState.Installed)
        {
            return;
        }

        var last = -1.0;
        var progress = new Progress<double>(v =>
        {
            // Persist at most every 1 % to keep the database quiet.
            if (v - last >= 0.01 || v >= 1)
            {
                last = v;
                _ = context.ReportProgressAsync(v, model.DisplayName);
            }
        });

        try
        {
            await store.InstallAsync(model.Id, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientJobException($"Download of {model.DisplayName} failed: {ex.Message}", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new TransientJobException(ex.Message, ex);
        }

        await context.SetEngineAsync(model.Source.ToString()).ConfigureAwait(false);
    }

    private sealed record Payload(string ModelId);
}
