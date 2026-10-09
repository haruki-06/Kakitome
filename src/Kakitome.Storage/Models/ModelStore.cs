using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Kakitome.Application.Models;

namespace Kakitome.Storage.Models;

/// <summary>
/// Model files under <c>&lt;AppData&gt;\Models\&lt;modelId&gt;\</c>. Downloads go to <c>*.partial</c> (resumed with HTTP
/// Range), are size- and hash-verified, and only then renamed into place; a <c>.installed.json</c> marker is written
/// last. A model without a valid marker is never reported as installed, so a partial or tampered download is never
/// loaded (docs/07).
/// </summary>
public sealed partial class ModelStore : IModelStore
{
    private const string MarkerFile = ".installed.json";
    private const string PartialSuffix = ".partial";

    private readonly HttpClient _http;
    private readonly ILogger<ModelStore> _logger;
    private readonly IReadOnlyList<ModelDescriptor> _catalog;

    public ModelStore(AppDataPaths paths, IHttpClientFactoryLite http, ILogger<ModelStore> logger)
        : this(paths, http, logger, [.. ModelCatalog.Everything])
    {
    }

    internal ModelStore(AppDataPaths paths, IHttpClientFactoryLite http, ILogger<ModelStore> logger, IReadOnlyList<ModelDescriptor> catalog)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(http);
        RootPath = paths.ModelsDirectory;
        _http = http.Create();
        _logger = logger;
        _catalog = catalog;
    }

    public string RootPath { get; }

    public string GetDirectory(string modelId) => Path.Combine(RootPath, SafeId(modelId));

    public ModelState GetState(string modelId)
    {
        var model = Find(modelId);
        var dir = GetDirectory(modelId);
        var marker = Path.Combine(dir, MarkerFile);
        if (model is null || !File.Exists(marker))
        {
            return ModelState.NotInstalled;
        }

        // Cheap check on every query; full hashing happens at install and in VerifyAsync.
        foreach (var file in model.Files)
        {
            var info = new FileInfo(Path.Combine(dir, file.Name));
            if (!info.Exists || info.Length != file.Size)
            {
                return ModelState.Invalid;
            }
        }

        return ModelState.Installed;
    }

    public async Task InstallAsync(string modelId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var model = Find(modelId) ?? throw new ArgumentException($"Unknown model '{modelId}'.", nameof(modelId));
        var dir = GetDirectory(modelId);
        Directory.CreateDirectory(dir);
        File.Delete(Path.Combine(dir, MarkerFile));

        long completed = 0;
        foreach (var file in model.Files)
        {
            var target = Path.Combine(dir, file.Name);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size && await HashMatchesAsync(target, file, cancellationToken).ConfigureAwait(false))
            {
                completed += file.Size;
                progress?.Report((double)completed / model.TotalSize);
                continue;
            }

            File.Delete(target);
            var partial = target + PartialSuffix;
            var baseCompleted = completed;
            await DownloadAsync(file, partial, p => progress?.Report((double)(baseCompleted + p) / model.TotalSize), cancellationToken)
                .ConfigureAwait(false);

            if (!await HashMatchesAsync(partial, file, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(partial);
                throw new InvalidDataException($"{file.Name} failed integrity verification and was discarded.");
            }

            File.Move(partial, target, overwrite: true);
            completed += file.Size;
        }

        var marker = new InstalledMarker(model.Id, DateTimeOffset.UtcNow, model.Files.Select(f => f.Sha256 ?? f.GitBlobSha1 ?? string.Empty).ToList());
        await File.WriteAllTextAsync(Path.Combine(dir, MarkerFile), JsonSerializer.Serialize(marker), cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        LogInstalled(model.Id);
    }

    public async Task<bool> VerifyAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = Find(modelId);
        if (model is null || GetState(modelId) != ModelState.Installed)
        {
            return false;
        }

        var dir = GetDirectory(modelId);
        foreach (var file in model.Files)
        {
            if (!await HashMatchesAsync(Path.Combine(dir, file.Name), file, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(Path.Combine(dir, MarkerFile));
                LogInvalid(modelId, file.Name);
                return false;
            }
        }

        return true;
    }

    public Task RemoveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var dir = GetDirectory(modelId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        return Task.CompletedTask;
    }

    public long GetUsedBytes() =>
        Directory.Exists(RootPath)
            ? new DirectoryInfo(RootPath).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

    internal static async Task<bool> HashMatchesAsync(string path, ModelFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
        {
            return false;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            if (file.Sha256 is { Length: > 0 } sha256)
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                return string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase);
            }

            if (file.GitBlobSha1 is { Length: > 0 } blob)
            {
                // git blob id = SHA-1("blob <size>\0" + content)
                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning disable CA5350 // SHA-1 is only used to match the publisher's git object id, alongside the pinned size.
                sha1.AppendData(Encoding.ASCII.GetBytes($"blob {file.Size}\0"));
#pragma warning restore CA5350
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    sha1.AppendData(buffer, 0, read);
                }

                return string.Equals(Convert.ToHexStringLower(sha1.GetHashAndReset()), blob, StringComparison.OrdinalIgnoreCase);
            }

            return false; // a file without a pinned hash is never trusted
        }
    }

    private async Task DownloadAsync(ModelFile file, string partial, Action<long> progress, CancellationToken cancellationToken)
    {
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > file.Size)
        {
            File.Delete(partial);
            existing = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            existing = 0; // server ignored the range: start over
        }

        response.EnsureSuccessStatusCode();
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var target = new FileStream(partial, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[1024 * 1024];
                var written = existing;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > file.Size)
                    {
                        throw new InvalidDataException($"{file.Name} is larger than expected; download aborted.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress(written);
                }
            }
        }
    }

    private ModelDescriptor? Find(string modelId) => _catalog.FirstOrDefault(m => m.Id == modelId);

    private static string SafeId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || modelId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid model id '{modelId}'.", nameof(modelId));
        }

        return modelId;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {Id} installed and verified")]
    private partial void LogInstalled(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model {Id} failed verification ({File}); it will not be loaded")]
    private partial void LogInvalid(string id, string file);

    private sealed record InstalledMarker(string Id, DateTimeOffset InstalledAt, List<string> Hashes);
}

/// <summary>Minimal HttpClient source (the app uses one shared client; tests can substitute a handler).</summary>
public interface IHttpClientFactoryLite
{
    HttpClient Create();
}

public sealed class SharedHttpClientFactory : IHttpClientFactoryLite, IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "Kakitome-ModelManager" } },
    };

    public HttpClient Create() => _client;

    public void Dispose() => _client.Dispose();
}
