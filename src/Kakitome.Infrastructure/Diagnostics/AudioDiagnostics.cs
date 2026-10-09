using Kakitome.Application.Diagnostics;
using Kakitome.Application.Recording;

namespace Kakitome.Infrastructure.Diagnostics;

/// <summary>Microphones and output devices Windows reports, for recording problems.</summary>
public sealed class AudioDiagnostics(IAudioDeviceCatalog audio) : IDiagnosticsSource
{
    public string Name => "Audio devices";

    public IEnumerable<string> Describe()
    {
        foreach (var d in audio.GetMicrophones())
        {
            yield return $"Microphone: {d.Name}{(d.IsDefault ? " (default)" : string.Empty)}";
        }

        foreach (var d in audio.GetOutputDevices())
        {
            yield return $"Output: {d.Name}{(d.IsDefault ? " (default)" : string.Empty)}";
        }

        yield return $"Per-app capture supported: {audio.IsApplicationCaptureSupported}";
    }
}
