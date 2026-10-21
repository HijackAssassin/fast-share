using System.IO;

namespace FastShare;

/// <summary>
/// Turning a share set into listings and manifests.
///
/// Both directions need exactly this: the HTTPS endpoints answering someone who connected to
/// us, and the tunnel worker answering the host we connected to. Keeping one implementation
/// means a peer's folders look identical however the request arrived.
/// </summary>
public static class Listing
{
    private const int MaxManifestFiles = 2_000_000;

    /// <summary>Immediate children of one folder, or the shares themselves at the root.</summary>
    public static List<Entry>? Children(ShareSet shares, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return shares.RootEntries();
        if (!shares.TryResolve(path, out var directory) || !Directory.Exists(directory)) return null;

        try
        {
            var entries = new List<Entry>();
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (!ShareSet.IsVisible(info)) continue;

                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                entries.Add(new Entry(
                    info.Name,
                    isDirectory,
                    isDirectory ? 0 : ((FileInfo)info).Length,
                    new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds(),
                    isDirectory && ShareSet.HasSubfolders(info.FullName)));
            }
            return entries;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every file beneath a subtree, or beneath every share when the path is empty.</summary>
    /// <summary>
    /// Every file beneath a subtree. Given <paramref name="only"/>, files the folder rules do
    /// not allow that for are left out - a folder with downloading turned off simply is not
    /// offered for download, rather than failing file by file.
    /// </summary>
    public static Manifest? Tree(ShareSet shares, string? path, FolderOp? only)
    {
        Func<string, bool> keep = only is { } op && shares.HasRules ? full => shares.Allows(op, full) : _ => true;
        return Tree(shares, path, keep);
    }

    public static Manifest? Tree(ShareSet shares, string? path) => Tree(shares, path, _ => true);

    private static Manifest? Tree(ShareSet shares, string? path, Func<string, bool> keep)
    {
        var files = new List<ManifestFile>();
        var empty = new List<string>();
        long total = 0;
        var roots = new List<string>();

        if (string.IsNullOrWhiteSpace(path))
        {
            foreach (var name in shares.Names)
            {
                if (shares.TryResolve(name, out var root)) { roots.Add(root); Walk(root, name, files, empty, ref total, keep); }
            }
        }
        else
        {
            if (!shares.TryResolve(path, out var directory) || !Directory.Exists(directory)) return null;
            roots.Add(directory);
            Walk(directory, "", files, empty, ref total, keep);
        }

        // The disk these files came off, so the far side can log which end was slow. Computed
        // from the resolved roots, never from the remote path, so nothing here leaks a location.
        var drive = DriveDetect.Label(DriveDetect.ForPaths(roots));
        return new Manifest(files, total, drive == "?" ? null : drive, empty.Count == 0 ? null : empty);
    }

    private const int MaxEmptyFolders = 200_000;

    private static void Walk(string root, string prefix, List<ManifestFile> files, List<string> empty,
        ref long total, Func<string, bool> keep)
    {
        var stack = new Stack<(DirectoryInfo Dir, string Prefix)>();
        stack.Push((new DirectoryInfo(root), prefix));

        while (stack.Count > 0)
        {
            var (current, currentPrefix) = stack.Pop();

            List<FileSystemInfo> children;
            try { children = current.EnumerateFileSystemInfos().Where(ShareSet.IsVisible).ToList(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            // A folder with nothing visible in it would otherwise never arrive at all - the
            // manifest only lists files. "" is the requested folder itself.
            if (children.Count == 0 && empty.Count < MaxEmptyFolders) empty.Add(currentPrefix);

            foreach (var info in children)
            {
                var relative = currentPrefix.Length == 0 ? info.Name : currentPrefix + "/" + info.Name;

                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    stack.Push(((DirectoryInfo)info, relative));
                }
                else
                {
                    var file = (FileInfo)info;
                    if (!keep(file.FullName)) continue;
                    files.Add(new ManifestFile(relative, file.Length,
                        new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds()));

                    total += file.Length;
                    if (files.Count >= MaxManifestFiles) return;
                }
            }
        }
    }
}
