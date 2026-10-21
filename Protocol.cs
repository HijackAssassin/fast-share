using System.IO;
using System.Text.Json.Serialization;

namespace FastShare;

/// <summary>One entry in a directory listing.</summary>
public sealed record Entry(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("d")] bool IsDirectory,
    [property: JsonPropertyName("s")] long Size,
    [property: JsonPropertyName("m")] long ModifiedUnixMs,
    [property: JsonPropertyName("c")] long CreatedUnixMs,
    /// <summary>Directories only: whether it holds subfolders, so the tree can omit a
    /// disclosure arrow on folders that would expand to nothing.</summary>
    [property: JsonPropertyName("k")] bool HasChildren = false);

/// <summary>One file in a recursive manifest, relative to the requested subtree.</summary>
public sealed record ManifestFile(
    [property: JsonPropertyName("p")] string Path,
    [property: JsonPropertyName("s")] long Size,
    [property: JsonPropertyName("m")] long ModifiedUnixMs);

public sealed record Manifest(
    [property: JsonPropertyName("files")] List<ManifestFile> Files,
    [property: JsonPropertyName("bytes")] long TotalBytes,
    /// <summary>
    /// What kind of disk these files live on at the source ("HDD", "SSD", "NVMe"), so the
    /// downloader can log "SSD → HDD" and a slow small-file transfer can be blamed on the
    /// right end. Optional so a manifest from an older build simply carries no drive and the
    /// UI shows "?" for that end.
    /// </summary>
    [property: JsonPropertyName("drive")] string? SourceDrive = null,
    /// <summary>
    /// Folders with nothing visible inside them, relative like the files. Without this an
    /// empty folder simply never arrived - the manifest only ever listed files. Optional, so
    /// an older build's manifest carries none and nothing changes.
    /// </summary>
    [property: JsonPropertyName("dirs")] List<string>? EmptyDirs = null);

/// <summary>
/// Everything needed to reach a host, packed into the connection code.
///
/// MachineName is optional and defaults to null so codes issued by earlier builds still
/// parse. When present it is what the UI shows instead of a bare IP address.
/// </summary>
public sealed record ConnectionInfo(
    [property: JsonPropertyName("h")] string Host,
    [property: JsonPropertyName("p")] int Port,
    [property: JsonPropertyName("t")] string Token,
    [property: JsonPropertyName("f")] string Fingerprint,
    [property: JsonPropertyName("m")] string? MachineName = null)
{
    /// <summary>What to call this host in the UI.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(MachineName) ? Host : MachineName;
}

public sealed record BundleRequest(
    [property: JsonPropertyName("paths")] List<string> Paths);

/// <summary>Files to hash for a post-transfer check; the answer maps each path to its SHA-256 (or "").</summary>
public sealed record HashRequest(
    [property: JsonPropertyName("paths")] List<string> Paths);

public sealed record DeleteRequest(
    [property: JsonPropertyName("paths")] List<string> Paths);

public sealed record BeginUploadRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    /// <summary>
    /// Write into a temp file beside the destination and only replace the real one on
    /// /api/upload/commit. Older hosts ignore it (and older clients never send it), which is
    /// why the client only asks when the host advertises <see cref="Caps.UploadPart"/>.
    /// </summary>
    [property: JsonPropertyName("part")] bool Part = false);

/// <summary>Moves a finished upload's temp file into place and stamps its date.</summary>
public sealed record CommitUploadRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtime")] long ModifiedUnixMs);

/// <summary>Throws away an unfinished upload's temp file.</summary>
public sealed record AbortUploadRequest(
    [property: JsonPropertyName("path")] string Path);

/// <summary>
/// What a host can do beyond the original protocol, advertised on every ping. A client only
/// uses a feature the host lists, so a mixed pair of old and new builds keeps working in
/// both directions while one of them is out of date.
/// </summary>
public static class Caps
{
    /// <summary>Uploads land in a temp file and replace the destination only on commit.</summary>
    public const string UploadPart = "upload-part";

