using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kakitome.Application.Settings;

namespace Kakitome.Infrastructure.Shell;

/// <summary>A command shown in the notification-area context menu.</summary>
public sealed record TrayMenuItem(int Id, string Text, bool Enabled = true, bool IsSeparator = false)
{
    public static TrayMenuItem Separator { get; } = new(0, string.Empty, IsSeparator: true);
}

/// <summary>
/// Notification-area (tray) icon and global hotkeys backed by a hidden Win32 window. Must be created and used on
/// the UI thread; events are raised on that thread from the app's message loop. Survives Explorer restarts.
/// </summary>
public sealed unsafe partial class NotificationAreaHost : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint CallbackMessage = WmApp + 1;
    private const uint WmHotkey = 0x0312;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmNull = 0x0000;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const uint IconId = 1;
    private const uint ModNoRepeat = 0x4000;
    private const uint MfString = 0x0, MfGrayed = 0x1, MfSeparator = 0x800;
    private const uint TpmReturnCmd = 0x0100, TpmRightButton = 0x0002;

    private static readonly Dictionary<IntPtr, NotificationAreaHost> Instances = [];

    private readonly string _className = "Kakitome.NotificationArea." + Guid.NewGuid().ToString("N");
    private readonly uint _taskbarCreatedMessage;
    private readonly HashSet<int> _hotkeys = [];
    private IntPtr _hwnd;
    private IntPtr _icon;
    private string _tooltip;
    private bool _iconAdded;

    public NotificationAreaHost(string iconPath, string tooltip)
    {
        _tooltip = tooltip;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        var classNamePtr = Marshal.StringToHGlobalUni(_className);
        try
        {
            var wc = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = &WindowProc,
                Instance = GetModuleHandleW(null),
                ClassName = classNamePtr,
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePtr);
        }

        _hwnd = CreateWindowExW(0, _className, "Kakitome", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        }

        Instances[_hwnd] = this;
        SetIcon(iconPath);
    }

    /// <summary>Left click or keyboard selection of the icon.</summary>
    public event EventHandler? Invoked;

    /// <summary>Right click / context-menu key; the handler typically calls <see cref="ShowMenu"/>.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>A registered global hotkey was pressed (argument: hotkey id).</summary>
    public event EventHandler<int>? HotkeyPressed;

    public void SetIcon(string iconPath)
    {
        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        var icon = LoadImageW(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        if (icon == IntPtr.Zero)
        {
            return;
        }

        var old = _icon;
        _icon = icon;
        AddOrUpdate();
        if (old != IntPtr.Zero)
        {
            DestroyIcon(old);
        }
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        AddOrUpdate();
    }

    /// <summary>Shows a context menu at the cursor and returns the chosen item id, or 0.</summary>
    public int ShowMenu(IReadOnlyList<TrayMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var menu = CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenuW(menu, MfSeparator, 0, null);
                }
                else
                {
                    AppendMenuW(menu, MfString | (item.Enabled ? 0 : MfGrayed), (nuint)item.Id, item.Text);
                }
            }

            GetCursorPos(out var point);
            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WmNull, 0, 0);
            return chosen;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>Registers a system-wide hotkey. Returns false when another app already owns the combination.</summary>
    public bool RegisterHotkey(int id, HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        UnregisterHotkey(id);
        var vk = VirtualKey(gesture.Key);
        if (vk == 0 || !RegisterHotKey(_hwnd, id, (uint)gesture.Modifiers | ModNoRepeat, vk))
        {
            return false;
        }

        _hotkeys.Add(id);
        return true;
    }

    public void UnregisterHotkey(int id)
    {
        if (_hotkeys.Remove(id))
        {
            UnregisterHotKey(_hwnd, id);
        }
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        foreach (var id in _hotkeys.ToList())
        {
            UnregisterHotkey(id);
        }

        if (_iconAdded)
        {
            var data = NewData(0);
            Shell_NotifyIconW(NimDelete, ref data);
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        Instances.Remove(_hwnd);
        DestroyWindow(_hwnd);
        UnregisterClassW(_className, GetModuleHandleW(null));
        _hwnd = IntPtr.Zero;
    }

    internal static uint VirtualKey(string key) => key switch
    {
        { Length: 1 } when char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0]) => key[0],
        "Space" => 0x20,
        "PageUp" => 0x21,
        "PageDown" => 0x22,
        "End" => 0x23,
        "Home" => 0x24,
        "Insert" => 0x2D,
        "Pause" => 0x13,
        _ when key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 24 => (uint)(0x70 + n - 1),
        _ => 0,
    };

    private void AddOrUpdate()
    {
        if (_hwnd == IntPtr.Zero || _icon == IntPtr.Zero)
        {
            return;
        }

        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        if (_iconAdded && Shell_NotifyIconW(NimModify, ref data))
        {
            return;
        }

        if (Shell_NotifyIconW(NimAdd, ref data))
        {
            data.VersionOrTimeout = NotifyIconVersion4;
            Shell_NotifyIconW(NimSetVersion, ref data);
            _iconAdded = true;
        }
    }

    private NotifyIconData NewData(uint flags)
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Hwnd = _hwnd,
            Id = IconId,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        var tip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip;
        tip.AsSpan().CopyTo(new Span<char>(data.Tip, 128));
        return data;
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case NinSelect or NinKeySelect or WmLButtonUp:
                    Invoked?.Invoke(this, EventArgs.Empty);
                    break;
                case WmContextMenu:
                    MenuRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == WmHotkey)
        {
            HotkeyPressed?.Invoke(this, unchecked((int)wParam.ToInt64()));
            return IntPtr.Zero;
        }

        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: the icon must be added again.
            _iconAdded = false;
            AddOrUpdate();
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return Instances.TryGetValue(hwnd, out var host)
                ? host.HandleMessage(hwnd, msg, wParam, lParam)
                : DefWindowProcW(hwnd, msg, wParam, lParam);
        }
#pragma warning disable CA1031 // Exceptions must never cross the native boundary.
        catch
#pragma warning restore CA1031
        {
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public IntPtr MenuName;
        public IntPtr ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint VersionOrTimeout;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WndClassEx wc);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClassW(string className, IntPtr instance);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? moduleName);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hwnd, int id);
}
