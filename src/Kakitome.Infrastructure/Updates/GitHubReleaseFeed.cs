using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kakitome.Application.Updates;

namespace Kakitome.Infrastructure.Updates;

/// <summary>
/// Latest release of the public repository through the GitHub REST API (anonymous; only the request itself reaches
/// GitHub). Pre-releases are not returned by <c>releases/latest</c>. The release page must be on the repository's own
/// GitHub releases path, so a changed response can never make Kakitome open another site.
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    public const string Repository = "haruki-06/Kakitome";

    private static readonly Uri LatestApi = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly string PagePrefix = $"https://github.com/{Repository}/releases/";

    private readonly HttpClient _http;

    public GitHubReleaseFeed()
        : this(new HttpClient())
    {
    }

    /// <summary>Tests pass a client with a fake handler.</summary>
    public GitHubReleaseFeed(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kakitome-UpdateCheck", "1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(LatestApi, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // nothing published yet
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var page = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || page is null || !page.StartsWith(PagePrefix, StringComparison.Ordinal)
            || !Uri.TryCreate(page, UriKind.Absolute, out var uri))
        {
            throw new FormatException("Unexpected release information.");
        }

        var version = tag.TrimStart('v', 'V');
        UpdateChecker.Compare(version, "0.0.0"); // throws FormatException for a tag that is not a version
        return new ReleaseInfo(version, uri);
    }

    public void Dispose() => _http.Dispose();
}
