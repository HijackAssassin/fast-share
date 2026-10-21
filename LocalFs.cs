using System.IO;

namespace FastShare;

/// <summary>
/// Write operations on your own shared folders, for when the Browse tab is pointed at
/// localhost.
///
/// Browsing your own shares reads straight off the disk through <see cref="LocalSource"/>
/// rather than through a socket, so there is no <see cref="ApiClient"/> to carry a delete or a
/// rename. This is the matching write side: the same operations the server exposes over the
/// wire, done directly on disk, and guarded by exactly the same choke point
/// (<see cref="ShareSet.TryResolve"/>) so browsing your own files cannot reach outside the
/// folders you actually share.
///
/// Each method returns null on success or a short, user-facing reason on failure - the same
/// shape the <see cref="ApiClient"/> calls return, so the callers read identically either way.
/// </summary>
public static class LocalFs
{
    public static string? Delete(ShareSet shares, IEnumerable<string> paths)
    {
        var targets = new List<string>();
        foreach (var remote in paths)
        {
            if (!shares.TryResolve(remote, out var full)) return "That item is not in a shared folder.";
            // A whole share is un-shared from the Share tab, not deleted from here.
            if (shares.IsShareRoot(full)) return "That is a shared folder itself — remove it on the Share tab.";
            targets.Add(full);
        }

        var failures = new List<string>();
        foreach (var target in targets) DeleteTree(target, failures);
        return failures.Count == 0 ? null : Summarise(failures);
    }

    /// <summary>
    /// Deletes a file or a whole folder, carrying on past anything it cannot remove.
    ///
    /// Directory.Delete(recursive) gives up at the first file it cannot delete - one open in
    /// another program, or marked read-only - having already removed part of the folder. Each
    /// press of Delete then took away a bit more, which is how a 160 GB folder needed pressing
    /// ten times. This clears read-only marks, deletes every file it can (several at once: an
    /// SSD deletes much faster in parallel), removes links to other folders without following
    /// them, and names what it had to leave, so one press does all that can be done.
    /// </summary>
    public static void DeleteTree(string target, List<string> failures)
    {
        void Fail(string path, Exception ex)
        {
            lock (failures) if (failures.Count < 500) failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }

        try
        {
            if (File.Exists(target))
            {
                ClearReadOnly(target);
                File.Delete(target);
                return;
            }
            if (!Directory.Exists(target)) return;

            // A link to another folder (junction, symlink) is removed, never walked into.
            if (new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(target);
                return;
            }
        }
        catch (Exception ex) { Fail(target, ex); return; }

        var folders = new List<string> { target };
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo sub)
                    {
                        folders.Add(sub.FullName);
                        // Links are listed (so they get removed) but their contents are not.
                        if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(sub.FullName);
                    }
                    else files.Add(entry.FullName);
                }
            }
            catch (Exception ex) { Fail(folder, ex); }
        }

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, file =>
        {
            try
            {
                ClearReadOnly(file);
                File.Delete(file);
            }
            catch (Exception ex) { Fail(file, ex); }
        });

        // Deepest first; a folder still holding something undeletable is simply left in place.
        foreach (var folder in folders.OrderByDescending(f => f.Length))
        {
            try
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists) continue;
                if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
                    info.Attributes &= ~FileAttributes.ReadOnly;
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !info.EnumerateFileSystemInfos().Any())
                    info.Delete();
            }
            catch (Exception ex) { Fail(folder, ex); }
        }
    }

    private static void ClearReadOnly(string file)
    {
        var attributes = File.GetAttributes(file);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
    }

    /// <summary>"3 items could not be deleted - usually because another program has them open:" and the first few.</summary>
    public static string Summarise(List<string> failures)
    {
        var shown = failures.Take(8).ToList();
        var more = failures.Count > shown.Count ? $"\n…and {failures.Count - shown.Count:N0} more" : "";
        return $"{failures.Count:N0} item(s) could not be deleted - usually because another program has them open " +
               $"(close it and delete again):\n{string.Join("\n", shown)}{more}";
    }

    public static string? Create(ShareSet shares, string path, bool isDirectory)
    {
        if (!shares.TryResolve(path, out var full)) return "That location is not in a shared folder.";
        if (Directory.Exists(full) || File.Exists(full)) return "Something with that name already exists.";

        try
        {
            if (isDirectory)
            {
                Directory.CreateDirectory(full);
            }
            else
            {
                var parent = Path.GetDirectoryName(full);
                if (parent is not null) Directory.CreateDirectory(parent);
                using (File.Create(full)) { }
            }
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string? Rename(ShareSet shares, string path, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            return "That name is not allowed.";
        }

        if (!shares.TryResolve(path, out var full)) return "That item is not in a shared folder.";
        if (shares.IsShareRoot(full)) return "A shared folder is renamed on the Share tab, not here.";

        var parent = Path.GetDirectoryName(full);
        if (parent is null) return "That item cannot be renamed.";

        var target = Path.Combine(parent, name);
        if (File.Exists(target) || Directory.Exists(target)) return "Something with that name already exists.";

        try
        {
            if (Directory.Exists(full)) Directory.Move(full, target);
            else if (File.Exists(full)) File.Move(full, target);
            else return "That item no longer exists.";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Copy or move items into another folder. Mirrors the server's transplant.</summary>
    public static string? Transplant(ShareSet shares, IEnumerable<string> paths, string destination, bool move)
    {
        if (!shares.TryResolve(destination, out var destFull) || !Directory.Exists(destFull))
            return "That destination is not a shared folder.";

        var sources = new List<string>();
        foreach (var path in paths)
        {
            if (!shares.TryResolve(path, out var full)) return "One of those items is not in a shared folder.";
            if (move && shares.IsShareRoot(full)) return "A shared folder is managed on the Share tab.";
            sources.Add(full);
        }

        var failures = new List<string>();
        foreach (var source in sources)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            var target = Path.Combine(destFull, name);
            try
            {
                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{name}: already there");
                    continue;
                }

                if (Directory.Exists(source))
                {
                    if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{name}: cannot go inside itself");
                        continue;
                    }
                    if (move) Directory.Move(source, target);
                    else CopyTree(source, target);
                }
                else if (File.Exists(source))
                {
                    if (move) File.Move(source, target, overwrite: true);
                    else File.Copy(source, target, overwrite: true);
                }
            }
            catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
        }

        return failures.Count == 0 ? null : string.Join("\n", failures);
    }

    /// <summary>
    /// Recursive folder copy that stays inside what the far side could see. Junctions and
    /// symlinks are skipped rather than followed - TryResolve only vetted the top folder, and
    /// following a link further down would copy content from outside the share into it.
    /// Hidden and system entries are skipped for the same reason listings hide them.
    /// </summary>
    public static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var info in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (!ShareSet.IsVisible(info)) continue;
            var target = Path.Combine(destination, info.Name);

            if ((info.Attributes & FileAttributes.Directory) != 0) CopyTree(info.FullName, target);
            else File.Copy(info.FullName, target, overwrite: true);
        }
    }
}
