using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kakitome.Application.Recording;

namespace Kakitome.Infrastructure.Power;

/// <summary>
/// Power request (PowerCreateRequest/PowerSetRequest) that prevents idle sleep while held. Unlike
/// SetThreadExecutionState it is not tied to a thread. Lid-close and user-initiated sleep still follow Windows policy.
/// </summary>
public sealed partial class WindowsKeepAwake : IKeepAwake
{
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;

    public IDisposable Acquire(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext { Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = text };
            var handle = PowerCreateRequest(ref context);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return NoopHandle.Instance;
            }

            if (!PowerSetRequest(handle, PowerRequestSystemRequired))
            {
                CloseHandle(handle);
                return NoopHandle.Instance;
            }

            return new Request(handle);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        // Remainder of the union with the detailed-reason form (module, id, count, strings) = 24 bytes on x64.
        private readonly IntPtr _padding1;
        private readonly IntPtr _padding2;
    }

    private sealed class Request(IntPtr handle) : IDisposable
    {
        private IntPtr _handle = handle;

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero)
            {
                PowerClearRequest(h, PowerRequestSystemRequired);
                CloseHandle(h);
            }
        }
    }

    private sealed class NoopHandle : IDisposable
    {
        public static readonly NoopHandle Instance = new();

        public void Dispose()
        {
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(IntPtr handle, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(IntPtr handle, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

/// <summary>Suspend/resume notifications via PowerRegisterSuspendResumeNotification (no window required).</summary>
public sealed unsafe partial class WindowsPowerEvents : IPowerEvents, IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmSuspend = 0x4;
    private const uint PbtApmResumeSuspend = 0x7;
    private const uint PbtApmResumeAutomatic = 0x12;

    private readonly GCHandle _self;
    private IntPtr _registration;

    public WindowsPowerEvents()
    {
        _self = GCHandle.Alloc(this);
        var parameters = new SubscribeParameters
        {
            Callback = &OnPowerNotification,
            Context = GCHandle.ToIntPtr(_self),
        };
        if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref parameters, out _registration) != 0)
        {
            _registration = IntPtr.Zero;
        }
    }

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            _ = PowerUnregisterSuspendResumeNotification(_registration);
            _registration = IntPtr.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnPowerNotification(IntPtr context, uint type, IntPtr setting)
    {
        if (GCHandle.FromIntPtr(context).Target is WindowsPowerEvents self)
        {
            // Handlers only flip state/queue work; they must return quickly (Windows waits during suspend).
            switch (type)
            {
                case PbtApmSuspend:
                    self.Suspending?.Invoke(self, EventArgs.Empty);
                    break;
                case PbtApmResumeSuspend or PbtApmResumeAutomatic:
                    self.Resumed?.Invoke(self, EventArgs.Empty);
                    break;
            }
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint> Callback;
        public IntPtr Context;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, ref SubscribeParameters recipient, out IntPtr registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
