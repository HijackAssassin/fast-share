using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FastShare;

/// <summary>
/// The little label that follows the cursor during a drag, telling you what you are carrying
/// and where it would land.
///
/// It has to be invisible to hit testing at the OS level, not just inside WPF: the drop target
/// is worked out with WindowFromPoint, and a normal window sitting under the cursor would be
/// the answer every time. WS_EX_TRANSPARENT makes Windows look straight through it.
/// </summary>
public partial class DragGhost : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public DragGhost()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle,
                GetWindowLong(hwnd, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
    }

    /// <summary>
    /// Updates the label. <paramref name="target"/> is null when the pointer is somewhere that
    /// cannot accept a drop, which is shown rather than hidden - a chip reading "not a folder"
    /// is clearer than one that silently stops changing.
    /// </summary>
    public void Describe(int itemCount, string? target)
    {
        var what = itemCount == 1 ? "1 item" : $"{itemCount:N0} items";

        if (target is null)
        {
            Glyph.Text = "";                  // cancel
            Caption.Text = $"{what} — drop on a folder or the desktop";
        }
        else
        {
            Glyph.Text = "";                  // download
            Caption.Text = $"Copy {what} to {target}";
        }
    }

    /// <summary>
    /// Places the chip just below and right of the pointer, where it does not cover what is
    /// being pointed at. Takes physical screen pixels and converts, so it lands correctly on
    /// a scaled display.
    /// </summary>
    public void MoveTo(int screenX, int screenY)
    {
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var point = transform.Transform(new System.Windows.Point(screenX + 18, screenY + 18));

        Left = point.X;
        Top = point.Y;
    }
}
