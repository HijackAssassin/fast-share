using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FastShare;

/// <summary>
/// Works out which real folder is under the mouse pointer.
///
/// This is what makes dragging files out of FastShare possible without implementing OLE
/// virtual-file drops. The proper way - handing Explorer a promise of bytes it fetches through
/// a COM callback - means running the download engine inside a callback Explorer controls.
/// Instead the drag is ours from start to finish: we follow the cursor, and when the button
/// comes up we simply ask Windows what is underneath and turn it into a path.
///
/// The cost of doing it this way is that only Explorer windows and the desktop can be dropped
/// on. Anything else - a chat window's upload box, a mail attachment - needs the real thing.
/// </summary>
public static class ShellDrop
{
    /// <summary>A resolved drop target: somewhere real that files can be written to.</summary>
    public sealed record Target(string Path, string Label);

    // =======================================================================
    // Win32
    // =======================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    private const uint GaRoot = 2;

    /// <summary>Screen position of the pointer, in physical pixels.</summary>
    public static (int X, int Y) CursorPosition =>
        GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    // =======================================================================
    // Resolving
    // =======================================================================

    /// <summary>
    /// Turns a screen position into a folder, or null if nothing there can accept files.
    /// </summary>
    /// <param name="ownWindow">Our own window handle, so dropping back onto FastShare is
    /// treated as "nowhere" rather than resolving to whatever is behind it.</param>
    public static Target? Resolve(int x, int y, IntPtr ownWindow)
    {
        IntPtr hwnd;
        try { hwnd = WindowFromPoint(new Point { X = x, Y = y }); }
        catch { return null; }

        if (hwnd == IntPtr.Zero) return null;

        var root = GetAncestor(hwnd, GaRoot);
        if (root == IntPtr.Zero) root = hwnd;

        // Dropping on ourselves does nothing. Without this, releasing over the file list
        // would resolve to whatever window happened to be behind the app.
        if (root == ownWindow) return null;

        // The desktop, which is a list view living inside Progman (or a WorkerW when a
        // wallpaper slideshow is running).
        var rootClass = ClassOf(root);
        if (rootClass is "Progman" or "WorkerW")
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!Directory.Exists(desktop)) return null;

            return Refine(desktop, x, y) ?? new Target(desktop, "Desktop");
        }

        // An open Explorer window. Ask the shell which folder it is showing.
        var folder = FolderOfExplorerWindow(root);
        if (folder is not null && Directory.Exists(folder))
            return Refine(folder, x, y) ?? new Target(folder, FriendlyName(folder));

        return null;
    }

    /// <summary>
    /// Narrows a window's folder to the specific subfolder the pointer is actually over, so
    /// hovering a folder icon drops inside it rather than beside it.
    ///
    /// Done through UI Automation rather than the usual list-view messages: Explorer draws its
    /// items with DirectUI, not a real SysListView32, so LVM_HITTEST has nothing to hit. UIA is
    /// the only thing that can see those items - and it works on the desktop too, which is a
    /// real list view, so one path covers both.
    /// </summary>
    private static Target? Refine(string folder, int x, int y)
    {
        var name = ItemNameUnderPointer(x, y);
        if (string.IsNullOrWhiteSpace(name)) return null;

        // The name comes from another process's UI, so it is untrusted input: it must not be
        // able to name a path outside the folder the window is showing.
        if (name.AsSpan().ContainsAny(Path.GetInvalidFileNameChars())) return null;

        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(folder, name)); }
        catch { return null; }

        var root = Path.TrimEndingDirectorySeparator(folder);
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        // Only a folder is a drop target. Hovering a file means the folder it lives in.
        return Directory.Exists(candidate) ? new Target(candidate, FriendlyName(candidate)) : null;
    }

    /// <summary>
    /// The name of the shell item under the pointer, or null if it is over empty space.
    ///
    /// Throttled: a UIA hit test costs milliseconds, and the drag asks on every mouse move.
    /// Without this the chip visibly lags the cursor.
    /// </summary>
    private static string? ItemNameUnderPointer(int x, int y)
    {
        var now = Environment.TickCount64;
        if (now - _lastProbeAt < ProbeIntervalMs && (x, y) == _lastProbePoint) return _lastProbeName;

        _lastProbeAt = now;
        _lastProbePoint = (x, y);
        _lastProbeName = null;

        try
        {
            var element = System.Windows.Automation.AutomationElement.FromPoint(
                new System.Windows.Point(x, y));

            // Walk up from whatever sub-element was hit (a label, an icon) to the row itself.
            var walker = System.Windows.Automation.TreeWalker.ControlViewWalker;
            for (var depth = 0; element is not null && depth < 6; depth++)
            {
                var type = element.Current.ControlType;
                if (type == System.Windows.Automation.ControlType.ListItem ||
                    type == System.Windows.Automation.ControlType.DataItem ||
                    type == System.Windows.Automation.ControlType.TreeItem)
                {
                    _lastProbeName = element.Current.Name;
                    break;
                }

                element = walker.GetParent(element);
            }
        }
        catch
        {
            // UIA can refuse or time out against a busy window. Falling back to the folder
            // the window is showing is a perfectly good answer.
        }

        return _lastProbeName;
    }

    private const int ProbeIntervalMs = 80;
    private static long _lastProbeAt;
    private static (int X, int Y) _lastProbePoint = (int.MinValue, int.MinValue);
    private static string? _lastProbeName;

    /// <summary>
    /// Finds the folder an Explorer window is displaying, by matching its handle against the
    /// shell's list of open windows.
    ///
    /// Late-bound on purpose: this needs no COM reference and no generated interop assembly,
    /// and if the shell is unavailable or returns something unexpected the whole thing simply
    /// degrades to "not a valid target".
    /// </summary>
    private static string? FolderOfExplorerWindow(IntPtr hwnd)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return null;

            dynamic? shell = Activator.CreateInstance(type);
            if (shell is null) return null;

            dynamic windows = shell.Windows();
            int count = windows.Count;

            for (var i = 0; i < count; i++)
            {
                dynamic window;
                try { window = windows.Item(i); } catch { continue; }
                if (window is null) continue;

                IntPtr handle;
                try { handle = (IntPtr)(long)window.HWND; } catch { continue; }
                if (handle != hwnd) continue;

                // Internet Explorer windows share this collection with Explorer ones.
                try
                {
                    string program = window.FullName;
                    if (!program.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                        return null;
                }
                catch { /* no FullName: fall through and try for a path anyway */ }

                // Self.Path is the dependable one - it gives a real filesystem path for
                // library and special folders, where LocationURL can be a shell moniker.
                try
                {
                    string path = window.Document.Folder.Self.Path;
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return path;
                }
                catch { }

                try
                {
                    string url = window.LocationURL;
                    if (!string.IsNullOrWhiteSpace(url) &&
                        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                    {
                        return uri.LocalPath;
                    }
                }
                catch { }

                return null;
            }
        }
        catch
        {
            // Shell automation blocked or unavailable - treat as no target.
        }

        return null;
    }

    /// <summary>Folder name for the drag label, with the drive root spelled out in full.</summary>
    private static string FriendlyName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
