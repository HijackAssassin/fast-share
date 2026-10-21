using System.IO;

namespace FastShare;

/// <summary>What someone can do to a folder's contents - the same four rights a user gets.</summary>
public enum FolderOp { Download, Send, Modify, Delete }

/// <summary>
/// A per-folder rule set on the Share tab. Each switch left on means "whatever the user is
/// allowed"; each one turned off means "nobody, whatever their own permissions say" - for that
/// folder and everything inside it. A rule can only take rights away, never grant them.
/// </summary>
public sealed class FolderRule
{
    /// <summary>The real folder on disk: a shared folder, or any folder inside one.</summary>
    public string Path { get; set; } = "";

    public bool Download { get; set; } = true;
    public bool Send { get; set; } = true;
    public bool Modify { get; set; } = true;
    public bool Delete { get; set; } = true;

    public bool Allows(FolderOp op) => op switch
    {
        FolderOp.Download => Download,
        FolderOp.Send => Send,
        FolderOp.Modify => Modify,
        _ => Delete,
    };

    /// <summary>All four on: the same as having no rule at all.</summary>
    public bool IsOpen => Download && Send && Modify && Delete;

    public string Summary
    {
        get
        {
            var off = new List<string>(4);
            if (!Download) off.Add("download");
            if (!Send) off.Add("upload");
            if (!Modify) off.Add("modify");
            if (!Delete) off.Add("delete");
            return off.Count == 0 ? "no limits" : "no " + string.Join(", ", off);
        }
    }

    public static string Normalise(string path)
    {
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch { return path; }
    }
}

/// <summary>
/// The set of folders being shared, presented to the far side as one virtual tree.
///
/// Path "" is the virtual root and lists each share by name. "Photos/2024/x.jpg" means the
/// share named Photos, then 2024/x.jpg beneath it. Share names are display names, never
/// real paths, so the far side never learns where anything actually lives on disk.
///
/// TryResolve is the single choke point that turns a path from the network into a path on
/// disk. Nothing else may call Path.Combine on remote input. With upload and delete enabled,
/// a gap here is not an information leak - it is arbitrary file destruction.
/// </summary>
public sealed class ShareSet
{
    private readonly Dictionary<string, string> _roots;

    /// <summary>Folder rules (normalised paths) that turn rights off for everyone in a subtree.</summary>
    private readonly List<(string Root, FolderRule Rule)> _rules;

    /// <summary>Private clone used by Restrict, which already has resolved name-to-root pairs.</summary>
    private ShareSet(Dictionary<string, string> roots, List<(string Root, FolderRule Rule)> rules)
    {
        _roots = roots;
        _rules = rules;
    }

    /// <summary>
    /// Whether the folder rules let anyone do <paramref name="op"/> to this path. The most
    /// restrictive rule on the way down wins.
    /// </summary>
    public bool Allows(FolderOp op, string fullPath) =>
        !_rules.Exists(r => !r.Rule.Allows(op) && IsInside(r.Root, fullPath));

    /// <summary>
    /// The same, for an operation on a whole folder (delete, rename, move): refused as well if
    /// any folder inside it has a rule turning that right off - otherwise deleting the parent
    /// would take the protected folder with it.
    /// </summary>
    public bool AllowsTree(FolderOp op, string fullPath) =>
        Allows(op, fullPath) &&
        !_rules.Exists(r => !r.Rule.Allows(op) && IsInside(fullPath, r.Root));

    public bool HasRules => _rules.Count > 0;

    /// <summary>
    /// A view limited to the named shares. Used to enforce per-peer folder access: a peer who
    /// was only granted two of your five folders gets a ShareSet containing exactly those two,
    /// so every path check, listing and manifest is narrowed at the source rather than each
    /// endpoint having to remember a filter.
    /// </summary>
    public ShareSet Restrict(IReadOnlyCollection<string>? allowed)
    {
        // Null means "everything", so folders shared later are picked up automatically.
        //
        // An EMPTY list means "nothing", and must not be confused with null. It used to fall
        // into the same branch, so unticking every folder for a peer - the clearest way there
        // is to say "you get none of these" - silently granted them all of them instead.
        if (allowed is null) return this;
        if (allowed.Count == 0) return new ShareSet(new Dictionary<string, string>(), _rules);

        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in allowed)
        {
            if (_roots.TryGetValue(name, out var root)) kept[name] = root;
        }

