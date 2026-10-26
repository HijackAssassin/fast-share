using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastShare;

public sealed class AppConfig
{
    /// <summary>Folders offered when hosting. Each appears as a top-level folder to the far side.</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>Defaults to the port already proven working by iperf.</summary>
    public int Port { get; set; } = 5201;

    public string Token { get; set; } = "";
    public string? CertPassword { get; set; }

    /// <summary>
    /// Public address baked into connection codes. Detected afresh every time sharing starts,
    /// so a new IP from the ISP is picked up on its own rather than codes carrying a dead one
    /// forever. The last good value is kept if detection fails.
    /// </summary>
    public string? PublicHost { get; set; }

    /// <summary>Set by hand (e.g. a dynamic-DNS name) to use instead of the detected address.</summary>
    public string? PublicHostOverride { get; set; }

    /// <summary>
    /// The secret on the end of this PC's code: made on first launch, and again by "New code" on
    /// the Share tab. Null (installs from before 2.4.19 that never pressed New Code) means the
    /// plain ten-symbol code. Someone presenting a different one is refused before any
    /// request appears - unless their PC has been approved here before and proves it.
    /// </summary>
    public string? CodeSecret { get; set; }

    /// <summary>The FastShare.exe paths we have already asked Windows Firewall about, so a "no" sticks.</summary>
    public List<string> FirewallAsked { get; set; } = [];

    /// <summary>Per-folder limits set on the Share tab (Folder Permissions). Beat user permissions.</summary>
    public List<FolderRule> FolderRules { get; set; } = [];

    /// <summary>Hosts connected to before, listed on the Connections tab.</summary>
    public List<SavedConnection> Recent { get; set; } = [];

    /// <summary>Dark theme preference, reapplied at startup. Dark unless switched off.</summary>
    public bool DarkMode { get; set; } = true;

    /// <summary>
    /// How speeds are shown: "Mbit" (megabits per second, the unit internet plans are sold
    /// in - the default) or "MB" (megabytes per second, the unit file sizes use).
    /// </summary>
    public string SpeedUnit { get; set; } = "Mbit";

    /// <summary>
    /// After every transfer, compare SHA-256 of each file on both PCs. On by default (Patrick's
    /// call, 2026-09-29). Named CheckFiles, not the earlier VerifyTransfers, so configs that saved
    /// the old default of off pick up the new default instead.
    /// </summary>
    public bool CheckFiles { get; set; } = true;

    /// <summary>
    /// Whether sharing was on when the app last closed, so it comes back the same way. Set when
    /// sharing starts, cleared when the user stops it: leave sharing on and it is on next launch;
    /// turn it off and it stays off. Replaces the old "start sharing on startup" toggle - the app
    /// simply remembers the last state instead of asking for a separate preference.
    ///
    /// The port check still runs before the listener is restored: it briefly binds the share
    /// port, so starting the real listener underneath it would collide.
    /// </summary>
    public bool WasSharing { get; set; }

    /// <summary>
    /// How big the Browse list draws its rows, as an index into the zoom steps. Ctrl+scroll
    /// changes it; kept here so the size you settled on is the size you get next launch.
    /// </summary>
    public int ViewScale { get; set; } = 0;

    // ---- updates -----------------------------------------------------------
    //
    // Distribution is not through GitHub any more (the account was banned). Instead the
    // maintainer's own PC serves the latest build over the same forwarded port it shares
    // files on, and everyone else pulls it with the Update button in Settings.

    /// <summary>Where the Update button looks for a newer build. The maintainer's PC.</summary>
    public string UpdateHost { get; set; } = "136.56.230.166";

    /// <summary>Port the update lives behind. The same forwarded port sharing uses.</summary>
    public int UpdatePort { get; set; } = 5201;

    /// <summary>
    /// The maintainer's PeerId. Whenever this PC connects to a host with this identity, that
    /// host's current address and certificate become the update source - so a new IP from the
    /// ISP fixes itself the next time anyone reconnects, instead of stranding everyone on a
    /// build that points at a dead address.
    /// </summary>
    public const string MaintainerPeerId = "e3d1f15bab9b8ceb210e49d97d9c42b2";

