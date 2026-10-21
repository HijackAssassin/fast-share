using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FastShare;

/// <summary>
/// The notification-area icon, and the little pop-up notifications that come out of it.
///
/// Done directly against Shell_NotifyIcon rather than by pulling in Windows Forms for its
/// NotifyIcon: that would add a second UI framework (and its DLLs) to a self-contained build
/// for one icon. On Windows 10 and later a "balloon" from here is shown as an ordinary toast.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x42;   // WM_APP + something of our own
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const int NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NifShowTip = 0x80;
    private const int NiifInfo = 1, NiifWarning = 2;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const int NinBalloonHide = 0x0403, NinBalloonTimeout = 0x0404, NinBalloonUserClick = 0x0405, NinSelect = 0x0400;

    private readonly HwndSource _window;
    private readonly nint _icon;
    private readonly bool _ownsIcon;
    private bool _added;

    /// <summary>The icon (or a notification from it) was clicked.</summary>
    public event Action? Activated;

    /// <summary>Right-click: the owner shows its own menu.</summary>
    public event Action? MenuRequested;

    /// <summary>A notification went away: timed out, was dismissed or was clicked.</summary>
    public event Action? NotificationGone;

    public TrayIcon(Window owner, string tooltip)
    {
        // The icon's clicks arrive as messages to the owning window, which exists (hidden or
        // not) for as long as the app runs.
        _window = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle());
        _window.AddHook(OnMessage);

        // The exe's own icon if it has one, otherwise the stock application icon.
        var exe = Environment.ProcessPath ?? "";
        _icon = ExtractIconEx(exe, 0, out var large, out var small, 1) > 0 && small != 0 ? small : 0;
        if (large != 0) DestroyIcon(large);
        _ownsIcon = _icon != 0;
        if (_icon == 0) _icon = LoadIcon(0, new nint(32512));   // IDI_APPLICATION

        var data = NewData();
        data.uFlags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = Clip(tooltip, 127);
        _added = Shell_NotifyIcon(NimAdd, ref data);

        data.uTimeoutOrVersion = 4;   // NOTIFYICON_VERSION_4
        if (_added) Shell_NotifyIcon(NimSetVersion, ref data);
    }

    public void SetTooltip(string text)
    {
        if (!_added) return;
        var data = NewData();
        data.uFlags = NifTip | NifShowTip;
        data.szTip = Clip(text, 127);
        Shell_NotifyIcon(NimModify, ref data);
    }

    /// <summary>Shows a notification. Windows decides how long it stays and may queue it.</summary>
    public void Notify(string title, string text, bool warning = false)
    {
        if (!_added) return;
        var data = NewData();
        data.uFlags = NifInfo;
        data.szInfoTitle = Clip(title, 63);
        data.szInfo = Clip(text, 255);
        data.dwInfoFlags = warning ? NiifWarning : NiifInfo;
        Shell_NotifyIcon(NimModify, ref data);
    }

    private nint OnMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != CallbackMessage) return 0;

        // With NOTIFYICON_VERSION_4 the event is in the low word of lParam.
        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case WmLButtonUp:
            case NinSelect:
            case NinBalloonUserClick:
                Activated?.Invoke();
                if ((int)(lParam.ToInt64() & 0xFFFF) == NinBalloonUserClick) NotificationGone?.Invoke();
                handled = true;
                break;

            case NinBalloonHide:
            case NinBalloonTimeout:
                NotificationGone?.Invoke();
                handled = true;
                break;

            case WmRButtonUp:
            case WmContextMenu:
                MenuRequested?.Invoke();
                handled = true;
                break;
        }
        return 0;
    }

    private NotifyIconData NewData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        if (_ownsIcon) DestroyIcon(_icon);
        _window.RemoveHook(OnMessage);
    }

    /// <summary>
    /// Flashes a window's taskbar button until it is brought forward - the standard way to say
    /// "something here needs you" without stealing focus from whatever you are doing.
    /// </summary>
    public static void FlashTaskbar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;

        var info = new FlashInfo
        {
            cbSize = (uint)Marshal.SizeOf<FlashInfo>(),
            hwnd = handle,
            dwFlags = 3 | 0xC,   // FLASHW_ALL | FLASHW_TIMERNOFG
            uCount = uint.MaxValue,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint instance, nint name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
