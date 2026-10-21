using System.Diagnostics;

namespace FastShare.Tests;

static class TransferTests
{
    public static async Task Run()
    {
        var source = Data.Tree("share");
        await using var host = await Host.StartAsync(source);
        var share = Path.GetFileName(source);

        // Download everything.
        var manifest = await host.Client.ManifestAsync(share, default);
        var dest = Data.NewDir("download");
        var plan = manifest.Files.Select(f => new PlanItem($"{share}/{f.Path}",
            Path.Combine(dest, f.Path.Replace('/', '\\')), f.Size, f.ModifiedUnixMs)).ToList();
        var downloader = new Downloader(host.Client);
        await downloader.RunAsync(plan, default, (manifest.EmptyDirs ?? []).Select(d => Path.Combine(dest, d.Replace('/', '\\'))).ToList());
        var (files, bad) = Data.Compare(source, dest);
        Check.That(bad == 0 && files == plan.Count && downloader.Stats.Errors.Count == 0,
            $"download: {files} files byte-identical ({downloader.Stats.BytesDone / 1048576} MB)");
        Check.That(Directory.Exists(Path.Combine(dest, "tree", "empty")), "download: empty folder recreated");

        // The host reports free space for "not enough room" checks.
        var (_, free) = await host.Client.DriveInfoForAsync(share, default);
        Check.That(free is > 0, $"host reports its free space ({Format.Bytes(free ?? 0)})");

        // "What I am doing before it starts", over the wire: the host shows the stage, then a
        // not-enough-space ending once, and an empty stage clears it.
        TrafficMonitor.DrainNoSpace();
        await host.Client.AnnouncePrepareAsync(true, "listing");
        Check.That(TrafficMonitor.Queued().Exists(q => q.Direction == TransferDirection.Upload && q.Stage == "listing"),
            "host shows what the other PC is doing before a download starts");
        await host.Client.AnnouncePrepareAsync(true, "nospace", 5_000_000, 1_000);
        var noSpace = TrafficMonitor.DrainNoSpace();
        Check.That(noSpace.Count == 1 && noSpace[0].Need == 5_000_000 && noSpace[0].Free == 1_000 &&
                   !TrafficMonitor.Queued().Exists(q => q.Direction == TransferDirection.Upload),
            "not enough space ends it, reported once with the sizes");
        await host.Client.AnnouncePrepareAsync(true, "location");
        await host.Client.AnnouncePrepareAsync(true, "");
        Check.That(!TrafficMonitor.Queued().Exists(q => q.Direction == TransferDirection.Upload), "an empty stage clears it");

        // A small file the host cannot read (held open by another program) fails with that reason.
        var lockedFile = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).First(f => new FileInfo(f).Length is > 0 and < 4096);
        var lockedRemote = $"{share}/{Path.GetRelativePath(source, lockedFile).Replace(Path.DirectorySeparatorChar, '/')}";
        var lockedDest = Data.NewDir("locked");
        using (File.Open(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var lockedRun = new Downloader(host.Client);
            await lockedRun.RunAsync([new PlanItem(lockedRemote, Path.Combine(lockedDest, "x.bin"), new FileInfo(lockedFile).Length, 0)], default, []);
            Check.That(lockedRun.Stats.Errors.Count == 1 && lockedRun.Stats.Errors[0].Contains("In use by a program on the other PC"),
                $"a locked small file says it is in use ({lockedRun.Stats.Errors.FirstOrDefault()})");
        }

        // The optional after-transfer check: clean, then with one file corrupted on our side.
        var pairs = plan.Select(p => (p.Remote, p.Local)).ToList();
        var reports = new System.Collections.Concurrent.ConcurrentQueue<Verify.CheckProgress>();
        Check.That((await Verify.CompareAsync(host.Client, pairs, reports.Enqueue, default)).Count == 0, "check after download: every file matches");
        var planBytes = plan.Sum(p => p.Size);
        var last = reports.Last();
        Check.That(last.Percent == 100 && last.BytesRead == planBytes && last.Bytes == planBytes &&
                   reports.Any(r => r.BytesRead > 0 && r.Percent < 100),
            $"check progress counts bytes as it reads ({reports.Count} reports, ends at {last.Percent:0}% / {last.BytesRead:N0} of {planBytes:N0})");
        var victim = plan.First(p => p.Size > 1000).Local;
        using (var f = new FileStream(victim, FileMode.Open)) { f.Position = 500; f.WriteByte(0x5A); f.WriteByte(0xA5); }
        var problems = await Verify.CompareAsync(host.Client, pairs, null, default);
        Check.That(problems.Count == 1 && problems[0].StartsWith(Path.GetFileName(victim)), $"check flags the corrupted file ({problems.FirstOrDefault()})");
        File.Copy(Path.Combine(source, Path.GetRelativePath(dest, victim)), victim, overwrite: true);
        var sample = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).First();
        Check.That(Math.Abs((File.GetLastWriteTimeUtc(sample) - File.GetLastWriteTimeUtc(Path.Combine(dest, Path.GetRelativePath(source, sample)))).TotalSeconds) < 2,
            "download: dates kept");