    /// <summary>
    /// The maintainer PC's certificate (SHA-256 of cert.pfx, valid to 2036). Updates are only
    /// ever taken from a host presenting exactly this, and only a host presenting it can become
    /// the update source - so neither a new IP address nor someone claiming the maintainer's
    /// PeerId can push a build. On the maintainer's PC, cert.pfx must therefore be kept: a new
    /// one would mean handing everyone the next build by hand, once, with this value updated.
    /// </summary>
    public const string MaintainerFingerprint = "09e3255421fbf10e2aa7cc9bec0072b0beccc370ff8128682669e1c61029f84a";

    /// <summary>Where the maintainer's build is published. Only exists on that one PC.</summary>
    public const string DefaultReleasePath = @"F:\Patrick\Projects\Apps\Fast Share\Build";

    /// <summary>
    /// The folder whose contents this PC hands out as "the latest build". Only the
    /// maintainer's machine has this; everywhere else the path simply does not exist, which
    /// is what stops an ordinary copy from trying to serve updates.
    /// </summary>
    public string ReleasePath { get; set; } = DefaultReleasePath;

    /// <summary>
    /// Debug mode: a console window showing everything as it happens, plus a log written
    /// beside the exe. Persisted, so a session that reproduces a problem can be left on.
    /// </summary>
    public bool Debug { get; set; }

    /// <summary>This installation's stable identity. Block lists key on it, not on the address.</summary>
    public string PeerId { get; set; } = "";

    /// <summary>
    /// The private key that proves <see cref="PeerId"/> when connecting (base64 PKCS#8).
    /// Generated on first run of a build that has it; never sent anywhere.
    /// </summary>
    public string? IdentityKey { get; set; }

    /// <summary>Shown to peers. Defaults to the desktop name until the user sets one.</summary>
    public string? Username { get; set; }


    /// <summary>Permanently refused peers, managed in Settings.</summary>
    public List<BlockedPeer> Blocked { get; set; } = [];

    // ---- all-time counters, shown on the Statistics tab -------------------
    public long LifetimeDownloadedBytes { get; set; }
    public long LifetimeUploadedBytes { get; set; }
    public long LifetimeDownloadedFiles { get; set; }
    public long LifetimeUploadedFiles { get; set; }
    public long LifetimeLargestFileBytes { get; set; }
    public double LifetimePeakBytesPerSecond { get; set; }
    public int LifetimeRuns { get; set; }
    public DateTime? FirstRunUtc { get; set; }

    /// <summary>The biggest files ever moved, newest ranking kept to ten.</summary>
    public List<TransferRecord> BiggestTransfers { get; set; } = [];

    /// <summary>
    /// Every transfer batch ever, newest last, for the History tab. Batches rather than
    /// individual files - one line per thing you actually did, not per byte moved.
    /// </summary>
    public List<HistoryEntry> History { get; set; } = [];

    /// <summary>Transfers with someone that have not finished yet, offered back on reconnect.</summary>
    public List<PendingTransfer> PendingTransfers { get; set; } = [];

    /// <summary>Peers approved before, so the same answer can be offered again.</summary>
    public List<KnownPeer> Known { get; set; } = [];

    /// <summary>Peer ids the user has starred, listed under Favorites on the Connections tab.</summary>
    public List<string> Favorites { get; set; } = [];
    public string? LastDestination { get; set; }

    /// <summary>Where the upload picker last looked, so it reopens there.</summary>
    public string? LastUploadFrom { get; set; }

    /// <summary>
    /// Download folders we have already offered a Windows Defender exclusion for, so the
    /// PyCharm-style "speed this up?" prompt appears at most once per folder rather than on
    /// every download into it.
    /// </summary>
    public List<string> DefenderOffered { get; set; } = [];

    /// <summary>Set once the user picks "don't ask again"; suppresses the exclusion offer for good.</summary>
    public bool DefenderStopAsking { get; set; }

