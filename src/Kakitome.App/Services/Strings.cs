using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Kakitome.App.Services;

/// <summary>Localized UI strings from <c>Strings/&lt;lang&gt;/Resources.resw</c> (ja-JP / en-US).</summary>
internal static class Strings
{
    private static readonly ResourceLoader Loader = new();

    /// <summary>
    /// Returns the localized string, or the key itself when missing (never throws: a missing string must not crash a
    /// page). Keys must not contain '.', which the resource system treats as a property path.
    /// </summary>
    public static string Get(string key)
    {
        try
        {
            var value = Loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (COMException)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
