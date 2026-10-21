using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FastShare;

/// <summary>
/// The app's own iperf: how fast the line between two PCs is, with no disk involved.
///
/// When a transfer is slow the question is always "the network, or one of the PCs?". This
/// answers the first half directly. The host streams bytes out of memory and the client throws
/// them away, over the same HTTPS connections, TLS and eight-stream layout a real transfer
/// uses - so whatever this measures is the ceiling a transfer could reach. A transfer well
/// under it is losing time on a disk, the CPU or Defender; one at it is the line.
///
/// Phases: download over 4, 6, 8, 10, 12 and 16 streams (each twice, to find the best count for
/// this line), download over 1 (the gap is what the extra streams buy on this path), upload
/// over 8, and the ping idle vs loaded (a large rise under load is queueing in a router -
/// bufferbloat - which also costs throughput). About 75 seconds.
/// </summary>
public static class SpeedTest
{
    public const int PhaseSeconds = 8;
    private const int MaxSeconds = 20;

    /// <summary>Random, so nothing on the path could ever compress it and flatter the figure.</summary>
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(1 << 20);

    /// <summary>Stream counts the download is tried with, to find the best one for this line.</summary>
    public static readonly int[] SweepCounts = [4, 6, 8, 10, 12, 16];

    /// <summary>Seconds per sweep step: one of warm-up, four measured.</summary>
    private const int SweepSeconds = 5;

    public sealed record Result(double Down8, double Down1, double Up8, double IdlePingMs, double LoadedPingMs,
        double Down16 = 0, IReadOnlyDictionary<int, double>? Sweep = null)
    {
        public string Summary =>
            (Sweep is { Count: > 0 }
                ? $"download by streams: {string.Join(", ", Sweep.Select(kv => $"{kv.Key} = {Format.Rate(kv.Value)}"))} " +
                  $"(best {Sweep.MaxBy(kv => kv.Value).Key}), {Format.Rate(Down1)} (1 stream) · "
                : $"download {Format.Rate(Down8)} (8 streams), {Format.Rate(Down16)} (16 streams), {Format.Rate(Down1)} (1 stream) · ") +
            $"upload {Format.Rate(Up8)} (8 streams) · ping {IdlePingMs:0} ms idle, {LoadedPingMs:0} ms loaded";
    }

    // ---- host side ----------------------------------------------------------

