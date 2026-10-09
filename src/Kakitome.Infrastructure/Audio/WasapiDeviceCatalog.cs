using NAudio.CoreAudioApi;
using Kakitome.Application.Recording;

namespace Kakitome.Infrastructure.Audio;

/// <summary>Lists active Core Audio endpoints and reports device changes.</summary>
public sealed class WasapiDeviceCatalog : IAudioDeviceCatalog, IDisposable
{
    /// <summary>Process loopback (ActivateAudioInterfaceAsync with PROCESS_LOOPBACK) exists since build 20348.</summary>
    private const int ProcessLoopbackMinBuild = 20348;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;

    public WasapiDeviceCatalog()
    {
        _notifications = _enumerator.CreateNotificationClient(false);
        _notifications.DeviceAdded += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
        _notifications.DeviceRemoved += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
        _notifications.DeviceStateChanged += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
        _notifications.DefaultDeviceChanged += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsApplicationCaptureSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, ProcessLoopbackMinBuild);

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> GetMicrophones() => List(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => List(DataFlow.Render);

    /// <summary>Processes with an audio session on any active output device (not system sounds, not Kakitome).</summary>
    public IReadOnlyList<AudioApplicationInfo> GetAudioApplications()
    {
        var apps = new Dictionary<int, AudioApplicationInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        var pid = (int)session.GetProcessID;
                        if (session.IsSystemSoundsSession || pid == 0 || pid == Environment.ProcessId)
                        {
                            continue;
                        }

                        var playing = session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive;
                        if (!apps.TryGetValue(pid, out var known) || (playing && !known.IsPlaying))
                        {
                            apps[pid] = new AudioApplicationInfo(pid, NameOf(pid, session.DisplayName), playing);
                        }
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // A device that disappears while enumerating is skipped.
                }
            }
        }

        return [.. apps.Values.OrderByDescending(a => a.IsPlaying).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string NameOf(int pid, string? sessionName)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            string? description = null;
            try
            {
                description = process.MainModule?.FileVersionInfo.FileDescription;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Elevated or protected process: no module access.
            }

            var name = !string.IsNullOrWhiteSpace(description) ? description : process.ProcessName;
            return string.IsNullOrWhiteSpace(process.MainWindowTitle) ? name : $"{name} — {process.MainWindowTitle}";
        }
        catch (ArgumentException)
        {
            return string.IsNullOrWhiteSpace(sessionName) ? $"PID {pid}" : sessionName;
        }
        catch (InvalidOperationException)
        {
            return string.IsNullOrWhiteSpace(sessionName) ? $"PID {pid}" : sessionName;
        }
    }

    public void Dispose()
    {
        _notifications.Dispose();
        _enumerator.Dispose();
    }

    private List<AudioDeviceInfo> List(DataFlow flow)
    {
        string? defaultId = null;
        if (_enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out var defaultDevice))
        {
            using (defaultDevice)
            {
                defaultId = defaultDevice.ID;
            }
        }

        var result = new List<AudioDeviceInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }

        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
