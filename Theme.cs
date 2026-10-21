using System.Windows;
using System.Windows.Media;

namespace FastShare;

/// <summary>
/// Light and dark palettes.
///
/// Every colour the UI uses is a DynamicResource keyed here rather than a literal, so
/// switching theme is just replacing brush values in the application resource dictionary.
/// Nothing reloads and no window is recreated - bindings pick the new brushes up live.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var resources = Application.Current.Resources;

        void Set(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();     // frozen brushes are cheaper and safe across threads
            resources[key] = brush;
        }

        if (dark)
        {
            Set("Chrome", "#202020");
            Set("Content", "#1B1B1B");
            Set("Line", "#333333");
            Set("Text", "#F0F0F0");
            Set("Subtle", "#A8A8A8");
            Set("Accent", "#4CC2FF");
            Set("OnAccent", "#0A1A22");
            Set("Good", "#6CCB5F");
            Set("Bad", "#FF6B5E");
            Set("Idle", "#6E6E6E");
            Set("ButtonBg", "#2D2D2D");
            Set("ButtonBorder", "#3F3F3F");
            Set("ButtonHover", "#383838");
            Set("InputBg", "#2B2B2B");
            Set("HeaderBg", "#262626");
            // A clear grey, well clear of both the content background and the near-white
            // text. #2A2A2A sat only a shade above the background, so a hovered folder in
            // dark mode was almost impossible to pick out.
            Set("RowHover", "#454545");
            Set("RowSelected", "#0F3A5A");
            Set("ItemSelected", "#494949");
            Set("ItemSelectedBorder", "#DADADA");
            Set("FolderGlyph", "#E8B931");

            // Near-white in dark mode: the accent blue it used to share with links was too
            // close to the background to pick out at a glance.
            Set("PersonGlyph", "#F2F2F2");
            Set("FileGlyph", "#A0A0A0");
            Set("DangerBg", "#3A1F1D");
            Set("DangerBorder", "#5E2C27");
        }
        else
        {
            Set("Chrome", "#F3F3F3");
            Set("Content", "#FFFFFF");
            Set("Line", "#E0E0E0");
            Set("Text", "#1A1A1A");
            Set("Subtle", "#5A5A5A");
            Set("Accent", "#0078D4");
            Set("OnAccent", "#FFFFFF");
            Set("Good", "#107C10");
            Set("Bad", "#C42B1C");
            Set("Idle", "#9A9A9A");
            Set("ButtonBg", "#FDFDFD");
            Set("ButtonBorder", "#CCCCCC");
            Set("ButtonHover", "#EAEAEA");
            Set("InputBg", "#FFFFFF");
            Set("HeaderBg", "#F3F3F3");
            Set("RowHover", "#F0F0F0");
            Set("RowSelected", "#CCE4F7");
            Set("ItemSelected", "#CCE4F7");
            Set("ItemSelectedBorder", "#99C9EF");
            Set("FolderGlyph", "#DCA612");
            Set("PersonGlyph", "#0A63B8");
            Set("FileGlyph", "#6E6E6E");
            Set("DangerBg", "#FDF3F2");
            Set("DangerBorder", "#E8B4AE");
        }
    }
}
