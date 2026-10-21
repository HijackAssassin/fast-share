using System.Diagnostics;
using System.Security.Cryptography;
using FastShare;
using FastShare.Tests;

// FastShare checks. `dotnet run --project Tests` (optionally a group name: codes, delete,
// transfer, tunnel, cancel, sessions, discovery, updates, format).

var home = Path.Combine(Path.GetTempPath(), "FastShareTests-" + Environment.ProcessId);
Directory.CreateDirectory(home);
Environment.SetEnvironmentVariable("FASTSHARE_HOME", home);   // before anything reads AppConfig
File.WriteAllText(Path.Combine(home, "config.json"), """{"KeepDefenderExcluded":false,"WasSharing":false}""");

var only = args.FirstOrDefault()?.ToLowerInvariant();
var groups = new (string Name, Func<Task> Run)[]
{
    ("format", FormatTests.Run),
    ("codes", CodeTests.Run),
    ("transfer end", TransferEndTests.Run),
    ("sessions", SessionTests.Run),
    ("lan list", LanListTests.Run),
    ("delete", DeleteTests.Run),
    ("discovery", DiscoveryTests.Run),
    ("updates", UpdateTests.Run),
    ("transfer", TransferTests.Run),
    ("tunnel", TunnelTests.Run),
    ("cancel", CancelTests.Run),
};

var clock = Stopwatch.StartNew();
foreach (var (name, run) in groups)
{
    if (only is not null && only != name) continue;
    Console.WriteLine($"== {name}");
    try { await run(); }
    catch (Exception ex) { Check.Fail($"{name} crashed: {ex.GetType().Name}: {ex.Message}"); }
}

try { Directory.Delete(home, recursive: true); } catch { }
Console.WriteLine();
Console.WriteLine(Check.Failures == 0
    ? $"ALL {Check.Passes} CHECKS PASSED ({clock.Elapsed.TotalSeconds:0} s)"
    : $"{Check.Failures} FAILED, {Check.Passes} passed");
return Check.Failures == 0 ? 0 : 1;

namespace FastShare.Tests
{
    static class Check
    {
        public static int Passes, Failures;

        public static void That(bool ok, string what)
        {
            if (ok) { Passes++; Console.WriteLine($"  PASS  {what}"); }
            else Fail(what);
        }

        public static void Fail(string what)
        {
            Failures++;
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  FAIL  {what}");
            Console.ForegroundColor = old;
        }
    }

    /// <summary>Generated test data and helpers shared by the groups.</summary>
    static class Data
    {
        public static string NewDir(string name)
        {
            var dir = Path.Combine(AppConfig.Dir, "data", name + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>small files, a few medium ones, one large one, and an empty folder.</summary>
        public static string Tree(string name, int small = 300, int medium = 3, long large = 150_000_000)
        {
            var root = NewDir(name);
            var rng = new Random(7);
            void Make(string rel, long size)
            {
                var path = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = new byte[size];
                rng.NextBytes(bytes);
                File.WriteAllBytes(path, bytes);
            }
            for (var i = 0; i < small; i++) Make($"tree/small/s{i}.bin", rng.Next(0, 200_000));
            for (var i = 0; i < medium; i++) Make($"tree/medium/m{i}.bin", 12_000_000 + i);
            if (large > 0) Make("tree/big.bin", large);
            Directory.CreateDirectory(Path.Combine(root, "tree", "empty"));
            return root;
        }

        public static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>Every file under <paramref name="a"/> exists under <paramref name="b"/> with the same bytes.</summary>
        public static (int Files, int Bad) Compare(string a, string b)
        {
            int files = 0, bad = 0;
            foreach (var file in Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories))
            {
                files++;
                var other = Path.Combine(b, Path.GetRelativePath(a, file));
                if (!File.Exists(other) || Hash(file) != Hash(other)) bad++;
            }
            return (files, bad);
        }

        public static AppConfig Config(params string[] folders)
        {
            var config = AppConfig.Load();
            config.KeepDefenderExcluded = false;
            config.Folders = folders.ToList();
            return config;
        }
    }

    /// <summary>A host server plus an approved client, over loopback TLS.</summary>
    sealed class Host : IAsyncDisposable
    {
        public ShareServer Server { get; } = new();
        public ApiClient Client { get; private set; } = null!;
        public string ClientPeerId { get; } = PeerIdentity.NewId();

        public static async Task<Host> StartAsync(params string[] folders)
        {
            var host = new Host();
            await host.Server.StartAsync(Data.Config(folders), loopbackOnly: true);
            host.Client = new ApiClient(host.Server.Info!);
            await host.Client.PingAsync(default);
            await host.Client.ConnectAsync(host.ClientPeerId, "tester", "TESTPC", default);
            return host;
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            await Server.StopAsync();
        }
    }
}