    /// <summary>
    /// When on AND FastShare is running as administrator, folders being transferred to (downloads)
    /// and shared from are excluded from Windows Defender automatically, with no prompt and no
    /// per-folder button. Off by default; only takes effect while elevated, so the normal
    /// non-elevated launch (which keeps Explorer drag-and-drop working) is unaffected.
    /// </summary>

    /// <summary>
    /// Keep every shared folder and every download destination excluded from Windows Defender:
    /// on startup, when a share is added (removed when it is un-shared), and whenever a download
    /// folder is picked. Each change is one UAC prompt, since FastShare itself stays un-elevated.
    /// </summary>
    public bool KeepDefenderExcluded { get; set; } = true;

    /// <summary>Folders FastShare has excluded (normalised). Also what "already excluded" means,
    /// since an un-elevated process cannot read Defender's own list.</summary>
    public List<string> DefenderExcluded { get; set; } = [];

    /// <summary>The subset of those that are download destinations; never un-excluded with a share.</summary>
    public List<string> DefenderDownloads { get; set; } = [];

    /// <summary>
    /// Closing the window (X) hides FastShare to the notification area instead of quitting, so
    /// it keeps sharing and pops up when something needs you. Quit from the tray icon's menu.
    /// Minimising is always ordinary minimising. Off by default: X quits.
    /// </summary>
    public bool CloseToTray { get; set; }

    /// <summary>
    /// Show "Playing Fast Share" on Discord while the Discord app is running: idle, sharing, or
    /// sending/receiving with the percent and speed - never file or people's names. On by default.
    /// </summary>
    public bool DiscordPresence { get; set; } = true;

    /// <summary>
    /// Dev mode: send this PC's log and live stats to the hosts it connects to, and keep what
    /// connected peers send (diag\&lt;peer&gt;.log). Both ends need it on. Off by default.
    /// </summary>
    public bool DevMode { get; set; }

    /// <summary>
    /// Where everything is kept. FASTSHARE_HOME overrides it, which is what lets a test
    /// harness run a real server and client without touching the real config - the harness
    /// used to save over it, wiping Recent connections more than once.
    /// </summary>
    [JsonIgnore]
    public static string Dir =>
        Environment.GetEnvironmentVariable("FASTSHARE_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastShare");

    [JsonIgnore] public static string FilePath => Path.Combine(Dir, "config.json");
    [JsonIgnore] public static string BackupPath => Path.Combine(Dir, "config.backup.json");
    [JsonIgnore] public static string CertPath => Path.Combine(Dir, "cert.pfx");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly Lock SaveLock = new();

    /// <summary>
    /// Reads the config, falling back to the backup if the main file is damaged.
    ///
    /// A damaged file used to be treated as "no config": the app started blank, generated a new
    /// identity and immediately saved over it - so one crash mid-write lost every saved
    /// connection, favourite, block and statistic, and broke everything the other side had
    /// remembered about this PC. Now a damaged file is set aside (never overwritten) and the
    /// previous good copy is used.
    /// </summary>
    public static AppConfig Load()
    {
        var main = TryRead(FilePath);
        if (main is not null) return Tidy(main);
        var existed = File.Exists(FilePath) || File.Exists(BackupPath);

        if (File.Exists(FilePath))
        {
            // Keep the evidence, and keep it out of the way: the next save would otherwise
            // rotate this broken copy into the backup slot, over the good one.
            try
            {
                File.Move(FilePath, Path.Combine(Dir, $"config.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
            }
            catch { }
        }

        var backup = TryRead(BackupPath);
        if (backup is not null) return Tidy(backup);

        // A brand-new install gets a code of its own. The code is otherwise just the public
        // address and port, so two PCs in one house would get the same one. Existing installs
        // keep theirs: changing it would lock out everyone who already has it.
        var fresh = new AppConfig();
        if (!existed) fresh.CodeSecret = ShortCode.NewSecret();
        return Tidy(fresh);
    }

    private static AppConfig? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Corrects settings that have gone stale since they were saved.</summary>
    private static AppConfig Tidy(AppConfig config)
    {
        // The project moved; a release folder that no longer exists is replaced by the current one.
        if (!Directory.Exists(config.ReleasePath) && Directory.Exists(DefaultReleasePath))
            config.ReleasePath = DefaultReleasePath;
        return config;
    }

    /// <summary>
    /// Writes the config safely: to a temp file first, flushed to disk, then swapped into
    /// place with the previous version kept as the backup. A crash or power cut at any
    /// moment leaves either the old file or the new one, never half of one.
    /// </summary>
    public void Save()
    {
        lock (SaveLock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                HistoryDetails.Persist(History);

                var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
                var temp = FilePath + ".tmp";

                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(FilePath)) File.Replace(temp, FilePath, BackupPath, ignoreMetadataErrors: true);
                else File.Move(temp, FilePath);
            }
            catch (Exception ex)
            {
                // Config is a convenience, never fail the app over it - but say so.
                Diagnostics.Log("config", "could not save config.json", ex);
            }
        }
    }

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    /// <summary>Packs host, port, token and fingerprint into one pasteable string.</summary>
    public static string BuildCode(ConnectionInfo info)
    {
        var json = JsonSerializer.Serialize(info);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "FS1-" + b64;
    }

