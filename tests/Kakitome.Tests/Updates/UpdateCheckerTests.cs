using System.Net;
using Kakitome.Application.Updates;
using Kakitome.Infrastructure.Updates;
using Kakitome.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kakitome.Tests.Updates;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("2.0.0", "2.0.0-beta.10", true)]   // a release is newer than its pre-releases
    [InlineData("2.0.0-beta.10", "2.0.0-beta.9", true)] // numeric identifiers compare as numbers
    [InlineData("2.0.1", "2.0.0", true)]
    [InlineData("2.1.0", "2.0.9", true)]
    [InlineData("v2.0.0", "2.0.0+abc", false)]      // leading v and build metadata ignored
    [InlineData("1.9.9", "2.0.0-beta.1", false)]
    [InlineData("2.0.0-beta.10", "2.0.0", false)]
    public void Versions_follow_semver_precedence(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public async Task A_newer_release_is_reported_and_checks_happen_at_most_daily_unless_forced()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var feed = new FakeFeed { Latest = new ReleaseInfo("2.1.0", new Uri("https://github.com/haruki-06/Kakitome/releases/tag/v2.1.0")) };
        var checker = new UpdateChecker(feed, f.Settings, TimeProvider.System, NullLogger<UpdateChecker>.Instance) { CurrentVersion = "2.0.0" };
        var raised = 0;
        checker.AvailableChanged += (_, _) => raised++;

        Assert.Equal(UpdateCheckResult.Available, await checker.CheckAsync(force: false));
        Assert.Equal("2.1.0", checker.Available!.Version);
        Assert.Equal(1, raised);
        Assert.Equal(UpdateCheckResult.Skipped, await checker.CheckAsync(force: false)); // checked today
        Assert.Equal(1, feed.Calls);

        feed.Latest = new ReleaseInfo("2.0.0", feed.Latest.Page);
        Assert.Equal(UpdateCheckResult.UpToDate, await checker.CheckAsync(force: true));
        Assert.Null(checker.Available);

        feed.Fail = true;
        Assert.Equal(UpdateCheckResult.Failed, await checker.CheckAsync(force: true));

        await f.Settings.UpdateAsync(s => { s.General.CheckForUpdates = false; s.General.LastUpdateCheck = null; });
        Assert.Equal(UpdateCheckResult.Skipped, await checker.CheckAsync(force: false)); // turned off: never asks
        Assert.Equal(3, feed.Calls);
    }

    [Fact]
    public async Task The_GitHub_feed_reads_the_latest_release_and_rejects_pages_outside_the_repository()
    {
        using var ok = new GitHubReleaseFeed(new HttpClient(new Respond(HttpStatusCode.OK,
            """{"tag_name":"v2.1.0","html_url":"https://github.com/haruki-06/Kakitome/releases/tag/v2.1.0","name":"x"}""")));
        var release = await ok.GetLatestAsync();
        Assert.Equal("2.1.0", release!.Version);

        using var none = new GitHubReleaseFeed(new HttpClient(new Respond(HttpStatusCode.NotFound, "{}")));
        Assert.Null(await none.GetLatestAsync()); // nothing published yet

        using var elsewhere = new GitHubReleaseFeed(new HttpClient(new Respond(HttpStatusCode.OK,
            """{"tag_name":"v9.9.9","html_url":"https://example.com/download"}""")));
        await Assert.ThrowsAsync<FormatException>(() => elsewhere.GetLatestAsync());
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        public ReleaseInfo? Latest { get; set; }

        public bool Fail { get; set; }

        public int Calls { get; private set; }

        public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Fail ? throw new HttpRequestException("offline") : Task.FromResult(Latest);
        }
    }

    private sealed class Respond(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
