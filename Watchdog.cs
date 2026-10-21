using System.IO;

namespace FastShare;

/// <summary>
/// Notices a transfer that has stopped moving.
///
/// A connection that dies without closing - Wi-Fi drops, the far PC sleeps, a router forgets
/// the flow - leaves a read or write waiting forever: nothing ever fails, so the retry never
/// starts and the progress bar just freezes. This cancels the operation once no progress has
/// been reported for the idle limit, and says so with a <see cref="TimeoutException"/> rather
/// than an <see cref="OperationCanceledException"/>, which the retry logic reserves for the
/// user pressing Cancel.
/// </summary>
public sealed class Watchdog : IDisposable
{
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _outer;
    private readonly TimeSpan _idle;

    public Watchdog(CancellationToken outer, TimeSpan? idle = null)
    {
        _outer = outer;
        _idle = idle ?? DefaultIdle;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _cts.CancelAfter(_idle);
    }

    /// <summary>Pass this to the operation being watched.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Progress happened; restart the idle clock.</summary>
    public void Pet()
    {
        try { _cts.CancelAfter(_idle); } catch (ObjectDisposedException) { }
    }

    /// <summary>True if the watchdog, not the caller, is what cancelled the operation.</summary>
    public bool Fired => _cts.IsCancellationRequested && !_outer.IsCancellationRequested;

    /// <summary>Runs an operation, turning a watchdog cancellation into a timeout.</summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        try { return await action(Token); }
        catch (OperationCanceledException) when (Fired) { throw Timeout(); }
    }

    public async Task RunAsync(Func<CancellationToken, Task> action)
    {
        try { await action(Token); }
        catch (OperationCanceledException) when (Fired) { throw Timeout(); }
    }

    public TimeoutException Timeout() =>
        new($"Nothing arrived from the other PC for {_idle.TotalSeconds:0} seconds.");

    public void Dispose() => _cts.Dispose();
}

/// <summary>
/// Wraps a response stream so that a read which waits too long fails instead of hanging.
/// Every read restarts the clock, so a slow but moving transfer is never cut off.
/// </summary>
public sealed class StallGuardStream(Stream inner, TimeSpan? idle = null) : Stream
{
    private readonly TimeSpan _idle = idle ?? Watchdog.DefaultIdle;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        using var dog = new Watchdog(ct, _idle);
        return await dog.RunAsync(token => inner.ReadAsync(buffer, token).AsTask());
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
