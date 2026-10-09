using System.Reflection;
using Kakitome.Domain;

namespace Kakitome.Tests.Architecture;

/// <summary>
/// Guards the layering contract: Domain and Application must not depend on WinUI, EF Core,
/// NAudio, or model-specific SDKs (CLAUDE.md → Architecture principles).
/// </summary>
public sealed class DependencyDirectionTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.WindowsAppSDK",
        "Microsoft.WinUI",
        "Microsoft.UI",
        "Microsoft.EntityFrameworkCore",
        "NAudio",
        "Microsoft.ML",
        "Whisper",
        "Kakitome.Storage",
        "Kakitome.Infrastructure",
        "Kakitome.App",
    ];

    [Fact]
    public void Storage_stays_portable_and_free_of_UI_audio_and_model_SDKs()
    {
        string[] forbidden = ["Microsoft.WindowsAppSDK", "Microsoft.WinUI", "Microsoft.UI", "Microsoft.Windows.SDK.NET", "NAudio", "Kakitome.Infrastructure", "Kakitome.App"];
        var references = typeof(Kakitome.Storage.AppDataPaths).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

        Assert.DoesNotContain(references, name => forbidden.Any(p => IsSameOrChild(name, p)));
    }

    [Theory]
    [InlineData(typeof(ProductInfo))]
    [InlineData(typeof(Kakitome.Application.ServiceCollectionExtensions))]
    public void Core_layers_do_not_reference_platform_or_model_assemblies(Type marker)
    {
        var references = marker.Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

        var violations = references
            .Where(name => ForbiddenPrefixes.Any(p => IsSameOrChild(name, p)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Domain_references_only_the_base_class_library()
    {
        var references = typeof(ProductInfo).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal) && n != "netstandard" && n != "mscorlib");

        Assert.Empty(references);
    }

    [Fact]
    public void Product_name_is_Kakitome()
    {
        Assert.Equal("Kakitome", ProductInfo.Name);
        Assert.DoesNotContain("vNext", ProductInfo.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Core_assemblies_are_named_Kakitome()
    {
        Assembly[] assemblies = [typeof(ProductInfo).Assembly, typeof(Kakitome.Application.ServiceCollectionExtensions).Assembly];
        Assert.All(assemblies, a => Assert.StartsWith("Kakitome.", a.GetName().Name, StringComparison.Ordinal));
    }

    /// <summary>"Kakitome.App" matches "Kakitome.App" and "Kakitome.App.X" but not "Kakitome.Application".</summary>
    private static bool IsSameOrChild(string name, string prefix) =>
        name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Offline after model installation (docs/00, docs/09): only the model store talks to the network, and only when the
    /// user installs a model. Anything else that opens a connection must be reviewed and added here deliberately.
    /// </summary>
    [Fact]
    public void Only_the_model_store_and_the_update_check_use_the_network()
    {
        string[] networkApis = ["HttpClient", "WebRequest", "TcpClient", "UdpClient", "WebSocket", "System.Net.Sockets"];
        // GitHubReleaseFeed: the optional daily update check (ADR-040) — one anonymous request for the latest version.
        string[] allowed = ["ModelStore.cs", "ServiceCollectionExtensions.cs", "GitHubReleaseFeed.cs"];
        var offenders = Directory.EnumerateFiles(Path.Combine(Kakitome.Tests.TestSupport.RepositoryPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => networkApis.Any(api => File.ReadAllText(f).Contains(api, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Where(name => !allowed.Contains(name))
            .ToList();
        Assert.Empty(offenders);
    }
}
