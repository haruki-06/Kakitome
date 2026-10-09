using Microsoft.Extensions.Logging.Abstractions;
using Kakitome.Application.Settings;
using Kakitome.Storage;
using Kakitome.Storage.Settings;

namespace Kakitome.Tests.Settings;

public sealed class SettingsTests : IDisposable
{
    private readonly AppDataPaths _paths = new(Path.Combine(Path.GetTempPath(), "KakitomeTests", "settings-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_persist_and_reset_to_defaults()
    {
        Directory.CreateDirectory(_paths.Root);
        using (var store = Create())
        {
            Assert.False(store.Current.Recording.IncludeSystemAudio);
            Assert.Equal(HotkeyGesture.DefaultToggle, store.Current.Recording.ToggleHotkey);
            await store.UpdateAsync(s => s.Recording.IncludeSystemAudio = true, TestContext.Current.CancellationToken);
        }

        using (var reopened = Create())
        {
            Assert.True(reopened.Current.Recording.IncludeSystemAudio);
            await reopened.ResetAsync(TestContext.Current.CancellationToken);
            Assert.False(reopened.Current.Recording.IncludeSystemAudio);
        }
    }

    [Fact]
    public void Current_is_a_snapshot()
    {
        Directory.CreateDirectory(_paths.Root);
        using var store = Create();
        store.Current.Recording.IncludeSystemAudio = true;
        Assert.False(store.Current.Recording.IncludeSystemAudio);
    }

    [Fact]
    public void Corrupt_settings_are_moved_aside_and_defaults_used()
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(_paths.SettingsFile, "{ not json");

        using var store = Create();

        Assert.True(store.Current.Recording.IncludeMicrophone);
        Assert.False(File.Exists(_paths.SettingsFile));
        Assert.Single(Directory.GetFiles(_paths.Root, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Settings_saved_with_a_utf8_bom_are_read()
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(_paths.SettingsFile, "{ \"general\": { \"theme\": \"dark\" } }", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var store = Create();

        Assert.Equal("dark", store.Current.General.Theme);
        Assert.Empty(Directory.GetFiles(_paths.Root, "settings.json.corrupt-*"));
    }

    [Theory]
    [InlineData("Ctrl+Alt+Shift+R", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "R")]
    [InlineData("win + f9", HotkeyModifiers.Windows, "F9")]
    [InlineData("ctrl+space", HotkeyModifiers.Control, "Space")]
    public void Hotkeys_parse_and_round_trip(string text, HotkeyModifiers modifiers, string key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(key, gesture.Key);
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("R")]
    [InlineData("Shift+R")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+R+T")]
    [InlineData("Ctrl+Ö")]
    public void Unsafe_or_invalid_hotkeys_are_rejected(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    private JsonSettingsStore Create() => new(_paths, TimeProvider.System, NullLogger<JsonSettingsStore>.Instance);
}