    public static ConnectionInfo ParseCode(string code)
    {
        code = code.Trim();
        if (!code.StartsWith("FS1-", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("That is not a FastShare code — it should start with FS1-.");

        var b64 = code[4..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');

        return JsonSerializer.Deserialize<ConnectionInfo>(
                   Encoding.UTF8.GetString(Convert.FromBase64String(b64)))
               ?? throw new FormatException("Connection code is malformed.");
    }
}

/// <summary>
/// One completed transfer, as it appears in the History tab.
///
/// Stores the counts and the names, not the file list: keeping every path of every transfer
/// forever would turn config.json into a database, and the counts are what the history is
/// actually read for.
/// </summary>
public sealed class HistoryEntry
{
    public DateTime WhenUtc { get; set; }

    /// <summary>True when it left this PC.</summary>
    public bool Sent { get; set; }

    public string? Peer { get; set; }
    public long Files { get; set; }
    public long Folders { get; set; }
    public long Bytes { get; set; }
    public double Seconds { get; set; }

    /// <summary>What went wrong, when it did not finish cleanly.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// True when the OTHER side drove this transfer (they pulled from us, or pushed to us)
    /// rather than us. It changes the wording: our own reads "Uploaded"/"Downloaded", theirs
    /// reads "Downloaded From You"/"Uploaded to You".
    /// </summary>
    public bool PeerDriven { get; set; }

    /// <summary>
    /// The folders and files this transfer touched, so a History row can be opened to show
    /// them - the same list the Activity Log shows while it runs.
    ///
    /// Kept in a file of their own (history\&lt;DetailId&gt;.json) rather than in config.json,
    /// which they had grown past a megabyte - rewritten in full on every save. Read here only
    /// until they are written out; after that <see cref="HistoryDetails.Load"/> fetches them
    /// when a row is actually opened. Older configs still carry them inline and are moved out
    /// on the next save.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FolderNames { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FileNames { get; set; }

    /// <summary>Files that did not make it, each with why ("name: reason"). Capped at 200.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Failed { get; set; }

    /// <summary>Which history\ file holds this row's name lists, if any.</summary>
    public string? DetailId { get; set; }

    /// <summary>
    /// The kinds of disk this transfer moved between - "HDD", "SSD" or "NVMe" - source then
    /// destination. Shown in the Activity Log as "SSD → HDD" so a slow small-file transfer can
    /// be pinned on the right end. Null when an end could not be told: an older peer that does
    /// not report it, or a network share with no single drive behind it.
    /// </summary>
    public string? SourceDrive { get; set; }
    public string? DestDrive { get; set; }
}

/// <summary>The per-transfer name lists, stored beside config.json instead of inside it.</summary>
public static class HistoryDetails
{
    private sealed record Lists(List<string> Folders, List<string> Files);

    private static string Folder => Path.Combine(AppConfig.Dir, "history");
    private static string PathOf(string id) => Path.Combine(Folder, id + ".json");

    /// <summary>Writes out any lists still held in memory and drops them from the entry.</summary>
    public static void Persist(IEnumerable<HistoryEntry> history)
    {
        foreach (var entry in history)
        {
            if (entry.FileNames is null && entry.FolderNames is null) continue;

            try
            {
                Directory.CreateDirectory(Folder);
                entry.DetailId ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
                File.WriteAllText(PathOf(entry.DetailId),
                    JsonSerializer.Serialize(new Lists(entry.FolderNames ?? [], entry.FileNames ?? [])));
                entry.FileNames = null;
                entry.FolderNames = null;
            }
            catch { /* stays inline this time; tried again on the next save */ }
        }
    }

    /// <summary>The lists for one row, or empty ones if they were never kept or have been trimmed.</summary>
    public static (List<string> Folders, List<string> Files) Load(HistoryEntry entry)
    {
        if (entry.FileNames is not null || entry.FolderNames is not null)
            return (entry.FolderNames ?? [], entry.FileNames ?? []);

        try
        {
            if (entry.DetailId is { } id &&
                JsonSerializer.Deserialize<Lists>(File.ReadAllText(PathOf(id))) is { } lists)
            {
                return (lists.Folders, lists.Files);
            }
        }
        catch { }
        return ([], []);
    }

    public static void Delete(HistoryEntry entry)
    {
        entry.FileNames = null;
        entry.FolderNames = null;
        if (entry.DetailId is not { } id) return;
        try { File.Delete(PathOf(id)); } catch { }
        entry.DetailId = null;
    }
}

/// <summary>
/// One notable file transfer, kept for the all-time top ten.
///
/// Deliberately a flat record of a single file rather than a whole transfer: "the biggest
/// thing I ever moved" is a question about one file, and a batch of ten thousand small files
/// would otherwise crowd out every real answer.
/// </summary>
public sealed class TransferRecord
{
    public string FileName { get; set; } = "";
    public long Bytes { get; set; }

    /// <summary>True when it left this PC - including someone downloading it from us.</summary>
    public bool Sent { get; set; }

    /// <summary>Who it went to or came from, as known at the time.</summary>
    public string? Peer { get; set; }

    public DateTime WhenUtc { get; set; }
}

/// <summary>
/// A host we have connected to before. Kept so the Connections tab can list them, show
/// whether each is currently sharing, and reconnect without pasting a code again.
/// </summary>
public sealed class SavedConnection
{
    public string Code { get; set; } = "";

    /// <summary>Name the user typed for this host, if they renamed it.</summary>
    public string? Name { get; set; }

    /// <summary>Desktop name reported by the host, used when the user has not renamed it.</summary>
    public string? MachineName { get; set; }

    public string Host { get; set; } = "";
    public int Port { get; set; }
    public DateTime LastConnectedUtc { get; set; }

    /// <summary>Certificate learned on first contact. Verified on every connection after.</summary>
    public string? PinnedFingerprint { get; set; }

    /// <summary>Their stable identity, when we have learned it from a session.</summary>
    public string? PeerId { get; set; }

    /// <summary>
    /// True for someone who reached US and cannot be dialled back - their port is shut, which
    /// is the whole reason they connect inward. Shown differently so a red "offline" light is
    /// not mistaken for something being broken.
    /// </summary>
    public bool InboundOnly { get; set; }

    /// <summary>Renamed name first, then the host's desktop name, then the raw address.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name!
        : !string.IsNullOrWhiteSpace(MachineName) ? MachineName!
        : Host;

    /// <summary>Identity is the address, not the code - a host reissues codes on restart.</summary>
    /// <summary>What the PC runs on ("Windows 11"), as it last told us. Shown instead of its address.</summary>
    public string? Os { get; set; }

    public bool SameHostAs(SavedConnection other) =>
        Port == other.Port && Host.Equals(other.Host, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The short, typeable form of a connection code: <c>name@host</c>, with an optional
/// <c>:port</c> when it is not the default.
///
/// Deliberately guessable. Everything that used to be carried secretly inside a long code -
/// the access token and the certificate fingerprint - now comes from the session handshake
/// and trust-on-first-use instead, which is what lets the code be short enough to read aloud.
/// </summary>
/// <summary>
/// The connection code: ten characters, no visible address.
///
/// A code has to carry the address somehow - there is no directory server to look one up in,
/// so ten purely random characters could not tell anyone where to connect. Instead the four
/// bytes of the IPv4 address and the two of the port are packed into 48 bits, which is
/// exactly ten base-32 characters. The result looks random and reveals nothing at a glance,
/// but it is complete: paste it and the app knows where to go.
///
/// The alphabet is Crockford's - no I, L, O or U - so a code read aloud or copied by hand
/// cannot turn a 1 into an I or a 0 into an O.
/// </summary>
public static class ShortCode
{
    public const int DefaultPort = 5201;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Symbols in the optional secret on the end of a code (20 bits).</summary>
    public const int SecretLength = 4;

    /// <summary>A fresh secret for "New code" - what makes an old code stop working.</summary>
    public static string NewSecret()
    {
        var symbols = new char[SecretLength];
        for (var i = 0; i < symbols.Length; i++) symbols[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(symbols);
    }

    /// <summary>
    /// The code for an address, with the secret on the end when there is one:
    /// "H0WED9GMA4-K3TZ". The ten-symbol part is still just the address, so a code keeps
    /// working through any number of secret changes as far as reaching the PC goes; the
    /// secret is what the host checks before it will even show a request.
    /// </summary>
    public static string Build(string host, int port, string? secret)
    {
        var code = Build(host, port);
        if (string.IsNullOrEmpty(secret)) return code;
        return code.Length == 10 ? $"{code}-{secret}" : $"{code}#{secret}";
    }

    public static string Build(string host, int port)
    {
        // A hostname (dynamic DNS, say) has no fixed byte form, so fall back to plain text.
        if (!System.Net.IPAddress.TryParse(host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return port == DefaultPort ? host : $"{host}:{port}";
        }

        ulong packed = 0;
        foreach (var b in address.GetAddressBytes()) packed = (packed << 8) | b;
        packed = (packed << 16) | (ushort)port;   // 48 bits of address + port
        packed <<= 2;                             // widened to the 50 bits ten symbols hold

        var symbols = new char[10];
        for (var i = 9; i >= 0; i--)
        {
            symbols[i] = Alphabet[(int)(packed & 31)];
            packed >>= 5;
        }

        return new string(symbols);
    }

    public static bool TryParse(string text, out string host, out int port) =>
        TryParse(text, out host, out port, out _);

    /// <summary>Also returns the code's secret, or null for a code without one (or an address).</summary>
    public static bool TryParse(string text, out string host, out int port, out string? secret)
    {
        host = "";
        port = DefaultPort;
        secret = null;

        text = text.Trim();
        if (text.Length == 0) return false;

        // A hostname code carries its secret after '#'.
        var hash = text.LastIndexOf('#');
        if (hash > 0)
        {
            var tail = Normalise(text[(hash + 1)..]);
            if (tail.Length == SecretLength && tail.All(c => Alphabet.Contains(c))) secret = tail;
            text = text[..hash];
        }

        // Ten symbols of address, then (from 2.4.11) four of secret.
        var cleaned = Normalise(text);
        if (cleaned.Length == 10 + SecretLength && cleaned.All(c => Alphabet.Contains(c)) &&
            TryDecode(cleaned[..10], out host, out port))
        {
            secret = cleaned[10..];
            return true;
        }

        // Older builds issued "name@address"; keep accepting the address half.
        var at = text.LastIndexOf('@');
        if (at >= 0) text = text[(at + 1)..].Trim();
        if (text.Length == 0) return false;

        if (TryDecode(text, out host, out port)) return true;
        if (TryLiteral(text, out host, out port)) return true;

        // A line pasted straight out of ipconfig ("IPv4 Address. . . : 192.168.1.100") or with
        // Windows' "(Preferred)" still attached. Only a whole dotted address is lifted out, so
        // stray text never turns into a hostname.
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?::(\d{1,5}))?(?![\d.])");
        return match.Success && TryLiteral(match.Value, out host, out port);
    }

    private static bool TryLiteral(string text, out string host, out int port)
    {
        host = "";
        port = DefaultPort;

        // A literal address, with an optional :port.
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
        {
            if (!int.TryParse(text[(colon + 1)..], out port) || port is < 1 or > 65535) return false;
            text = text[..colon];
        }

        // Only accept something that could actually be an address. Without this, any typed
        // rubbish became a "hostname" and surfaced later as a confusing DNS failure instead
        // of an honest "that is not a code".
        if (!System.Net.IPAddress.TryParse(text, out _) && !LooksLikeHostname(text)) return false;

        host = text;
        return host.Length > 0;
    }

    private static bool LooksLikeHostname(string text)
    {
        if (text.Length is 0 or > 253) return false;
        if (text.StartsWith('.') || text.EndsWith('.') ||
            text.StartsWith('-') || text.EndsWith('-')) return false;

        var sawAlphanumeric = false;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) sawAlphanumeric = true;
            else if (c is not ('.' or '-')) return false;
        }

        return sawAlphanumeric;
    }

    /// <summary>Upper-case, separators removed, and the letters people confuse mapped to what they mean.</summary>
    private static string Normalise(string text) =>
        new(text.Replace("-", "").Replace(" ", "").ToUpperInvariant()
            .Select(c => c switch { 'I' or 'L' => '1', 'O' => '0', 'U' => 'V', _ => c }).ToArray());

    /// <summary>
    /// The address part of a code - the first ten symbols, normalised - or null if the text is
    /// not a code (a typed address, say). Used to find the PC on the local network.
    /// </summary>
    public static string? AddressPart(string text)
    {
        var hash = text.LastIndexOf('#');
        if (hash > 0) text = text[..hash];
        var at = text.LastIndexOf('@');
        if (at >= 0) text = text[(at + 1)..];
        var cleaned = Normalise(text.Trim());
        if (cleaned.Length is not (10 or 10 + SecretLength)) return null;
        var address = cleaned[..10];
        return address.All(c => Alphabet.Contains(c)) && TryDecode(address, out _, out _) ? address : null;
    }

    private static bool TryDecode(string text, out string host, out int port)
    {
        host = "";
        port = DefaultPort;

        var cleaned = text.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (cleaned.Length != 10) return false;

        ulong packed = 0;
        foreach (var symbol in cleaned)
        {
            // Accept the letters Crockford excludes, mapped to the digits they resemble.
            var mapped = symbol switch { 'I' or 'L' => '1', 'O' => '0', 'U' => 'V', _ => symbol };
            var index = Alphabet.IndexOf(mapped);
            if (index < 0) return false;
            packed = (packed << 5) | (uint)index;
        }

        packed >>= 2;
        port = (int)(packed & 0xFFFF);
        var address = (uint)(packed >> 16);
        if (port is < 1 or > 65535) return false;

        host = $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";
        return true;
    }

    /// <summary>
    /// True for an address that only means something inside one home or office network
    /// (192.168.x.x, 10.x.x.x, 172.16-31.x.x, and 169.254.x.x). Connecting to one of these goes
    /// straight across the local network - no router port forward, no trip out to the internet.
    /// </summary>
    public static bool IsPrivate(string host)
    {
        if (!System.Net.IPAddress.TryParse(host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        var b = address.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254);
    }
}


/// <summary>
/// A transfer with someone, written down as it starts and crossed off when it ends, so one that
/// never finished (PC off, connection gone, FastShare closed) can be offered back.
/// </summary>
public sealed class PendingTransfer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? PeerId { get; set; }
    public string PeerName { get; set; } = "";
    public bool Download { get; set; }

    /// <summary>Downloads: the local folder they went into. Uploads: the folder on their PC.</summary>
    public string Where { get; set; } = "";

    /// <summary>"\"Blender\"", "3 items".</summary>
    public string Description { get; set; } = "";

    /// <summary>Downloads: what was selected on their side.</summary>
    public List<PendingItem>? Rows { get; set; }

    /// <summary>Uploads: the files and folders picked on this PC.</summary>
    public List<string>? Paths { get; set; }

    public long TotalBytes { get; set; }
    public long DoneBytes { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed record PendingItem(string Name, string RemotePath, bool IsDirectory, long Size, DateTime Modified);