        return new ShareSet(kept, _rules);
    }

    public ShareSet(IEnumerable<string> folders, IEnumerable<FolderRule>? rules = null)
    {
        _rules = (rules ?? [])
            .Where(r => !r.IsOpen && !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => (FolderRule.Normalise(r.Path), r))
            .ToList();

        _roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var name = Path.GetFileName(full);

            // A drive root like "D:\" has no file name component, so it is shown as "D:" -
            // what it is called everywhere else on Windows.
            //
            // Trimmed by hand rather than with TrimEndingDirectorySeparator, which refuses to
            // touch a root: the stored root must stay "D:\", but the NAME must not contain a
            // separator or path lookups would split on it.
            if (string.IsNullOrEmpty(name)) name = full.TrimEnd(Path.DirectorySeparatorChar);

            var unique = name;
            for (var n = 2; _roots.ContainsKey(unique); n++) unique = $"{name} ({n})";
            _roots[unique] = full;
        }
    }

    public IReadOnlyCollection<string> Names => _roots.Keys;
    public int Count => _roots.Count;

    /// <summary>The virtual root: one pseudo-folder per share.</summary>
    public List<Entry> RootEntries() =>
        _roots.Keys
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new Entry(n, true, 0,
                new DateTimeOffset(Directory.GetLastWriteTimeUtc(_roots[n])).ToUnixTimeMilliseconds(),
                new DateTimeOffset(Directory.GetCreationTimeUtc(_roots[n])).ToUnixTimeMilliseconds(),
                HasSubfolders(_roots[n])))
            .ToList();

    /// <summary>
    /// Maps a remote path to a real path. Returns false for anything that escapes its share,
    /// names an unknown share, is rooted, or crosses a symlink or junction.
    /// </summary>
    public bool TryResolve(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalised = path.Replace('\\', '/').Trim('/');
        if (normalised.Length == 0) return false;

        var slash = normalised.IndexOf('/');
        var shareName = slash < 0 ? normalised : normalised[..slash];
        var rest = slash < 0 ? "" : normalised[(slash + 1)..];

        if (!_roots.TryGetValue(shareName, out var root)) return false;

        if (rest.Length == 0)
        {
            full = root;
            return true;
        }

        // Rooted input ("C:\Windows", "\\server\share", "/etc") is never legitimate.
        if (Path.IsPathRooted(rest)) return false;

        // A colon below the share name can only be an NTFS alternate data stream
        // ("file.txt:secret"), which would read or hide data no listing ever shows.
        if (rest.Contains(':')) return false;

        string candidate;
        try
        {
            // GetFullPath collapses "..", so the prefix check below cannot be walked past.
            candidate = Path.GetFullPath(Path.Combine(root, rest.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return false;   // malformed, invalid characters, or too long
        }

        if (!IsInside(root, candidate)) return false;
        if (CrossesForbidden(root, candidate)) return false;

        full = candidate;
        return true;
    }

    /// <summary>True if the path names a whole share rather than something inside one.</summary>
    public bool IsShareRoot(string fullPath) =>
        _roots.Values.Any(r => r.Equals(fullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a resolved path sits at or beneath a share root.
    ///
    /// The separator has to be added conditionally. A drive root is the one path that already
    /// ends with one - TrimEndingDirectorySeparator deliberately leaves "E:\" alone, since
    /// "E:" means something different - so blindly appending produced "E:\\" and nothing on
    /// that drive ever matched. Sharing a whole drive worked at the top level and then 404'd
    /// on every folder inside it.
    /// </summary>
    private static bool IsInside(string root, string candidate)
    {
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walks from the target up to the share root and rejects anything along the way that no
    /// listing would ever show.
    ///
    /// A junction or symlink inside a share can resolve to a target outside it, and
    /// GetFullPath will not catch that because the escape happens in the filesystem rather
    /// than in the string. Hidden and system entries are refused too: listings never show
    /// them, but a peer who guessed the name could still fetch one directly - sharing a
    /// profile folder would otherwise hand out AppData, including this app's own config.
    /// </summary>
    private static bool CrossesForbidden(string root, string candidate)
    {
        const FileAttributes forbidden =
            FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System;

        var current = candidate;
        while (current.Length > root.Length)
        {
            try
            {
                if ((File.GetAttributes(current) & forbidden) != 0) return true;
            }
            catch (FileNotFoundException) { /* upload targets do not exist yet */ }
            catch (DirectoryNotFoundException) { }
            catch { return true; }   // anything unexpected: refuse rather than guess

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// Whether a folder contains any subfolder. The tree uses this to decide if a node gets a
    /// disclosure arrow - without it every folder shows one and half of them expand to nothing.
    /// Enumeration stops at the first hit, so this is one directory probe, not a full listing.
    /// </summary>
    public static bool HasSubfolders(string path)
    {
        try
        {
            foreach (var child in Directory.EnumerateDirectories(path))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                    return true;
            }
        }
        catch { /* unreadable folders simply report none */ }
        return false;
    }

    /// <summary>
    /// Entries the far side may see. Hidden and system files stay invisible, and so do
    /// half-finished downloads - a transfer landing in a shared folder is not something to
    /// offer anyone until it has been moved into place.
    /// </summary>
    public static bool IsVisible(FileSystemInfo info)
    {
        var a = info.Attributes;
        return (a & FileAttributes.ReparsePoint) == 0
            && (a & FileAttributes.System) == 0
            && (a & FileAttributes.Hidden) == 0
            && !IsPartial(info.Name);
    }

    /// <summary>In-progress transfer files and their resume notes.</summary>
    public static bool IsPartial(string name) =>
        name.EndsWith(PartFiles.Suffix, StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(PartFiles.NoteSuffix, StringComparison.OrdinalIgnoreCase);
}
