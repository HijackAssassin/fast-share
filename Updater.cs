using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastShare;

/// <summary>
/// In-app updates, peer to peer.
///
/// There is no GitHub any more, so a build is not fetched from a release page - it is pulled
/// straight off the maintainer's PC, over the very same forwarded port and TLS certificate
/// that sharing uses. The maintainer keeps FastShare open; everyone else presses Update in
/// Settings, and their copy downloads whatever is sitting in the maintainer's release folder
/// and swaps itself over with it.
///
/// The awkward part of updating a running Windows program is that its own files are locked.
/// The trick, unchanged from the old GitHub updater, is that Windows lets you *rename* a file
/// that is open - the handle follows the data, not the directory entry - so the live
/// FastShare.exe is moved aside to a .fsold name, the new one is dropped into the space it
/// left, and the app relaunches. Everything is staged in temp and checked before a single
/// file in the install folder is touched, and every rename is recorded so a failure halfway
/// through puts it all back.
/// </summary>
public static class Updater
{
    /// <summary>Suffix given to displaced files. Swept up on the next launch.</summary>
    private const string OldSuffix = ".fsold";

    /// <summary>The one file that has to be present for a download to count as a real build.</summary>
    public const string MainExe = "FastShare.exe";

    /// <summary>
    /// Held open by FastShareUpdateServer.exe for as long as its window is. FastShare only hands
    /// out updates while it exists, so updates are on offer exactly while that window is open.
    /// </summary>
    public const string ServingMutexName = @"Local\FastShare.UpdateServer";

    /// <summary>Held by FastShare while it is sharing, so the update server knows to leave the port to it.</summary>
    public const string SharingMutexName = @"Local\FastShare.Sharing";

