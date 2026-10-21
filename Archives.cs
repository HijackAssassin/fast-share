using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace FastShare;

/// <summary>
/// Compressing and extracting through WinRAR, when it is installed.
///
/// Deliberately shells out rather than bundling anything. RAR is proprietary - there is no
/// library we could ship that creates one - so the only honest options are "use the tool the
/// user already has" or "do not offer it". The menu entries stay hidden entirely when WinRAR
/// is absent, so nobody is offered a button that cannot work.
///
/// Note this runs on whichever machine holds the files. Archiving a folder you are browsing
/// over a connection would mean asking the FAR side to run a program, which is a different and
/// much larger question - so it is offered only for your own files.
/// </summary>
public static class Archives
{
    /// <summary>Full path to Rar.exe, or null when WinRAR is not installed.</summary>
    public static string? RarPath { get; } = FindRar();

    /// <summary>Full path to UnRAR.exe, or null.</summary>
    public static string? UnrarPath { get; } = FindUnrar();

    public static bool CanCompress => RarPath is not null;
    public static bool CanExtract => UnrarPath is not null;

    private static string? FindRar() => Find("Rar.exe", "WinRAR.exe");
    private static string? FindUnrar() => Find("UnRAR.exe", "WinRAR.exe");

    /// <summary>
    /// Looks where WinRAR actually registers itself before falling back to the usual folders.
    /// Registry first, because a user who installed it elsewhere still has the key.
    /// </summary>
    private static string? Find(params string[] names)
    {
        var folders = new List<string>();

        foreach (var key in new[]
                 {
                     @"SOFTWARE\WinRAR",
                     @"SOFTWARE\WOW6432Node\WinRAR",
                 })
        {
            try
            {
                using var handle = Registry.LocalMachine.OpenSubKey(key)
                                   ?? Registry.CurrentUser.OpenSubKey(key);

                if (handle?.GetValue("exe64") as string is { Length: > 0 } exe64)
                    folders.Add(Path.GetDirectoryName(exe64) ?? "");

                if (handle?.GetValue("exe32") as string is { Length: > 0 } exe32)
                    folders.Add(Path.GetDirectoryName(exe32) ?? "");
            }
            catch { }
        }

        folders.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinRAR"));
        folders.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinRAR"));

        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>Whether a name looks like something UnRAR could open.</summary>
    public static bool IsArchive(string name) =>
        Path.GetExtension(name).Equals(".rar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Packs the given local paths into <paramref name="archivePath"/>.
    ///
    /// Run with -ep1 so the archive contains the files relative to the folder they came from
    /// rather than their full drive path - otherwise extracting produces C:\Users\... inside
    /// the destination.
    /// </summary>
    public static Task<string?> CompressAsync(string archivePath, IReadOnlyList<string> items,
        string workingDirectory, IProgress<string>? progress, CancellationToken ct)
    {
        if (RarPath is null) return Task.FromResult<string?>("WinRAR is not installed.");

        var arguments = new List<string> { "a", "-ep1", "-r", "-y", "--", archivePath };
        arguments.AddRange(items);

        progress?.Report($"Compressing {items.Count} item(s) into {Path.GetFileName(archivePath)}…");
        return RunAsync(RarPath, arguments, workingDirectory, ct);
    }

    /// <summary>Unpacks an archive into a folder, keeping the paths stored inside it.</summary>
    public static Task<string?> ExtractAsync(string archivePath, string destination,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (UnrarPath is null) return Task.FromResult<string?>("WinRAR is not installed.");

        // The trailing separator matters: UnRAR treats a bare name as a file, not a folder.
        var target = destination.EndsWith(Path.DirectorySeparatorChar)
            ? destination
            : destination + Path.DirectorySeparatorChar;

        progress?.Report($"Extracting {Path.GetFileName(archivePath)}…");
        return RunAsync(UnrarPath, ["x", "-o+", "-y", "--", archivePath, target],
            Path.GetDirectoryName(archivePath) ?? destination, ct);
    }

    /// <summary>
    /// Runs the tool and returns null on success, or what went wrong.
    ///
    /// Arguments go through ArgumentList rather than a command string, so a file called
    /// "my movie".mkv or one containing a quote cannot turn into extra arguments.
    /// </summary>
    private static async Task<string?> RunAsync(string exe, IReadOnlyList<string> arguments,
        string workingDirectory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return "Could not start WinRAR.";

            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0) return null;

            Diagnostics.Log("archive", $"{Path.GetFileName(exe)} exit {process.ExitCode}: {stderr.Trim()}");
            return Describe(process.ExitCode, stderr);
        }
        catch (OperationCanceledException)
        {
            return "Cancelled.";
        }
        catch (Exception ex)
        {
            Diagnostics.Log("archive", "could not run WinRAR", ex);
            return ex.Message;
        }
    }

    /// <summary>RAR's exit codes, which are documented but not self-explanatory.</summary>
    private static string Describe(int code, string stderr)
    {
        var detail = stderr.Trim();
        if (detail.Length > 0) return detail;

        return code switch
        {
            1 => "Finished with warnings — some files may not have been included.",
            2 => "WinRAR reported a fatal error.",
            3 => "The archive is corrupt (checksum failed).",
            5 => "Could not write the archive — check the folder is writable.",
            8 => "Not enough memory.",
            10 => "No files matched.",
            11 => "The archive needs a password.",
            _ => $"WinRAR failed (exit code {code}).",
        };
    }
}
