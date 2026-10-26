using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastShare;

/// <summary>What the Discord status says. Records compare by value, so an unchanged one is not resent.</summary>
public sealed record PresenceActivity(string Details, string? State, DateTime? StartUtc);

/// <summary>
/// "Playing Fast Share" on Discord, through the local IPC pipe the Discord app opens
/// (\\.\pipe\discord-ipc-0..9). Spoken directly rather than through an SDK: it is four frame
/// types, and Code\ takes no third-party binaries.
///
/// One background loop owns the pipe. <see cref="Show"/> only records what should be shown; the
/// loop connects (retrying while Discord is closed), sends it when it changes - no more often
/// than Discord allows - and answers pings. Disabling, or the process ending, closes the pipe and
/// Discord clears the status by itself.
///
/// Never names files, folders or people: the status is visible to all of the user's Discord friends.
/// </summary>
public static class DiscordPresence
{
    /// <summary>The Fast Share application in the Discord Developer Portal (not a secret).</summary>
    private const string ClientId = "1564103262632935479";

    /// <summary>Art asset uploaded under this name in the Developer Portal (Rich Presence → Art Assets).</summary>
    private const string LargeImage = "logo";

    private const string PageUrl = "https://teamwolfgamers.online/fast-share";

    // Discord accepts 5 updates per 20 s. A new kind of status (idle → sending) goes out after
    // 5 s; the same kind with new figures (percent, speed) at most every 15 s.
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FigureGap = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly object Gate = new();
    private static CancellationTokenSource? _cts;
    private static Task? _loop;
    private static PresenceActivity? _wanted;

    /// <summary>Starts or stops the loop. Safe to call repeatedly with the same value.</summary>
    public static void SetEnabled(bool on)
    {
        lock (Gate)
        {
            if (on == (_cts is not null)) return;

            if (on)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => RunAsync(token));
            }
            else
            {
                _cts!.Cancel();
                _cts = null;
                _loop = null;
            }
        }
    }

    public static void Show(PresenceActivity activity) => Volatile.Write(ref _wanted, activity);

    private static async Task RunAsync(CancellationToken ct)
    {
        var failureLogged = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = await ConnectAsync(ct);
                if (pipe is null)
                {
                    await Task.Delay(RetryGap, ct);
                    continue;
                }

                var writeLock = new SemaphoreSlim(1, 1);
                await WriteFrameAsync(pipe, writeLock, Op.Handshake, new { v = 1, client_id = ClientId }, ct);
                var (op, _) = await ReadFrameAsync(pipe, ct);
                if (op != Op.Frame) throw new IOException($"Discord refused the handshake (op {op})");

                Diagnostics.Log("discord", "connected");
                failureLogged = false;

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var reader = ReadLoopAsync(pipe, writeLock, linked.Token);

                PresenceActivity? sent = null;
                var lastSend = DateTime.MinValue;
                try
                {
                    while (!reader.IsCompleted)
                    {
                        var wanted = Volatile.Read(ref _wanted);
                        var since = DateTime.UtcNow - lastSend;
                        if (wanted is not null && wanted != sent &&
                            since >= (sent?.Details == wanted.Details ? FigureGap : MinGap))
                        {
                            await WriteFrameAsync(pipe, writeLock, Op.Frame, SetActivity(wanted), ct);
                            sent = wanted;
                            lastSend = DateTime.UtcNow;
                        }
                        await Task.WhenAny(reader, Task.Delay(1000, ct));
                        ct.ThrowIfCancellationRequested();
                    }
                }
                finally
                {
                    linked.Cancel();
                }
                Diagnostics.Log("discord", "Discord closed the connection");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!failureLogged) Diagnostics.Log("discord", "presence connection failed", ex);
                failureLogged = true;
            }

            try { await Task.Delay(RetryGap, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The first Discord pipe that answers, or null when Discord is not running.</summary>
    private static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken ct)
    {
        // Listing the pipes is instant; trying to connect to one that does not exist waits out the timeout.
        var open = Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 10; i++)
        {
            var name = $"discord-ipc-{i}";
            if (!open.Contains(name)) continue;

            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(1000, ct);
                return pipe;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
            }
        }
        return null;
    }

    /// <summary>Answers pings and ends when Discord closes the pipe or says goodbye.</summary>
    private static async Task ReadLoopAsync(Stream pipe, SemaphoreSlim writeLock, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var (op, body) = await ReadFrameAsync(pipe, ct);
                if (op == Op.Close) return;
                if (op == Op.Ping) await WriteRawAsync(pipe, writeLock, Op.Pong, body, ct);

                // A refused SET_ACTIVITY comes back as evt ERROR with Discord's reason.
                if (op == Op.Frame && Encoding.UTF8.GetString(body) is var text && text.Contains("\"evt\":\"ERROR\""))
                    Diagnostics.Log("discord", "Discord refused the status: " + text);
            }
        }
        catch (Exception) { }
    }

    private static object SetActivity(PresenceActivity a) => new
    {
        cmd = "SET_ACTIVITY",
        nonce = Guid.NewGuid().ToString("N"),
        args = new
        {
            pid = Environment.ProcessId,
            activity = new
            {
                details = a.Details,
                state = a.State,
                timestamps = a.StartUtc is { } start ? new { start = new DateTimeOffset(start).ToUnixTimeSeconds() } : null,
                assets = new { large_image = LargeImage, large_text = $"Fast Share {AppVersion.Text}" },
                buttons = new[] { new { label = "Get Fast Share", url = PageUrl } },
            },
        },
    };

    // -----------------------------------------------------------------------
    // Frames: little-endian int32 opcode, int32 length, then UTF-8 JSON.
    // -----------------------------------------------------------------------

    private static class Op
    {
        public const int Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4;
    }

    private static Task WriteFrameAsync(Stream pipe, SemaphoreSlim writeLock, int op, object payload, CancellationToken ct) =>
        WriteRawAsync(pipe, writeLock, op, JsonSerializer.SerializeToUtf8Bytes(payload, Json), ct);

    private static async Task WriteRawAsync(Stream pipe, SemaphoreSlim writeLock, int op, byte[] body, CancellationToken ct)
    {
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);

        await writeLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }
        finally { writeLock.Release(); }
    }

    private static async Task<(int Op, byte[] Body)> ReadFrameAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 1 << 20) throw new IOException($"Discord sent a frame of {length} bytes");

        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct);
        return (op, body);
    }
}