    /// <summary>Whether some process is holding one of the markers above.</summary>
    public static bool IsMarked(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>True while the update server is open, i.e. while updates should be offered.</summary>
    public static bool UpdatesOffered => IsMarked(ServingMutexName);

    public static Version Current { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Folder the running program lives in - what an update replaces.</summary>
    public static string InstallDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    // =======================================================================
    // Serving (the maintainer's side)
    // =======================================================================

    /// <summary>
    /// What the host advertises about the build it is offering.
    ///
    /// The property names are pinned to PascalCase with <see cref="JsonPropertyNameAttribute"/>.
    /// ASP.NET's <c>Results.Json</c> serialises camelCase by default ("version"), but every
    /// shipped client deserialises case-sensitively expecting "Version" - so without this the
    /// version came back null and every copy believed it was already current and refused to
    /// update. Pinning the names to what those clients read is what lets an old build see a
    /// new one at all.
    /// </summary>
    public sealed record BuildInfo(
        [property: JsonPropertyName("Version")] string Version,
        [property: JsonPropertyName("Bytes")] long Bytes);

    /// <summary>
    /// The build sitting in the release folder, or null if there is none - which is the case
    /// on every machine except the one that actually publishes releases, since the path does
    /// not exist there. That absence is what stops an ordinary copy offering updates.
    /// </summary>
    public static BuildInfo? LocalBuild(string releasePath)
    {
        try
        {
            var exe = Path.Combine(releasePath, MainExe);
            if (!File.Exists(exe)) return null;

            var version = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "0.0.0";

            long bytes = 0;
            foreach (var file in PackagedFiles(releasePath))
            {
                try { bytes += new FileInfo(file).Length; } catch { }
            }

            return new BuildInfo(Trim(version), bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Version strings come out as 1.8.0.0; the app talks in 1.8.0.</summary>
    private static string Trim(string version) =>
        Version.TryParse(version, out var v) ? v.ToString(3) : version;

    private static readonly Lock PackageGate = new();

    /// <summary>
    /// What an update carries: FastShare.exe, the native DLLs beside it, and the guide - from the
    /// top of the release folder only. The whole folder used to be zipped, so whatever else lived
    /// there (packaging scripts, the .rar they make, a shortcut, a 100 MB copy of the update
    /// server in a subfolder) went out to every PC that updated and landed in its install folder.
    /// </summary>
    public static IEnumerable<string> PackagedFiles(string releasePath) =>
        Directory.EnumerateFiles(releasePath, "*", SearchOption.TopDirectoryOnly).Where(file =>
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(file);
            return name.Equals(MainExe, StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("GUIDE.md", StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// A zip of the whole release folder, built on demand and cached until the folder changes.
    /// Rebuilding a 77 MB archive on every request would be wasteful, and the folder only
    /// changes when a new build is published.
    /// </summary>
    public static string BuildPackage(string releasePath)
    {
        lock (PackageGate)
        {
            var cache = Path.Combine(Path.GetTempPath(), "FastShareServe");
            Directory.CreateDirectory(cache);

            // Key by the folder AND its newest file's exact time. Seconds-resolution alone
            // would let two different folders touched in the same second share one cached zip,
            // and a path-blind key would serve one folder's build for another's.
            var identity = $"{Path.GetFullPath(releasePath).ToLowerInvariant()}|{LatestWrite(releasePath).Ticks}";
            var tag = Convert.ToHexStringLower(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..16];

            var zip = Path.Combine(cache, $"release-{tag}.zip");
            if (File.Exists(zip)) return zip;

            // A newer build makes older cached zips dead weight.
            foreach (var old in Directory.EnumerateFiles(cache, "release-*.zip"))
            {
                try { File.Delete(old); } catch { }
            }

            var temp = zip + ".building";
            if (File.Exists(temp)) File.Delete(temp);
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var file in PackagedFiles(releasePath))
                    archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Fastest);
            }
            File.Move(temp, zip);
            return zip;
        }
    }

    private static DateTime LatestWrite(string directory)
    {
        var latest = DateTime.MinValue;
        foreach (var file in PackagedFiles(directory))
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written > latest) latest = written;
            }
            catch { }
        }
        return latest;
    }

    // =======================================================================
    // Fetching (everyone else's side)
    // =======================================================================

    private static HttpClient Client(string fingerprint)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "fastshare",
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    if (string.IsNullOrEmpty(fingerprint)) return true;   // trust on first use
                    var presented = Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData()));
                    return presented.Equals(fingerprint, StringComparison.OrdinalIgnoreCase);
                },
            },
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Asks the maintainer's PC what build it is offering. Returns null when it cannot be
    /// reached or has nothing to offer - never throws, because "no update available" and
    /// "could not check" should both just leave the button quiet.
    /// </summary>
    public static async Task<BuildInfo?> CheckAsync(string host, int port, string fingerprint,
        CancellationToken ct = default)
    {
        try
        {
            using var http = Client(fingerprint);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var json = await http.GetStringAsync($"https://{host}:{port}/api/update/info", timeout.Token);

            // Case-insensitive so a future casing change on either side can never resurrect the
            // "reads null, thinks it is current" bug.
            return JsonSerializer.Deserialize<BuildInfo>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True if <paramref name="offered"/> is a higher version than this build.</summary>
    public static bool IsNewer(string offered) =>
        Version.TryParse(offered, out var v) && v > Current;

    /// <summary>
    /// Downloads the offered build to a temp folder, reporting bytes as they arrive. Returns
    /// the path to the downloaded zip.
    /// </summary>
    public static async Task<string> DownloadAsync(string host, int port, string fingerprint,
        Action<long, long> onProgress, CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetTempPath(), "FastShareUpdate");
        Directory.CreateDirectory(staging);
        var target = Path.Combine(staging, "update.zip");

        using var http = Client(fingerprint);
        using var response = await http.GetAsync($"https://{host}:{port}/api/update/package",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write,
                         FileShare.None, 1 << 20, useAsync: true))
        {
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

        return target;
    }

    /// <summary>
    /// Unpacks the zip, swaps it over the running install and relaunches. Only returns if
    /// something stopped it; on success the new process is already starting and the caller
    /// should shut this one down.
    /// </summary>
    public static void Apply(string zipPath)
    {
        var install = InstallDir;
        Swap(zipPath, install);

        Process.Start(new ProcessStartInfo(Path.Combine(install, MainExe))
        {
            UseShellExecute = true,
            WorkingDirectory = install,
            Arguments = $"--updated {Environment.ProcessId}",
        });
    }

    /// <summary>
    /// Unpack, then move the new files over the old ones. Split from <see cref="Apply"/> so it
    /// can be tested against a throwaway folder without relaunching anything.
    /// </summary>
    public static void Swap(string zipPath, string install)
    {
        var extracted = Path.Combine(Path.GetDirectoryName(zipPath)!, "unpacked");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extracted);

        var source = FindPayload(extracted)
            ?? throw new InvalidOperationException(
                $"That download does not contain {MainExe} — it may not be a FastShare build.");

        RequireWritable(install);

        var moved = new List<(string From, string To)>();
        var stamp = DateTime.UtcNow.Ticks.ToString();

        try
        {
            foreach (var incoming in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, incoming);
                var destination = Path.Combine(install, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // The running exe and its loaded DLLs cannot be deleted or written over, but
                // they can be renamed - which is the whole basis of this working.
                if (File.Exists(destination))
                {
                    var parked = destination + OldSuffix + stamp;
                    File.Move(destination, parked);
                    moved.Add((destination, parked));
                }

                File.Copy(incoming, destination, overwrite: true);
            }
        }
        catch
        {
            Rollback(moved);
            throw;
        }
    }

    private static void Rollback(List<(string From, string To)> moved)
    {
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            var (original, parked) = moved[i];
            try
            {
                if (File.Exists(original)) File.Delete(original);
                File.Move(parked, original);
            }
            catch { }
        }
    }

    private static string? FindPayload(string root)
    {
        if (File.Exists(Path.Combine(root, MainExe))) return root;
        var found = Directory.EnumerateFiles(root, MainExe, SearchOption.AllDirectories).FirstOrDefault();
        return found is null ? null : Path.GetDirectoryName(found);
    }

    private static void RequireWritable(string directory)
    {
        var probe = Path.Combine(directory, ".fswrite" + Environment.ProcessId);
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch
        {
            throw new UnauthorizedAccessException(
                $"FastShare cannot write to its own folder:\n{directory}\n\n" +
                "Move the FastShare folder somewhere like your Desktop or Documents and try again.");
        }
    }

    // =======================================================================
    // Housekeeping at startup
    // =======================================================================

    /// <summary>
    /// Waits for the process an update replaced to exit, so the new one does not race it for
    /// the share port. Bounded - if that process hangs, carrying on beats never starting.
    /// </summary>
    public static void WaitForPrevious(int processId)
    {
        try
        {
            // Long enough for the old copy to say goodbye to everyone connected (a few seconds
            // at most) before this one takes the share port.
            using var previous = Process.GetProcessById(processId);
            previous.WaitForExit(30_000);
        }
        catch
        {
            // Already gone, which is the normal case by the time we get here.
        }
    }

    /// <summary>Deletes the files the last update displaced, now that the old process is gone.</summary>
    public static void CleanStaleFiles() => CleanStaleFiles(InstallDir);

    public static void CleanStaleFiles(string install)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(install, "*" + OldSuffix + "*",
                         SearchOption.AllDirectories))
            {
                try { File.Delete(stale); } catch { }
            }
        }
        catch { }

        try
        {
            var staging = Path.Combine(Path.GetTempPath(), "FastShareUpdate");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch { }
    }
}
