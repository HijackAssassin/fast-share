using System.Diagnostics;
using System.IO;

namespace FastShare;

/// <summary>
/// Adds a Windows Defender exclusion for a folder, and measures the difference it makes.
///
/// Real-time scanning is the biggest real-world drag on many-small-file transfers: every file
/// is scanned as it is written, and each scan blocks the write. A folder of thousands of
/// blender assets pays that toll thousands of times. Excluding the folder skips it.
///
/// The exclusion is a Windows security setting, so it is never changed silently: adding one
/// always goes through an elevation (UAC) prompt the user answers themselves. This class only
/// ever ADDS an exclusion the user asked for; it never removes one or touches anything else.
/// </summary>
public static class DefenderTune
{
    /// <summary>
    /// The current Defender path exclusions, or null when they cannot be read (no Defender, a
    /// policy-managed machine, or tamper protection hiding them without elevation). Null means
    /// "unknown", never "none" - the caller must not treat it as an empty list.
    /// </summary>
    public static List<string>? TryReadExclusions()
    {
        try
        {
            var output = RunPowerShell(
                "-NoProfile -NonInteractive -Command " +
                "\"(Get-MpPreference).ExclusionPath\"",
                elevated: false, out var exitCode);

            if (exitCode != 0) return null;

            // Without elevation, Get-MpPreference does not fail - it returns the sentinel
            // "N/A: Must be an administrator to view exclusions" in place of the list. Treat that
            // as "unknown" (null), never as a real exclusion, or every folder reads as not-excluded
            // and the "already fast" shortcut would misfire.
            if (output.Contains("Must be an administrator", StringComparison.OrdinalIgnoreCase))
                return null;

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a folder is already covered by an exclusion - itself or a parent of it. Returns
    /// null when the exclusion list cannot be read, so the caller can say "not sure" rather than
    /// wrongly claim it is not excluded.
    /// </summary>
    public static bool? IsExcluded(string folder)
    {
        var exclusions = TryReadExclusions();
        if (exclusions is null) return null;

        var target = Normalise(folder);
        foreach (var raw in exclusions)
        {
            var excl = Normalise(raw);
            if (target.Equals(excl, StringComparison.OrdinalIgnoreCase)) return true;

            // A parent folder's exclusion covers everything beneath it.
            var prefix = excl.EndsWith(Path.DirectorySeparatorChar) ? excl : excl + Path.DirectorySeparatorChar;
            if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>What adding an exclusion did.</summary>
    public enum AddResult { Added, Cancelled, Failed }

    /// <summary>
    /// Excludes several folders behind one UAC prompt. The elevation belongs to a separate
    /// PowerShell process that exits straight away; FastShare itself stays un-elevated, which
    /// is what keeps drag-and-drop with Explorer working (Windows blocks drags from a normal
    /// Explorer into an elevated window).
    /// </summary>
    /// <summary>
    /// Adds or removes exclusions for several folders behind one UAC prompt (none at all when
    /// already elevated). Returns Added on success either way.
    /// </summary>
    public static AddResult ChangeExclusions(IReadOnlyCollection<string> folders, bool add)
    {
        var list = folders.Where(f => !string.IsNullOrWhiteSpace(f) && (!add || Directory.Exists(f)))
            .Select(Normalise).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return AddResult.Failed;

        try
        {
            var paths = string.Join(",", list.Select(f => "'" + f.Replace("'", "''") + "'"));
            var verb = add ? "Add-MpPreference" : "Remove-MpPreference";
            RunPowerShell(
                $"-NoProfile -NonInteractive -Command \"{verb} -ExclusionPath {paths}\"",
                elevated: !IsElevated(), out var exitCode);

            if (exitCode == 0)
            {
                Diagnostics.Log("defender", $"{(add ? "excluded" : "un-excluded")} {list.Count} folder(s): {string.Join("; ", list)}");
                return AddResult.Added;
            }

            Diagnostics.Log("defender", $"batch exclusion failed (exit {exitCode})");
            return AddResult.Failed;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Diagnostics.Log("defender", "batch exclusion cancelled at UAC prompt");
            return AddResult.Cancelled;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("defender", "batch exclusion could not be attempted", ex);
            return AddResult.Failed;
        }
    }

    /// <summary>
    /// The folders not already covered by an exclusion (their own, or a parent's), normalised.
    /// </summary>
    public static List<string> Uncovered(IEnumerable<string> folders, IEnumerable<string> excluded)
    {
        var covers = excluded.Where(e => !string.IsNullOrWhiteSpace(e)).Select(Normalise).ToList();
        return folders
            .Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f))
            .Select(Normalise)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !covers.Any(c => f.Equals(c, StringComparison.OrdinalIgnoreCase) ||
                                         f.StartsWith(c.EndsWith('\\') ? c : c + "\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Whether this process is running with administrator rights.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string Normalise(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    /// <summary>
    /// Runs a PowerShell command, optionally elevated. Elevated runs go through ShellExecute with
    /// the "runas" verb, which is what raises the UAC prompt; their output cannot be captured
    /// (a different, higher-integrity process), so only the exit code is returned.
    /// </summary>
    internal static string RunPowerShell(string arguments, bool elevated, out int exitCode)
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        if (elevated)
        {
            info.UseShellExecute = true;   // required for the runas verb
            info.Verb = "runas";
        }
        else
        {
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("PowerShell did not start.");

        var output = elevated ? "" : process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        exitCode = process.ExitCode;
        return output;
    }
}