    public static void MapEndpoints(WebApplication app, Action<string> report)
    {
        // Streams from memory until the time is up or the client hangs up.
        app.MapGet("/api/speed/down", async (HttpContext context, int? seconds) =>
        {
            var limit = TimeSpan.FromSeconds(Math.Clamp(seconds ?? PhaseSeconds, 1, MaxSeconds));
            context.Response.ContentType = "application/octet-stream";
            var clock = Stopwatch.StartNew();
            try
            {
                while (clock.Elapsed < limit && !context.RequestAborted.IsCancellationRequested)
                    await context.Response.Body.WriteAsync(Payload, context.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        });

        // Reads and discards; answers with what arrived, so the client can check its own count.
        app.MapPost("/api/speed/up", async (HttpContext context) =>
        {
            var buffer = new byte[1 << 20];
            long total = 0;
            try
            {
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0) total += read;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            return Results.Json(new { bytes = total });
        });

        // The client's figures, so the host - who usually asked for the test - gets them too.
        app.MapPost("/api/speed/result", async (HttpContext context) =>
        {
            if (context.Items["session"] is not SessionStore.Session session) return Results.Unauthorized();
            using var reader = new StreamReader(context.Request.Body);
            var text = await reader.ReadToEndAsync();
            if (text.Length > 2000) text = text[..2000];
            report($"{session.DisplayName} measured: {text}");
            return Results.NoContent();
        });
    }

    // ---- client side --------------------------------------------------------

    public static async Task<Result> RunAsync(ApiClient api, Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("Speed test: measuring ping…");
        var idle = await MedianPingAsync(api, 5, ct);

        // How many connections suit this line? On a line that drops packets, each loss halves
        // one stream's speed, so more streams soften it - up to the point where they only
        // compete. Each count is tried twice, in opposite orders, so a line that speeds up or
        // slows down during the test does not favour whichever count happened to go first.
        var order = SweepCounts.Concat(SweepCounts.Reverse()).ToArray();
        var samples = SweepCounts.ToDictionary(n => n, _ => new List<double>());
        using var loadedPings = new CancellationTokenSource();
        var pinging = PingWhileAsync(api, loadedPings.Token);
        for (var i = 0; i < order.Length; i++)
        {
            progress?.Invoke($"Speed test: download, {order[i]} streams ({i + 1} of {order.Length})…");
            samples[order[i]].Add(await DownloadAsync(api, order[i], ct, SweepSeconds));
        }
        loadedPings.Cancel();
        var loaded = await pinging;
        var sweep = SweepCounts.ToDictionary(n => n, n => samples[n].Average());

        progress?.Invoke("Speed test: download, 1 stream…");
        var down1 = await DownloadAsync(api, 1, ct, SweepSeconds);

        progress?.Invoke("Speed test: upload, 8 streams…");
        var up8 = await UploadAsync(api, 8, ct);

        var result = new Result(sweep[8], down1, up8, idle, loaded, sweep[16], sweep);
        Diagnostics.Log("speedtest", $"to {api.Info.Host}:{api.Info.Port}: {result.Summary}");
        return result;
    }

    private static async Task<double> DownloadAsync(ApiClient api, int streams, CancellationToken ct,
        int seconds = PhaseSeconds)
    {
        long total = 0;
        var clock = Stopwatch.StartNew();
        // A second of warm-up is excluded, so slow start and the TLS handshakes do not count.
        long warm = -1;
        TimeSpan warmAt = default;

        await Task.WhenAll(Enumerable.Range(0, streams).Select(async _ =>
        {
            await using var stream = await api.SpeedDownAsync(seconds, ct);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                var now = Interlocked.Add(ref total, read);
                if (warm < 0 && clock.Elapsed.TotalSeconds >= 1 && Interlocked.CompareExchange(ref warm, now, -1) == -1)
                    warmAt = clock.Elapsed;
            }
        }));

        clock.Stop();
        if (warm < 0) return total / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        return (total - warm) / Math.Max((clock.Elapsed - warmAt).TotalSeconds, 0.001);
    }

    private static async Task<double> UploadAsync(ApiClient api, int streams, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var counts = await Task.WhenAll(Enumerable.Range(0, streams).Select(_ =>
            api.SpeedUpAsync(new TimedContent(TimeSpan.FromSeconds(PhaseSeconds)), ct)));
        clock.Stop();
        return counts.Sum() / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
    }

    private static async Task<double> MedianPingAsync(ApiClient api, int count, CancellationToken ct)
    {
        var samples = new List<double>();
        for (var i = 0; i < count; i++)
        {
            try { samples.Add((await api.PingAsync(ct)).Milliseconds); } catch when (!ct.IsCancellationRequested) { }
            await Task.Delay(200, ct);
        }
        return Median(samples);
    }

    private static async Task<double> PingWhileAsync(ApiClient api, CancellationToken stop)
    {
        var samples = new List<double>();
        try
        {
            await Task.Delay(1500, stop);   // let the streams fill the line first
            while (!stop.IsCancellationRequested)
            {
                try { samples.Add((await api.PingAsync(stop)).Milliseconds); } catch when (!stop.IsCancellationRequested) { }
                await Task.Delay(400, stop);
            }
        }
        catch (OperationCanceledException) { }
        return Median(samples);
    }

    private static double Median(List<double> samples)
    {
        if (samples.Count == 0) return 0;
        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>A request body that keeps sending the random payload until its time is up.</summary>
    private sealed class TimedContent(TimeSpan duration) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context,
            CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < duration) await stream.WriteAsync(Payload, ct);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
