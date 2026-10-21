using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FastShare.Tests;

static class FormatTests
{
    public static Task Run()
    {
        Format.Megabytes = false;
        Check.That(Format.Rate(70 * 1048576) == "587 Mbit/s", $"megabits by default ({Format.Rate(70 * 1048576)})");
        Check.That(Format.Rate(0) == "0 Mbit/s", "idle reads 0 Mbit/s, never blank");
        Format.Megabytes = true;
        Check.That(Format.Rate(70 * 1048576) == "70 MB/s", $"megabytes when chosen ({Format.Rate(70 * 1048576)})");
        Format.Megabytes = false;
        Check.That(Format.Duration(3725) == "1h 02m" && Format.Duration(260) == "4m 20s" && Format.Duration(34.2) == "35s", "durations");
        return Task.CompletedTask;
    }
}

static class TransferEndTests
{
    public static Task Run()
    {
        Check.That(TransferWording.Outcome(null) == "Completed" &&
                   TransferWording.Outcome("Stopped at 5 GB of 9 GB - Connection closed: lost connection") == "Interrupted" &&
                   TransferWording.Outcome("Stopped at 5 GB of 9 GB - Cancelled by Dillon") == "Cancelled" &&
                   TransferWording.Outcome("Stopped at 5 GB of 9 GB - Stopped early on Dillon's side") == "Failed" &&
                   TransferWording.Outcome("3 failed - double-click for why") == "Some failed" &&
                   TransferWording.Outcome("Paused - FastShare was closed; resume next time you connect") == "Paused" &&
                   TransferWording.Outcome("Discarded - not enough free space") == "Discarded" &&
                   TransferWording.Outcome("Not enough free space") == "Failed",
            "finished rows get the right outcome word");

        // Their file check after the last byte keeps the announced transfer open and marked as checking.
        var peer = "test-end-" + Guid.NewGuid().ToString("N")[..8];
        TrafficMonitor.Announce(peer, "Tester", TransferDirection.Download, 100, 1);
        TrafficMonitor.Add(peer, "Tester", TransferDirection.Download, "a/file.bin", 100, 100);
        FlowSnapshot? Mine() => TrafficMonitor.Active().FirstOrDefault(f => f.Key.EndsWith(peer));
        Check.That(Mine() is { Checking: false, Bytes: 100 }, "all bytes in, not checking yet");
        using (TrafficMonitor.BeginCheck(peer))
        {
            TrafficMonitor.Sweep(TimeSpan.Zero);
            Check.That(Mine() is { Checking: true }, "a check in progress keeps the transfer open and shows Checking");
        }
        // A "getting ready" message that arrives after the start must not put it back to waiting.
        TrafficMonitor.Prepare(peer, "Tester", TransferDirection.Download, "listing");
        Check.That(!TrafficMonitor.Queued().Exists(q => q.Key.EndsWith(peer)), "a late preparing message is ignored once it runs");
        TrafficMonitor.Complete(peer, TransferDirection.Download);
        var closed = TrafficMonitor.Sweep(TimeSpan.Zero).FirstOrDefault(f => f.Key.EndsWith(peer));
        Check.That(closed is { Stopped: null }, "their finished message closes it as completed");
        return Task.CompletedTask;
    }
}

static class CodeTests
{
    public static Task Run()
    {
        var plain = ShortCode.Build("136.56.230.166", 5201);
        var secret = ShortCode.NewSecret();
        var full = ShortCode.Build("136.56.230.166", 5201, secret);
        Check.That(ShortCode.TryParse(full, out var h, out var p, out var s) && h == "136.56.230.166" && p == 5201 && s == secret, "code with secret round-trips");
        Check.That(ShortCode.TryParse(full.ToLowerInvariant().Replace("-", " "), out _, out _, out var s2) && s2 == secret, "lower case and spaces tolerated");
        Check.That(ShortCode.TryParse(plain, out _, out _, out var s3) && s3 is null, "ten-symbol code still parses");
        Check.That(ShortCode.AddressPart(full) == plain && ShortCode.AddressPart("192.168.1.100") is null, "address part");
        Check.That(ShortCode.TryParse("   IPv4 Address. . . . . . . : 192.168.1.100", out var ih, out _) && ih == "192.168.1.100", "ipconfig line");
        Check.That(ShortCode.TryParse("192.168.1.100:5311", out var lh, out var lp) && lh == "192.168.1.100" && lp == 5311, "address with port");
        Check.That(ShortCode.IsPrivate("192.168.1.5") && ShortCode.IsPrivate("10.0.0.2") && !ShortCode.IsPrivate("136.56.230.166"), "private ranges");

        // A new install gets a code of its own; an existing one without a secret keeps its code.
        var home = Environment.GetEnvironmentVariable("FASTSHARE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("FASTSHARE_HOME", Data.NewDir("fresh-config"));
            var fresh = AppConfig.Load();
            fresh.Save();
            var existing = AppConfig.Load();
            existing.CodeSecret = null;
            existing.Save();
            Check.That(fresh.CodeSecret is { Length: > 0 } && AppConfig.Load().CodeSecret is null,
                "first launch makes a code of its own; an existing plain code is kept");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FASTSHARE_HOME", home);
        }
        return Task.CompletedTask;
    }
}

