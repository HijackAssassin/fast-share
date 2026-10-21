using System.Reflection;

namespace FastShare;

/// <summary>
/// What build this is.
///
/// All that survives of the old updater. FastShare used to check GitHub Releases and install
/// updates in place; that is gone along with the repository, so the version is now only a
/// label - shown in the title bar and in Settings so a bug report can say which build it came
/// from. Set it in one place, Website\version.txt in the project folder (build.bat stamps it in;
/// FastShare.csproj reads it as a fallback, 0.0.0 when there is none).
/// </summary>
public static class AppVersion
{
    public static Version Current { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string Text => Current.ToString(3);

    /// <summary>
    /// What this runs on, shown on the other PC's Connections tab in place of an address:
    /// "Windows 11", "Windows 10". Windows 11 still reports 10.0, so it is told apart by build.
    /// </summary>
    public static string Device { get; } =
        OperatingSystem.IsWindows()
            ? Environment.OSVersion.Version is { Major: 10, Build: >= 22000 } ? "Windows 11"
              : Environment.OSVersion.Version.Major == 10 ? "Windows 10" : "Windows"
            : OperatingSystem.IsAndroid() ? "Android" : Environment.OSVersion.Platform.ToString();
}
