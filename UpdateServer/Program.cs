using System.IO;
using System.Net;
using System.Text.Json;
using FastShare;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// FastShare update listener.
//
// Updates are offered only while this window is open. Run it, and friends can press "Check for
// updates"; close it, and they can't. It presents the same certificate as FastShare, on the
// same port, so their app accepts it. If FastShare is running here it steps aside (two programs
// cannot share a port) and FastShare hands the build out instead - still only while this is open.
//
// It only ever hands out the contents of your release folder, read-only.

Console.Title = "FastShare Update Server";

// The main app's config, so the certificate (and therefore the fingerprint your friends have
// pinned), the port and the release folder all match exactly what the app itself uses.
var configPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastShare", "config.json");

string? certPassword = null;
var port = 5201;
var releasePath = @"F:\Patrick\Projects\Apps\Fast Share\Build";

try
{
    if (File.Exists(configPath))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;
        if (root.TryGetProperty("CertPassword", out var pw) && pw.ValueKind == JsonValueKind.String)
            certPassword = pw.GetString();
        if (root.TryGetProperty("UpdatePort", out var up) && up.TryGetInt32(out var p)) port = p;
        else if (root.TryGetProperty("Port", out var sp) && sp.TryGetInt32(out var p2)) port = p2;
        if (root.TryGetProperty("ReleasePath", out var rp) && rp.ValueKind == JsonValueKind.String)
            releasePath = rp.GetString() ?? releasePath;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Could not read FastShare config: {ex.Message}");
}

// A saved path that no longer exists (the project has moved before) falls back to the folder
// this tool sits in (Build\updater-tool -> Build), then the current project location.
if (!Directory.Exists(releasePath))
{
    var besideTool = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    releasePath = File.Exists(Path.Combine(besideTool, "FastShare.exe")) ? besideTool : @"F:\Patrick\Projects\Apps\Fast Share\Build";
}

var certPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastShare", "cert.pfx");

if (!File.Exists(certPath) || string.IsNullOrEmpty(certPassword))
{
    Console.WriteLine("No FastShare certificate found yet. Start FastShare and turn on sharing once,");
    Console.WriteLine("then run this again - it reuses the same certificate so friends stay pinned.");
    Console.WriteLine("Press any key to close.");
    Console.ReadKey();
    return;
}

var certificate = CertStore.LoadOrCreate(certPath, ref certPassword);

var build = Updater.LocalBuild(releasePath);
if (build is null)
{
    Console.WriteLine($"No build found in the release folder:\n  {releasePath}");
    Console.WriteLine("Publish a release there first. Press any key to close.");
    Console.ReadKey();
    return;
}

// While this window is open, updates are on offer - and only then. FastShare itself hands them
// out only while it can see this marker, so closing this window stops updates everywhere.
using var serving = new Mutex(initiallyOwned: false, Updater.ServingMutexName);

Console.WriteLine("FastShare Update Server");
Console.WriteLine($"  offering build v{build.Version} ({build.Bytes / (1024.0 * 1024):0.#} MB)");
Console.WriteLine($"  from {releasePath}");
Console.WriteLine($"  on port {port}");
Console.WriteLine();
Console.WriteLine("Friends can now press \"Check for updates\" in their FastShare settings.");
Console.WriteLine("Leave this window open. Close it (or press Ctrl+C) to stop offering updates.");
Console.WriteLine();

var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

// Two programs cannot listen on one port. While FastShare is sharing it owns the port, so this
// steps aside and FastShare hands the build out instead; the rest of the time this listens
// itself. Either way friends see the same address.
WebApplication? app = null;
string? mode = null;

while (!stop.IsCancellationRequested)
{
    var fastShareRunning = Updater.IsMarked(Updater.SharingMutexName);   // FastShare is sharing (or about to)

    if (fastShareRunning && app is not null)
    {
        try { await app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
        await app.DisposeAsync();
        app = null;
    }
    else if (!fastShareRunning && app is null)
    {
        try
        {
            app = BuildApp();
            await app.StartAsync();
        }
        catch (Exception ex)
        {
            if (app is not null) { try { await app.DisposeAsync(); } catch { } }
            app = null;
            if (mode != "blocked")
            {
                mode = "blocked";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Port {port} is in use by something else — retrying. ({ex.Message})");
            }
        }
    }

    var now = app is not null ? "self" : fastShareRunning ? "handoff" : mode;
    if (now != mode && now is not null)
    {
        mode = now;
        Console.WriteLine(mode == "self"
            ? $"[{DateTime.Now:HH:mm:ss}] Serving updates on port {port}."
            : $"[{DateTime.Now:HH:mm:ss}] FastShare is sharing, so it is handing out the update on port {port} for as long as this window stays open.");
    }

    var current = Updater.LocalBuild(releasePath)?.Version;
    if (current is not null && current != build.Version)
    {
        build = Updater.LocalBuild(releasePath)!;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Picked up a new build in the release folder: v{current}");
    }

    try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { }
}

if (app is not null) { try { await app.StopAsync(TimeSpan.FromSeconds(2)); } catch { } }
Console.WriteLine("Stopped offering updates.");

WebApplication BuildApp()
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Listen(IPAddress.Any, port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseHttps(certificate);
        });
    });

    var web = builder.Build();

    web.MapGet("/api/update/info", () =>
    {
        var latest = Updater.LocalBuild(releasePath);
        return latest is null ? Results.NotFound() : Results.Json(latest);
    });

    web.MapGet("/api/update/package", () =>
    {
        if (Updater.LocalBuild(releasePath) is null) return Results.NotFound();
        try
        {
            var zip = Updater.BuildPackage(releasePath);
            return Results.File(zip, "application/zip", "FastShare-update.zip");
        }
        catch
        {
            return Results.Problem("The update package could not be built.");
        }
    });

    return web;
}