    /// <summary>Upload bundles in the v2 frame format: per-file dates and empty folders.</summary>
    public const string BundleV2 = "bundle-v2";

    /// <summary>Accepts dev-mode diagnostics on /api/diag.</summary>
    public const string Diagnostics = "diag";

    /// <summary>Serves the built-in speed test on /api/speed/* (and can ask a client to run it).</summary>
    public const string SpeedTest = "speedtest";

    /// <summary>Answers /api/hash (SHA-256 of files), for checking a transfer afterwards.</summary>
    public const string Hash = "hash";

    /// <summary>Understands /api/activity phase=queued|unqueued: "you are number 2 in their queue" (2.4.27).</summary>
    public const string Queue = "queue";

    /// <summary>Can be asked "may we resume the unfinished transfer?" on /api/resume/* (2.4.29).</summary>
    public const string Resume = "resume";

    /// <summary>Understands kind=space questions and /api/resume/withdraw (2.4.30).</summary>
    public const string SpaceAsk = "space-ask";

    /// <summary>
    /// /api/hash caps its own disk readers per drive (Verify.Gate, 2.4.37), so a client may keep
    /// two requests in flight without the host reading more files at once than its disk likes.
    /// </summary>
    public const string HashGate = "hash-gate";

    /// <summary>
    /// Understands /api/activity phase=prepare&amp;stage=... (2.4.40): what the other PC is doing before a
    /// transfer starts - choosing a folder, getting the file list ready, deciding about files that
    /// already exist, or finding there is not enough space - instead of a bare "queued".
    /// </summary>
    public const string Prepare = "prepare";
}

/// <summary>
/// Wire format for bundled small files, used in both directions.
///
/// Why not tar: tar pads every entry to a 512-byte boundary and spends another 512 on each
/// header. For 10,000 files averaging 2 KB that is roughly 25% pure overhead. This frame
/// costs 12 bytes plus the path, with no padding at all.
///
///   int32   path byte length (UTF-8), or -1 to end the stream
///   bytes   path, relative to the request's base directory
///   int64   file length
///   bytes   file contents
/// </summary>
public static class BundleFormat
{
    public const int EndOfStream = -1;
    public const int MaxPathBytes = 8 * 1024;

    /// <summary>v2 only: a file length of -1 marks an empty folder, with no payload.</summary>
    public const long Directory = -1;
}

/// <summary>Create an empty folder or file at <c>Path</c>.</summary>
public sealed record CreateRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("dir")] bool IsDirectory);

/// <summary>Rename one entry in place. <c>Name</c> is a bare name, never a path.</summary>
public sealed record RenameRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Copy or move entries into <c>Destination</c>, a folder.</summary>
public sealed record TransplantRequest(
    [property: JsonPropertyName("paths")] List<string> Paths,
    [property: JsonPropertyName("dest")] string Destination,
    [property: JsonPropertyName("move")] bool Move);

/// <summary>
/// Somewhere files can be read from.
///
/// The transfer engine does not care whether the bytes arrive over an ordinary HTTPS
/// connection we opened, or backwards down a tunnel someone else opened to us. Both look the
/// same from here, which is what lets the eight-stream downloader work unchanged in either
/// direction.
/// </summary>
public interface IFileSource
{
    Task<List<Entry>> ListAsync(string path, CancellationToken ct);
    Task<Manifest> ManifestAsync(string path, CancellationToken ct);
    /// <summary>
    /// A byte range of one file. <paramref name="fileSize"/> is the size the manifest listed;
    /// a source that can tell the file is now a different size refuses rather than handing
    /// back a mix of old and new bytes.
    /// </summary>
    Task<Stream> OpenRangeAsync(string path, long offset, long length, long fileSize, CancellationToken ct);
    Task<Stream> OpenBundleAsync(List<string> paths, CancellationToken ct);
}
