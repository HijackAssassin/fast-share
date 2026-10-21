using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace FastShare;

/// <summary>
/// The second place updates come from: the published download on Patrick's website.
///
/// Order: the update server on Patrick's PC first (so a build can be handed to friends before
/// it goes public), and only when nobody answers there, the website. The site's front page and
/// /downloads/ both link the current file, named like "Fast-Share-V2.4.12.rar" (the publish
/// script keeps the link current), so the newest such link is the release. Its version comes from
/// the name; after unpacking, FastShare.exe's own version must agree before anything is installed.
///
/// Trust: this path is not pinned to Patrick's certificate the way the peer path is. It relies on
/// ordinary HTTPS to the site - whoever controls that address controls these builds. A plain
/// http:// site (allowed, for an IP without a certificate) could be tampered with on the way.
/// </summary>
public static class WebUpdate
{
    /// <summary>
    /// The website, as built into this exe from UpdateWebsite in FastShare.csproj (so a new
    /// address is a one-line change there, or an argument to build.bat). Always ends in "/".
    /// </summary>
    public static string Site { get; } = ReadSite("UpdateWebsite") ?? "https://teamwolfgamers.online/";

    /// <summary>Tried when <see cref="Site"/> is down or offers nothing (UpdateWebsiteFallback).</summary>
    public static string? Fallback { get; } = ReadSite("UpdateWebsiteFallback");

    /// <summary>The site the last successful check found its build on, for the "from …" line.</summary>
    public static string Used { get; private set; } = Site;

    private static string? ReadSite(string key)
    {
        var built = typeof(WebUpdate).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
        if (string.IsNullOrWhiteSpace(built)) return null;
        var site = built.Trim();
        if (!site.Contains("://", StringComparison.Ordinal)) site = "https://" + site;
        return site.EndsWith('/') ? site : site + "/";
    }

    /// <summary>Fast-Share / FastShare / File-Share / FileShare, then -V1.2.3, as .rar or .zip.</summary>
    private static readonly Regex Link = new(
        @"href=""(?<url>[^""]*?(?<file>(?:fast|file)[-_ ]?share[-_ ]?v?(?<ver>\d+(?:\.\d+){1,3})\.(?:rar|zip)))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record Build(string Version, Uri Url, string FileName, long Bytes);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// The newest FastShare build the website links to - the main site first, then the
    /// fallback if the main one is unreachable or offers nothing - or null if neither does.
    /// </summary>
    public static async Task<Build?> CheckAsync(CancellationToken ct = default)
    {
        foreach (var site in new[] { Site, Fallback }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var build = await CheckSiteAsync(site, ct);
            if (build is null) continue;
            Used = site;
            return build;
        }
        return null;
    }

    private static async Task<Build?> CheckSiteAsync(string site, CancellationToken ct)
    {
        Build? best = null;
        foreach (var page in new[] { site, site + "downloads/" })
        {
            string html;
            try { html = await Http.GetStringAsync(page, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostics.Log("update", $"website: could not read {page}", ex);
                continue;
            }

            foreach (Match match in Link.Matches(html))
            {
                if (!System.Version.TryParse(match.Groups["ver"].Value, out var version)) continue;
                if (best is not null && System.Version.Parse(best.Version) >= version) continue;
                // Downloads come from the same kind of address as the site: https, unless the site
                // itself was set to a plain http address (an IP without a certificate).
                if (!Uri.TryCreate(new Uri(page), match.Groups["url"].Value, out var url) ||
                    (url.Scheme != Uri.UriSchemeHttps && url.Scheme != new Uri(site).Scheme)) continue;

                best = new Build(Trim(version), url, match.Groups["file"].Value, 0);
            }
        }

        if (best is null) return null;

        // The size, for the "the download is 70 MB" line; not worth failing over.
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, best.Url);
            using var response = await Http.SendAsync(head, ct);
            best = best with { Bytes = response.Content.Headers.ContentLength ?? 0 };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        Diagnostics.Log("update", $"website offers {best.FileName} (v{best.Version})");
        return best;
    }

    /// <summary>
    /// Downloads the archive, unpacks it (rar or zip), checks FastShare.exe inside is the version
    /// the name promised and newer than this one, and returns a zip that
    /// <see cref="Updater.Apply"/> installs exactly as it installs a build from the update server.
    /// </summary>
    public static async Task<string> DownloadAsync(Build build, Action<long, long> onProgress, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "FastShareUpdate", "web");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var archive = Path.Combine(work, build.FileName);
        using (var download = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        using (var response = await download.GetAsync(build.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? build.Bytes;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                onProgress(received, total);
            }
            if (total > 0 && received != total)
                throw new IOException($"Download ended early — got {received:N0} of {total:N0} bytes.");
        }

        var unpacked = Path.Combine(work, "unpacked");
        Directory.CreateDirectory(unpacked);
        await Task.Run(() => Unpack(archive, unpacked), ct);

        var exe = Directory.EnumerateFiles(unpacked, Updater.MainExe, SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException($"{build.FileName} does not contain {Updater.MainExe}.");

        var inside = Trim(System.Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var v) ? v : new Version(0, 0));
        if (!string.Equals(inside, build.Version, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{build.FileName} is named v{build.Version} but contains FastShare v{inside}. Not installed.");
        if (!Updater.IsNewer(inside))
            throw new InvalidOperationException($"{build.FileName} is not newer than this copy. Not installed.");

        // Handed over as the same kind of zip the update server sends.
        var zip = Path.Combine(work, "package.zip");
        ZipFile.CreateFromDirectory(Path.GetDirectoryName(exe)!, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        return zip;
    }

    private static void Unpack(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            // Every name is kept inside the folder: an archive naming "..\..\x" writes nothing.
            var target = Path.GetFullPath(Path.Combine(destination, entry.Key ?? ""));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.OpenEntryStream();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    private static string Trim(Version version) => version.ToString(Math.Min(3, Math.Max(2, version.Build >= 0 ? 3 : 2)));
}
