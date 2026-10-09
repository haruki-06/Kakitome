using System.Globalization;
using System.Text;

namespace Kakitome.App;

/// <summary>
/// Last-resort crash recorder. Writes only exception type/message/stack (never user content)
/// to the per-user app data log folder.
/// </summary>
internal static class CrashLog
{
    public static void Write(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kakitome",
                "Logs");
            Directory.CreateDirectory(dir);
            var entry = string.Create(
                CultureInfo.InvariantCulture,
                $"[{DateTimeOffset.Now:O}] {Kakitome.Application.Diagnostics.LogRedactor.Redact(exception.ToString())}{Environment.NewLine}{Environment.NewLine}");
            File.AppendAllText(Path.Combine(dir, "crash.log"), entry, Encoding.UTF8);
        }
#pragma warning disable CA1031 // A crash logger must never throw.
        catch
#pragma warning restore CA1031
        {
        }
    }
}