        // Upload everything back into a sub-folder of the share.
        var items = Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories)
            .Select(f => new UploadItem(f, Path.GetRelativePath(dest, f).Replace('\\', '/'), new FileInfo(f).Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds()))
            .Append(new UploadItem(Path.Combine(dest, "tree", "empty"), "tree/empty", -1)).ToList();
        var uploader = new Uploader(host.Client);
        await uploader.RunAsync($"{share}/uploaded", items, default);
        var (upFiles, upBad) = Data.Compare(dest, Path.Combine(source, "uploaded"));
        Check.That(upBad == 0 && upFiles == items.Count - 1 && uploader.Stats.Errors.Count == 0, $"upload: {upFiles} files byte-identical");
        Check.That(!Directory.EnumerateFiles(source, "*.fspart*", SearchOption.AllDirectories).Any(), "upload: no temp files left");

        // Delete over the wire, including read-only files.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "uploaded"), "*", SearchOption.AllDirectories).Take(5))
            File.SetAttributes(file, FileAttributes.ReadOnly);
        Check.That(await host.Client.DeleteAsync([$"{share}/uploaded"], default) is null && !Directory.Exists(Path.Combine(source, "uploaded")),
            "delete over the wire removes read-only files too");
    }
}

static class TunnelTests
{
    public static async Task Run()
    {
        // "Patrick" hosts; "Dillon" connects in and shares back over the tunnel.
        await using var host = await Host.StartAsync(Data.NewDir("host-share"));
        var dillonShare = Data.NewDir("DillonShare");
        var shareName = Path.GetFileName(dillonShare);
        var shares = () => new ShareSet([dillonShare]);

        var dillonSessions = new SessionStore();
        var reverse = dillonSessions.CreateReverse(PeerIdentity.NewId(), "Patrick", new Permissions(true, true, true, Modify: true));
        var writer = ShareServer.ForTunnel(dillonSessions, shares);
        await writer.StartAsync(Data.Config(dillonShare), loopbackOnly: true);

        var tunnel = new TunnelService { PeerLabel = "Patrick", WriteTarget = () => (writer.Info!, reverse.Token) };
        tunnel.Start(host.Client.Info with { Fingerprint = host.Client.ObservedFingerprint ?? "" }, host.Client.Grant!.Token, shares);
        PeerTunnel? peer = null;
        for (var i = 0; i < 50 && (peer = host.Server.Tunnels.Find(host.ClientPeerId)) is null; i++) await Task.Delay(100);
        await Task.Delay(500);
        Check.That(peer is { SocketCount: > 0 }, $"tunnel up ({peer?.SocketCount} sockets)");
        if (peer is null) return;

        var api = new ApiClient(new ConnectionInfo("tunnel", 0, "", "", "Dillon"), peer.CreateHttpHandler());
        var ping = await api.PingAsync(default);
        Check.That(ping.Granted is { Send: true, Delete: true, Modify: true } && ping.Shares.Contains(shareName),
            "ping over the tunnel returns Dillon's permissions and shares");

        var src = Data.Tree("to-dillon", small: 200, medium: 2, large: 60_000_000);
        var items = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Select(f => new UploadItem(f, Path.GetRelativePath(src, f).Replace('\\', '/'), new FileInfo(f).Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds())).ToList();
        var up = new Uploader(api);
        await up.RunAsync(shareName, items, default);
        var (files, bad) = Data.Compare(src, dillonShare);
        Check.That(bad == 0 && up.Stats.Errors.Count == 0, $"upload into Dillon's share over the tunnel: {files} files byte-identical");
        Check.That((await Verify.CompareAsync(api, items.Select(i => ($"{shareName}/{i.Relative}", i.Local)).ToList(), null, default)).Count == 0,
            "check after upload over the tunnel: every file matches");

        // Reading back over the tunnel with the normal downloader.
        var back = Data.NewDir("from-dillon");
        var manifest = await peer.ManifestAsync(shareName, default);
        var dl = new Downloader(peer);
        await dl.RunAsync(manifest.Files.Select(f => new PlanItem($"{shareName}/{f.Path}", Path.Combine(back, f.Path.Replace('/', '\\')), f.Size, f.ModifiedUnixMs)).ToList(), default);
        Check.That(Data.Compare(src, back).Bad == 0, "download from Dillon over the tunnel byte-identical");

        Check.That(await api.CreateAsync($"{shareName}/made", true, default) is null && Directory.Exists(Path.Combine(dillonShare, "made")), "create folder");
        Check.That(await api.RenameAsync($"{shareName}/made", "renamed", default) is null && Directory.Exists(Path.Combine(dillonShare, "renamed")), "rename");
        Check.That(await api.DeleteAsync([$"{shareName}/renamed"], default) is null && !Directory.Exists(Path.Combine(dillonShare, "renamed")), "delete");

        reverse.Permissions = new Permissions(true, false, false);
        Check.That((await api.PingAsync(default)).Granted is { Send: false, Delete: false }, "permission change visible on the next ping");
        Check.That(await api.DeleteAsync([$"{shareName}/tree"], default) is not null && Directory.Exists(Path.Combine(dillonShare, "tree")),
            "delete refused once Delete is off");

        var blocked = await new HttpClient(peer.CreateHttpHandler()).PostAsync("https://tunnel/api/session/request",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Check.That((int)blocked.StatusCode == 400, "session endpoints are not reachable through the tunnel");

        await tunnel.DisposeAsync();
        await writer.StopAsync();
    }
}

static class CancelTests
{
    public static async Task Run()
    {
        var source = Data.Tree("cancel", small: 3000, medium: 0, large: 2_000_000_000);
        await using var host = await Host.StartAsync(source);
        var share = Path.GetFileName(source);
        var manifest = await host.Client.ManifestAsync(share, default);

        foreach (var (label, pick) in new (string, Func<ManifestFile, bool>)[]
                 { ("small files", f => f.Size < 4_000_000), ("2 GB file", f => f.Size > 1_000_000_000) })
        {
            var dest = Data.NewDir("cancel-dest");
            var plan = manifest.Files.Where(pick).Select(f => new PlanItem($"{share}/{f.Path}",
                Path.Combine(dest, f.Path.Replace('/', '\\')), f.Size, f.ModifiedUnixMs)).ToList();
            var dl = new Downloader(host.Client);
            using var cts = new CancellationTokenSource();
            var run = dl.RunAsync(plan, cts.Token);
            await Task.Delay(700);
            var clock = Stopwatch.StartNew();
            cts.Cancel();
            var cancelled = false;
            try { await run; } catch (OperationCanceledException) { cancelled = true; }
            Check.That(cancelled && clock.ElapsedMilliseconds < 2000, $"cancel stops a {label} download in {clock.ElapsedMilliseconds} ms");
        }
    }
}