static class LanListTests
{
    public static async Task Run()
    {
        var code = ShortCode.Build("136.56.230.166", 5398, ShortCode.NewSecret());
        string? current = code;
        using var responder = LanDiscovery.Respond(5398, () => current, () => new LanDiscovery.Self("peer-a", "Tester", "Windows 11"));
        var found = await LanDiscovery.ListAsync([5398], "peer-b", TimeSpan.FromMilliseconds(700));
        Check.That(found.Count == 1 && found[0].Code == code && found[0].Name == "Tester" && found[0].Port == 5398 &&
                   found[0].Sharing && found[0].Os == "Windows 11",
            $"a sharing PC on this network is listed with its code, name and system ({found.Count} found)");
        var self = await LanDiscovery.ListAsync([5398], "peer-a", TimeSpan.FromMilliseconds(500));
        Check.That(self.Count == 0, "our own copy is not listed");

        current = null;
        var idle = await LanDiscovery.ListAsync([5398], "peer-b", TimeSpan.FromMilliseconds(700));
        Check.That(idle.Count == 1 && !idle[0].Sharing && idle[0].Code == "",
            "a PC with FastShare open but not sharing is listed as not sharing, without its code");
    }
}

static class SessionTests
{
    public static Task Run()
    {
        var secret = ShortCode.NewSecret();
        var code = ShortCode.Build("136.56.230.166", 5201, secret);
        var store = new SessionStore { CodeSecret = () => secret, CurrentCode = () => code };

        var knownId = PeerIdentity.NewId();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = PeerIdentity.PublicKeyOf(key);
        store.KnownKey = id => id == knownId ? pub : null;
        store.AutoApproves = id => id == knownId;
        store.RememberedPermissions = id => id == knownId ? new Permissions(true, true, false) : null;

        var stranger = new SessionRequest(PeerIdentity.NewId(), "Stranger", "PC-X");
        var old = store.Request(stranger with { Code = "ZZZZ" }, "203.0.113.5", out _);
        Check.That(old.State == SessionStates.Denied && old.Reason == DenyReasons.CodeChanged, "old code refused before any prompt");
        Check.That(store.Request(stranger with { Code = secret }, "203.0.113.6", out _).State == SessionStates.Pending, "current code reaches the prompt");
        var known = store.Request(new SessionRequest(knownId, "Dillon", "OS-PC-22", pub, "n", "s"), "203.0.113.7", out _, verifiedKey: pub);
        Check.That(known.State == SessionStates.Denied, "a PC approved before is also refused with the old code");
        var knownNew = store.Request(new SessionRequest(knownId, "Dillon", "OS-PC-22", pub, "n", "s", Code: secret), "203.0.113.8", out _, verifiedKey: pub);
        Check.That(knownNew.State == SessionStates.Approved && knownNew.Grant?.Code == code, "…and let in with the new code, which it is handed");
        Check.That(new SessionStore().Request(new SessionRequest(PeerIdentity.NewId(), "A", "B"), "203.0.113.9", out _).State == SessionStates.Pending,
            "no secret set: behaves as before");

        // Resume: asked, pending until answered, then the answer; unknown ids say so.
        SessionStore.ResumeAsk? asked = null;
        store.ResumeAsked += a => asked = a;
        var askId = store.AskResume("peer-x", "Dillon", "downloading \"big\" from you");
        var pending = store.ResumeState(askId);
        store.AnswerResume(askId, false);
        Check.That(asked?.PeerName == "Dillon" && pending == "pending" && store.ResumeState(askId) == "no" && store.ResumeState("nope") == "unknown",
            "resume question: raised, pending, answered, unknown id");
        return Task.CompletedTask;
    }
}

