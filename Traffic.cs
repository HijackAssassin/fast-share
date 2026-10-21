namespace FastShare;

/// <summary>
/// One transfer somebody else is driving: them pulling files off this PC, or pushing files
/// onto it.
///
/// Our own transfers have a <see cref="TransferStats"/> to report against; traffic the far
/// side starts has nothing - the bytes simply leave through an endpoint. Without this, a peer
/// downloading 40 GB showed as absolute silence: no rate in the status bar, no line on the
/// Session tab, nothing in the history.
///
/// A flow is opened by the first byte and closed once it has been quiet for a moment, which
/// is what turns thousands of range requests into the single line "Dillon · 412 files".
/// </summary>
internal sealed class PeerFlow
{
    public required string Key { get; init; }
    public required string PeerName { get; init; }

    /// <summary>From this PC's point of view: Upload when they are downloading from us.</summary>
    public required TransferDirection Direction { get; init; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUtc { get; set; } = DateTime.UtcNow;

    public long Bytes { get; set; }
    public int Files { get; set; }

    /// <summary>
    /// What the far side told us it is about to move, so a real percentage can be shown -
    /// for the sender as well as the downloader. Zero until they announce it (or if they run
    /// an older build that does not), in which case there is a count but no percentage.
    /// </summary>
    public long Total { get; set; }
    public int TotalFiles { get; set; }

    /// <summary>The far side's own disk for this transfer ("HDD"/"SSD"/"NVMe"), if they told us.
    /// The other end of the "SSD → HDD" reading; our end we resolve from our own shares.</summary>
    public string? PeerDrive { get; set; }

    /// <summary>The far side declared this transfer and has not yet said it finished.</summary>
    public bool Announced { get; set; }

    /// <summary>The far side said this transfer is done - close it at the next sweep.</summary>
    public bool Completed { get; set; }

    /// <summary>Why it ended before it finished (cancelled, connection lost…), or null.</summary>
    public string? Stopped { get; set; }

    /// <summary>It finished, but not cleanly ("2 failed - …"), for the row's Result.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// File checks (/api/hash) the far side has running against us right now. No file data
    /// moves while both PCs fingerprint everything, which on 125 GB is minutes - so while one
    /// is open the transfer is plainly still going, whatever the byte stream says.
    /// </summary>
    public int ChecksInFlight { get; set; }

    /// <summary>The far side has started checking the files: everything has been moved.</summary>
    public bool Checking { get; set; }

    /// <summary>Bytes read here for their check so far, against <see cref="Total"/> for its bar.</summary>
    public long CheckBytes { get; set; }

    /// <summary>Every announced byte and file is here (and nothing is being checked).</summary>
    public bool AllArrived =>
        Total > 0 && Bytes >= Total && (TotalFiles == 0 || Files >= TotalFiles);

    /// <summary>Distinct folders touched, so the line can say "9 folders" like the history does.</summary>
    public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bytes seen so far per file, so a file pulled as eight ranges counts once.</summary>
    public Dictionary<string, long> Partial { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What they actually took, so the row can be opened and read. Capped: "what are they
    /// downloading" is answered long before the ten-thousandth name, and an uncapped list
    /// would let a peer grow this window's memory just by browsing.
    /// </summary>
    public List<string> Names { get; } = [];

    // High enough that "show me everything they took" really means everything for any
    // realistic transfer, but still a ceiling so a peer cannot grow this without bound.
    public const int MaxNames = 100_000;
}

/// <summary>
/// A flow as it stood at one instant, safe to read from the UI thread.
///
/// The live flow is written by Kestrel threads under the monitor's lock, so handing the UI a
/// reference to it would be a race on every field - and enumerating its name list while a
/// request thread appended to it would throw.
/// </summary>
public sealed record FlowSnapshot(
    string Key,
    string PeerName,
    TransferDirection Direction,
    DateTime StartedUtc,
    double Seconds,
    long Bytes,
    int Files,
    int Folders,
    long Total,
    int TotalFiles,
    IReadOnlyList<string> Names,
    IReadOnlyList<string> FolderNames,
    string? PeerDrive = null,
    string? Stopped = null,
    string? Problem = null,
    bool Checking = false,
    long CheckBytes = 0);

/// <summary>
/// Accounting for traffic the far side drives. Deliberately WPF-free: the harnesses compile
/// it, and the endpoints that feed it run on Kestrel threads.
/// </summary>
public static class TrafficMonitor
{
    private static long _inBytes;
    private static long _outBytes;

    /// <summary>Running totals, sampled by the status bar's rate meters.</summary>
    public static long TotalBytes(TransferDirection direction) =>
        direction == TransferDirection.Download
            ? Interlocked.Read(ref _inBytes)
            : Interlocked.Read(ref _outBytes);

    private static readonly Dictionary<string, PeerFlow> Flows = [];

    /// <summary>
    /// Flows we just stopped, and until when: the far side's requests already in flight keep
    /// arriving for a second or so, and without this they opened a new, bogus row.
    /// </summary>
    private static readonly Dictionary<string, DateTime> Stopped = [];
    private static readonly Lock Gate = new();

    private static string KeyOf(string peerKey, TransferDirection direction) =>
        $"{direction}:{peerKey}";

    /// <summary>
    /// A transfer the far side has not started yet: queued behind another one (Position), or
    /// still being prepared (Stage: "location", "picking", "listing" or "overwrite").
    /// </summary>
    public sealed record QueuedFlow(string Key, string PeerName, TransferDirection Direction, int Position, DateTime SinceUtc,
        string? Stage = null);

    /// <summary>A transfer the far side gave up on before it started, because the receiving disk is too full.</summary>
    public sealed record NoSpaceNotice(string Key, string PeerName, TransferDirection Direction, long Need, long Free);

    private static readonly List<NoSpaceNotice> NoSpace = [];

    private static readonly Dictionary<string, QueuedFlow> QueuedFlows = [];

    /// <summary>They have a transfer with us waiting in their queue, at this position.</summary>
    public static void Queue(string peerKey, string peerName, TransferDirection direction, int position)
    {
        lock (Gate)
        {
            var key = KeyOf(peerKey, direction);
            QueuedFlows[key] = QueuedFlows.TryGetValue(key, out var old)
                ? old with { Position = position, PeerName = peerName, Stage = null }
                : new QueuedFlow(key, peerName, direction, position, DateTime.UtcNow);
        }
    }

    public static void Unqueue(string peerKey, TransferDirection direction)
    {
        lock (Gate) QueuedFlows.Remove(KeyOf(peerKey, direction));
    }

    /// <summary>
    /// What the far side is doing before its transfer starts. An empty stage clears it (they
    /// cancelled); "nospace" ends it, to be shown once (<see cref="DrainNoSpace"/>).
    /// </summary>
    public static void Prepare(string peerKey, string peerName, TransferDirection direction, string stage, long need = 0, long free = 0)
    {
        lock (Gate)
        {
            var key = KeyOf(peerKey, direction);
            // Already running: a stage that arrives late must not put it back to "getting ready".
            if (Flows.TryGetValue(key, out var running) && !running.Completed) return;
            if (string.IsNullOrWhiteSpace(stage)) { QueuedFlows.Remove(key); return; }
            if (stage == "nospace")
            {
                QueuedFlows.Remove(key);
                NoSpace.Add(new NoSpaceNotice(key, peerName, direction, need, free));
                return;
            }
            QueuedFlows[key] = QueuedFlows.TryGetValue(key, out var old)
                ? old with { Position = 0, PeerName = peerName, Stage = stage }
                : new QueuedFlow(key, peerName, direction, 0, DateTime.UtcNow, stage);
        }
    }

    /// <summary>"Not enough space" endings since the last call.</summary>
    public static List<NoSpaceNotice> DrainNoSpace()
    {
        lock (Gate)
        {
            var all = NoSpace.ToList();
            NoSpace.Clear();
            return all;
        }
    }

    /// <summary>What is waiting in other people's queues to come our way.</summary>
    public static List<QueuedFlow> Queued()
    {
        lock (Gate) return QueuedFlows.Values.ToList();
    }

    /// <summary>
    /// The far side is starting a transfer and has told us how big it will be. Gives the flow
    /// a total to show a percentage against, and a clear beginning - so what follows is one
    /// batch on the Session tab rather than several, however much the byte stream stalls.
    /// </summary>
    public static void Announce(string peerKey, string peerName, TransferDirection direction,
        long totalBytes, int totalFiles, string? peerDrive = null)
    {
        lock (Gate)
        {
            var key = KeyOf(peerKey, direction);
            var flow = new PeerFlow
            {
                Key = key,
                PeerName = peerName,
                Direction = direction,
                Total = totalBytes,
                TotalFiles = totalFiles,
                PeerDrive = peerDrive,
                Announced = true,
            };
            Flows[key] = flow;   // a fresh announce starts a fresh batch
            Stopped.Remove(key); // and is a real new transfer, even straight after a cancel
            QueuedFlows.Remove(key);   // it was waiting; now it runs
        }
    }

    /// <summary>
    /// The far side says its announced transfer is finished; close it next sweep. With
    /// <paramref name="stopped"/>, it ended early and that is what the row will say.
    /// </summary>
    public static void Complete(string peerKey, TransferDirection direction, string? stopped = null, string? problem = null)
    {
        lock (Gate)
        {
            if (Flows.TryGetValue(KeyOf(peerKey, direction), out var flow))
            {
                flow.Announced = false;
                flow.Completed = true;
                flow.Stopped ??= stopped;
                flow.Problem ??= problem;
            }
        }
    }

    /// <summary>
    /// Ends every flow with this peer now, marked with why - their connection ended, so nothing
    /// more is coming. Without this a transfer cut off halfway sat open for 90 seconds and was
    /// then written down as received.
    /// </summary>
    public static void Interrupt(string peerKey, string reason)
    {
        lock (Gate)
        {
            QueuedFlows.Remove(KeyOf(peerKey, TransferDirection.Download));
            QueuedFlows.Remove(KeyOf(peerKey, TransferDirection.Upload));
            foreach (var direction in new[] { TransferDirection.Download, TransferDirection.Upload })
            {
                if (!Flows.TryGetValue(KeyOf(peerKey, direction), out var flow) || flow.Completed) continue;
                flow.Announced = false;
                flow.Completed = true;
                flow.Stopped ??= reason;
            }
        }
    }

    /// <summary>
    /// The far side is checking files with us (/api/hash). Keeps their announced transfer open
    /// and marks it "Checking files" until the returned handle is disposed. Their check comes
    /// after the last byte and before their "finished", and moves no file data: without this
    /// a big transfer sat at 100% and was closed as "Nothing arrived for 90 seconds" while the
    /// two PCs were still comparing.
    /// </summary>
    public static CheckScope BeginCheck(string peerKey)
    {
        var touched = new List<PeerFlow>();
        lock (Gate)
        {
            foreach (var direction in new[] { TransferDirection.Download, TransferDirection.Upload })
            {
                if (!Flows.TryGetValue(KeyOf(peerKey, direction), out var flow) || !flow.Announced) continue;
                flow.ChecksInFlight++;
                flow.Checking = true;
                flow.LastUtc = DateTime.UtcNow;
                touched.Add(flow);
            }
        }
        return new CheckScope(touched);
    }

    /// <summary>One /api/hash request's hold on the flows it belongs to.</summary>
    public sealed class CheckScope : IDisposable
    {
        private readonly List<PeerFlow> flows;
        private int _done;

        internal CheckScope(List<PeerFlow> flows) => this.flows = flows;

        /// <summary>Bytes read for the check, so the row on this PC can show how far it has got.</summary>
        public void Read(long bytes)
        {
            if (flows.Count == 0) return;
            lock (Gate)
                foreach (var flow in flows) flow.CheckBytes += bytes;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (Gate)
            {
                foreach (var flow in flows)
                {
                    flow.ChecksInFlight = Math.Max(0, flow.ChecksInFlight - 1);
                    flow.LastUtc = DateTime.UtcNow;
                }
            }
        }
    }

    /// <summary>Ends one flow now (we cancelled it), marked with why.</summary>
    public static void Stop(string key, string reason)
    {
        lock (Gate)
        {
            if (!Flows.TryGetValue(key, out var flow)) return;
            flow.Announced = false;
            flow.Completed = true;
            flow.Stopped ??= reason;
            Stopped[key] = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        }
    }

    /// <summary>The peer a flow key belongs to ("Download:&lt;peerId&gt;").</summary>
    public static string PeerOf(string key) => key[(key.IndexOf(':') + 1)..];

    /// <summary>
    /// Records bytes moved on someone else's behalf.
    ///
    /// <paramref name="fileLength"/> is what the whole file weighs; once that much of it has
    /// gone by the file is counted as complete. The eight-stream engine asks for a large file
    /// as eight separate ranges, so counting a request as a file would report it eight times -
    /// and counting only whole-file requests, which is what used to happen, missed every large
    /// file entirely.
    /// </summary>
    public static void Add(string peerKey, string peerName, TransferDirection direction,
        string path, long fileLength, long bytes)
    {
        // An empty file still counts as a file: it has a name and it arrived. Without this,
        // pulling 400 files where three of them were zero bytes reported 397.
        var empty = bytes == 0 && fileLength == 0 && !string.IsNullOrEmpty(path);
        if (bytes <= 0 && !empty) return;

        if (direction == TransferDirection.Download) Interlocked.Add(ref _inBytes, bytes);
        else Interlocked.Add(ref _outBytes, bytes);

        string? finished = null;
        long finishedSize = 0;

        lock (Gate)
        {
            var key = KeyOf(peerKey, direction);
            if (!Flows.TryGetValue(key, out var flow))
            {
                if (Stopped.TryGetValue(key, out var until) && DateTime.UtcNow < until) return;
                flow = new PeerFlow { Key = key, PeerName = peerName, Direction = direction };
                Flows[key] = flow;
            }

            flow.LastUtc = DateTime.UtcNow;
            flow.Bytes += bytes;

            if (!string.IsNullOrEmpty(path))
            {
                var slash = path.LastIndexOf('/');
                flow.Folders.Add(slash <= 0 ? "" : path[..slash]);

                flow.Partial.TryGetValue(path, out var seen);
                seen += bytes;

                if (empty || (fileLength > 0 && seen >= fileLength))
                {
                    flow.Partial.Remove(path);
                    flow.Files++;
                    if (flow.Names.Count < PeerFlow.MaxNames) flow.Names.Add(path);

                    finished = path;
                    finishedSize = fileLength;
                }
                else
                {
                    flow.Partial[path] = seen;
                }
            }
        }

        // Outside the lock: this reaches the top-ten bookkeeping and the all-time counters.
        if (finished is not null)
            ActivityLog.File(direction, finished, finishedSize, 0, peerName);
    }

    private static FlowSnapshot Snapshot(PeerFlow flow) => Snapshot(flow, withNames: true);

    /// <summary>
    /// The copy the UI works from. Without the names it is a handful of numbers; with them it
    /// can be a hundred thousand strings - which the 250 ms tick used to copy, under the same
    /// lock every server request takes, only to rebuild a details list nobody was looking at.
    /// </summary>
    private static FlowSnapshot Snapshot(PeerFlow flow, bool withNames) => new(
        flow.Key,
        flow.PeerName,
        flow.Direction,
        flow.StartedUtc,
        Math.Max((flow.LastUtc - flow.StartedUtc).TotalSeconds, 0.001),
        flow.Bytes,
        flow.Files,
        flow.Folders.Count,
        flow.Total,
        flow.TotalFiles,
        withNames ? flow.Names.ToList() : [],
        withNames ? flow.Folders.ToList() : [],
        flow.PeerDrive,
        flow.Stopped,
        flow.Problem,
        flow.Checking,
        flow.CheckBytes);

    /// <summary>What is moving right now, for the status bar and the live rows. Counts only.</summary>
    public static List<FlowSnapshot> Active()
    {
        lock (Gate) return Flows.Values.Select(f => Snapshot(f, withNames: false)).ToList();
    }

    /// <summary>One flow in full, names included - for when its details are actually opened.</summary>
    public static FlowSnapshot? Find(string key)
    {
        lock (Gate) return Flows.TryGetValue(key, out var flow) ? Snapshot(flow, withNames: true) : null;
    }

    /// <summary>
    /// Closes flows that have gone quiet and hands them back so they can be logged once.
    ///
    /// Quiet rather than "finished" because there is no end marker to wait for: the far side
    /// simply stops asking. A couple of seconds is long enough to ride out the gap between two
    /// bundles and short enough that the line appears while it is still interesting.
    /// </summary>
    public static List<FlowSnapshot> Sweep(TimeSpan idle)
    {
        var done = new List<FlowSnapshot>();
        var now = DateTime.UtcNow;
        var cutoff = now - idle;

        lock (Gate)
        {
            foreach (var flow in Flows.Values.ToList())
            {
                // A declared transfer stays one batch: it closes when the far side says it is
                // done, not on a stall in the byte stream. The long fallback only catches a
                // peer that crashed mid-transfer and will never send its completion.
                //
                // A check in progress is the transfer still going (see BeginCheck). And when
                // the fallback does fire with every announced byte and file already here, it
                // arrived: the "finished" message is what went missing, not the files.
                bool close;
                if (flow.Completed) close = true;
                else if (flow.Announced)
                {
                    close = flow.ChecksInFlight == 0 && now - flow.LastUtc > TimeSpan.FromSeconds(90);
                    if (close && !flow.AllArrived) flow.Stopped ??= "Nothing arrived for 90 seconds";
                }
                else close = flow.LastUtc <= cutoff;

                if (!close) continue;
                Flows.Remove(flow.Key);
                done.Add(Snapshot(flow));
            }
        }

        return done;
    }

    /// <summary>Drops everything, for when sharing stops and nobody is connected any more.</summary>
    public static List<FlowSnapshot> Drain()
    {
        lock (Gate)
        {
            var all = Flows.Values.Select(Snapshot).ToList();
            Flows.Clear();
            return all;
        }
    }
}