static class DeleteTests
{
    public static Task Run()
    {
        var root = Data.NewDir("delete");
        var tree = Path.Combine(root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
        File.WriteAllText(Path.Combine(tree, "a", "plain.txt"), "x");
        var readOnly = Path.Combine(tree, "a", "b", "readonly.txt");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        new DirectoryInfo(Path.Combine(tree, "a", "b")).Attributes |= FileAttributes.ReadOnly;
        var locked = Path.Combine(tree, "locked", "open.blend");
        Directory.CreateDirectory(Path.GetDirectoryName(locked)!);
        File.WriteAllText(locked, "x");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "must survive");
        Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{Path.Combine(tree, "link")}\" \"{outside}\"")
            { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();

        var failures = new List<string>();
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            LocalFs.DeleteTree(tree, failures);
        Check.That(!File.Exists(readOnly) && !Directory.Exists(Path.Combine(tree, "a")), "read-only file and folder deleted");
        Check.That(File.Exists(Path.Combine(outside, "keep.txt")) && !Directory.Exists(Path.Combine(tree, "link")),
            "junction removed without touching its target");
        Check.That(File.Exists(locked) && failures.Count == 1 && failures[0].Contains("open.blend"), "only the locked file stays, and is named");
        failures.Clear();
        LocalFs.DeleteTree(tree, failures);
        Check.That(!Directory.Exists(tree) && failures.Count == 0, "second press removes the rest");

        // Directory.Delete's own failure mode, so a regression back to it shows up here.
        var many = Data.Tree("delete-many", small: 2000, medium: 0, large: 0);
        foreach (var file in Directory.EnumerateFiles(many, "*", SearchOption.AllDirectories).Where((_, i) => i % 97 == 0))
            File.SetAttributes(file, FileAttributes.ReadOnly);
        var clock = Stopwatch.StartNew();
        LocalFs.DeleteTree(many, failures);
        Check.That(!Directory.Exists(many) && failures.Count == 0, $"2,000 files with some read-only: gone in one pass ({clock.ElapsedMilliseconds} ms)");
        return Task.CompletedTask;
    }
}

static class DiscoveryTests
{
    public static async Task Run()
    {
        var code = ShortCode.Build("136.56.230.166", 5201, ShortCode.NewSecret());
        var part = ShortCode.AddressPart(code)!;
        using (LanDiscovery.Respond(5398, () => code))
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            udp.Client.ReceiveTimeout = 1500;
            string Ask(string addressPart)
            {
                udp.Send(Encoding.ASCII.GetBytes("FSD1?" + addressPart), new IPEndPoint(IPAddress.Loopback, 5398));
                try { var from = new IPEndPoint(IPAddress.Any, 0); return Encoding.ASCII.GetString(udp.Receive(ref from)); }
                catch { return ""; }
            }
            ShortCode.TryParse(code, out _, out _, out var secret);
            Check.That(Ask(part) == "FSD1!5398", "answers for its own code");
            Check.That(Ask(part + "#" + secret) == "FSD1!5398", "answers when the secret matches");
            Check.That(Ask(part + "#ZZZZ") == "", "quiet when the secret differs (another PC with the same address)");
            Check.That(Ask(ShortCode.Build("1.2.3.4", 5201)) == "", "quiet for someone else's code");
            Check.That(await LanDiscovery.FindAsync(part, 5398, TimeSpan.FromMilliseconds(600)) is null, "ignores its own answer");
        }
    }
}

static class UpdateTests
{
    public static Task Run()
    {
        var release = Data.NewDir("release");
        foreach (var name in new[] { "FastShare.exe", "wpfgfx_cor3.dll", "GUIDE.md", "compress.bat", "compress.py", "Fast-Share-V9.9.9.rar", "Website Downloads.lnk" })
            File.WriteAllText(Path.Combine(release, name), name);
        Directory.CreateDirectory(Path.Combine(release, "updater-tool"));
        File.WriteAllText(Path.Combine(release, "updater-tool", "FastShareUpdateServer.exe"), "x");

        var shipped = Updater.PackagedFiles(release).Select(Path.GetFileName).Order().ToList();
        Check.That(shipped.SequenceEqual(["FastShare.exe", "GUIDE.md", "wpfgfx_cor3.dll"]),
            $"update package is only the app's own files ({string.Join(", ", shipped)})");

        var zip = Updater.BuildPackage(release);
        using (var archive = ZipFile.OpenRead(zip))
            Check.That(archive.Entries.Count == 3, "built package holds exactly those");

        Check.That(Updater.IsNewer("999.0.0") && !Updater.IsNewer("0.0.1"), "version comparison");
        Check.That(WebUpdate.Site.StartsWith("https://") && WebUpdate.Site.EndsWith('/'), $"update website set ({WebUpdate.Site})");
        return Task.CompletedTask;
    }
}
