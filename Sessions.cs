using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FastShare;

/// <summary>
/// Who is currently allowed in, and what they may do.
///
/// This replaces the old model where holding the code meant full access. Codes are now short
/// and guessable on purpose, so the approval prompt is the real security boundary: an unknown
/// peer gets nothing until you say yes, and what you grant is enforced here on the host rather
/// than hidden in the other side's UI.
/// </summary>
public sealed class SessionStore
{
    public sealed class PendingApproval
    {
        public required string RequestId { get; init; }
        public required string PeerId { get; init; }
        public required string Username { get; init; }
        public required string RemoteAddress { get; init; }

        /// <summary>What they run on, if their build says (2.4.23+).</summary>
        public string? Os { get; init; }

        /// <summary>Last answer given to this peer, used to pre-select the switches.</summary>
        public Permissions Suggested { get; init; } = Permissions.ReadOnly;

        /// <summary>Whether they proved the identity they claim. Shown on the prompt.</summary>
        public IdentityCheck Identity { get; init; } = IdentityCheck.Unverified;

        /// <summary>The key they proved, if any - remembered on approval.</summary>
        public string? PublicKey { get; init; }

        /// <summary>Every folder currently shared, so the prompt can offer them individually.</summary>
        public IReadOnlyList<string> AvailableShares { get; init; } = [];
        public DateTime RaisedUtc { get; } = DateTime.UtcNow;

        /// <summary>
        /// When the requester last asked for the answer. They poll every 0.7 s while their
        /// "Connecting…" box is up; if that stops, they have given up and the prompt can go.
        /// </summary>
        public DateTime LastPolledUtc { get; set; } = DateTime.UtcNow;

        public bool Abandoned => DateTime.UtcNow - LastPolledUtc > TimeSpan.FromSeconds(15);

        /// <summary>Null once denied; set to the grant once approved.</summary>
        public SessionGrant? Grant { get; set; }
        public bool Decided { get; set; }

        /// <summary>Why it was denied, when it was. Read back by the far side for its message.</summary>
        public string? DenyReason { get; set; }
    }

    public sealed class Session
    {
        public required string Token { get; init; }
        public required string PeerId { get; init; }
        public required string Username { get; init; }
        public required Permissions Permissions { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Round trip to this peer, as THEY measured it and reported back on their ping.
        ///
        /// We host and they connect, so we never get to time a request to them ourselves -
        /// HTTP only lets them ask us. But they time their own pings to us, and that figure is
        /// the same round trip, so they carry it back on the next ping and we show it. Null
        /// until the first one arrives.
        /// </summary>
        public double? ReportedRttMs { get; set; }

        /// <summary>
        /// A local name for this person, set on this machine only. Never sent anywhere and
        /// never seen by them - it is purely so a list of peers can read as "Dave" rather
        /// than whatever they happened to call their PC.
        /// </summary>
        public string? Nickname { get; set; }

        /// <summary>The nickname if one was given, otherwise the name they reported.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? Username : Nickname!;

        /// <summary>
        /// This PC talking to itself. Hidden from the "connected to you" displays - being told
        /// you are connected to yourself is noise, not information.
        /// </summary>
        public bool IsLoopback { get; init; }

        /// <summary>
        /// A session created for someone browsing us back over a tunnel they cannot ping,
        /// because their own port is shut. It is not swept for going idle (there is no ping to
        /// go idle on) and its lifetime is managed by the tunnel instead.
        /// </summary>
        public bool IsReverse { get; init; }

        /// <summary>Where they connected from, so their code can be recognised later.</summary>
        public string RemoteAddress { get; init; } = "";

        /// <summary>
        /// Cancelled the moment this session ends, for whatever reason.
        ///
        /// This is what makes a disconnect instant. The far side parks a request on /api/watch
        /// which completes as soon as this fires, so they hear about it in milliseconds instead
        /// of waiting for their next four-second ping to fail - which, with its own timeout on
        /// top, took the best part of ten seconds to notice.
        /// </summary>
        public CancellationTokenSource Ended { get; } = new();

        /// <summary>
        /// Why it ended, when that is worth telling them: "shutdown" when sharing was stopped
        /// deliberately, "closed" when the app is closing. Carried on the watch reply so the far
        /// side can say the right thing. Per session rather than a store-wide flag, which stayed
        /// set after a stop/start and made every later kick read as "stopped sharing".
        /// </summary>
        public string? EndReason { get; private set; }

        public void End(string? reason = null)
        {
            EndReason ??= reason;
            try { Ended.Cancel(); } catch { }
        }

        /// <summary>
        /// Fires whenever this peer's permissions or folder list change, so their screen
        /// catches up in milliseconds rather than on their next four-second ping.
        ///
        /// Enforcement here was always immediate - the middleware reads these per request -
        /// but their UI went on offering things that would be refused, which looks like the
        /// app is broken rather than like permission being withdrawn.
        /// </summary>
        public CancellationTokenSource Refreshed { get; private set; } = new();

        public void NotifyChanged()
        {
            var previous = Refreshed;
            Refreshed = new CancellationTokenSource();

            try { previous.Cancel(); } catch { }
            previous.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();
    private readonly ConcurrentDictionary<string, Session> _byToken = new();
    private readonly ConcurrentDictionary<string, Session> _byPeer = new();

    /// <summary>Peers turned away this run. Cleared on restart, exactly as a reject should be.</summary>
    private readonly HashSet<string> _rejected = [];
    private readonly Lock _gate = new();

    /// <summary>Supplied by the app so the store can consult the persisted block list.</summary>
    public Func<string, bool> IsBlocked { get; set; } = _ => false;

    /// <summary>The folders currently on offer, listed in the approval prompt.</summary>
    public Func<IReadOnlyList<string>> AvailableShares { get; set; } = () => [];

    /// <summary>Remembered answer for a peer we have approved before, if any.</summary>
    public Func<string, Permissions?> RememberedPermissions { get; set; } = _ => null;

    /// <summary>The public key on record for a peer, if they have proved one before.</summary>
    public Func<string, string?> KnownKey { get; set; } = _ => null;

    /// <summary>Whether the user said this peer may connect without a prompt.</summary>
    public Func<string, bool> AutoApproves { get; set; } = _ => false;

    /// <summary>Raised when a peer was let in without a prompt, so the UI can say so.</summary>
    public event Action<PendingApproval>? AutoApproved;

    // ---- identity challenges -------------------------------------------------------------

    private readonly ConcurrentDictionary<string, DateTime> _challenges = new();

    /// <summary>A single-use value for a connecting peer to sign. Expires after two minutes.</summary>
    public string IssueChallenge()
    {
        var now = DateTime.UtcNow;
        foreach (var (old, issued) in _challenges)
        {
            if (now - issued > TimeSpan.FromMinutes(2)) _challenges.TryRemove(old, out _);
        }

        // Bounded: anyone can ask for these, so a flood must not grow the table without end.
        if (_challenges.Count > 1000) _challenges.Clear();

        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _challenges[nonce] = now;
        return nonce;
    }

    /// <summary>
    /// Checks a request's proof: a challenge we issued (used up here, so it cannot be replayed)
    /// signed for our own certificate. Returns the proven public key, or null.
    /// </summary>
    public string? VerifiedKeyOf(SessionRequest request, string hostFingerprint)
    {
        if (request.PublicKey is not { Length: > 0 } key ||
            request.Signature is not { Length: > 0 } signature ||
            request.Nonce is not { Length: > 0 } nonce)
        {
            return null;
        }

        if (!_challenges.TryRemove(nonce, out var issued) || DateTime.UtcNow - issued > TimeSpan.FromMinutes(2))
            return null;

        return PeerIdentity.Verify(key, signature, request.PeerId, nonce, hostFingerprint) ? key : null;
    }

    /// <summary>Raised when a peer needs a decision. The UI subscribes and shows the prompt.</summary>
    public event Action<PendingApproval>? ApprovalNeeded;

    /// <summary>Raised whenever the set of connected peers changes, so the UI can show it.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when a session ends for good - kicked, blocked, expired, left, or everyone
    /// dropped at once - but not when a reconnect replaces it. The server uses it to close any
    /// tunnel that session had opened, which otherwise outlived the permission it rode on.
    /// </summary>
    public event Action<Session>? SessionEnded;

    public IReadOnlyList<Session> Active => _byToken.Values.ToList();

    /// <summary>
    /// Handles a connection attempt. Denied outright for a blocked or already-refused peer,
    /// approved immediately only for loopback, and otherwise pending while the prompt sits on
    /// screen waiting for an answer.
    /// </summary>
    /// <summary>The secret this PC's code currently ends in, or null for a plain code.</summary>
    public Func<string?> CodeSecret { get; set; } = () => null;

    /// <summary>This PC's current code, handed to everyone approved so their saved entry keeps up.</summary>
    public Func<string?> CurrentCode { get; set; } = () => null;

    public SessionStatus Request(SessionRequest request, string remoteAddress, out string requestId,
        string? verifiedKey = null)
    {
        requestId = "";

        if (IsBlocked(request.PeerId))
            return new SessionStatus(SessionStates.Denied, null, DenyReasons.Blocked);

        lock (_gate)
        {
            if (_rejected.Contains(request.PeerId))
                return new SessionStatus(SessionStates.Denied, null, DenyReasons.Kicked);
        }

        // Loopback is this machine talking to itself - the localhost self-test should not
        // require approving your own PC.
        if (IsLoopback(remoteAddress))
            return new SessionStatus(SessionStates.Approved,
                Grant(request, Permissions.All, loopback: true, remoteAddress));

        // Every other connection prompts, every time.
        //
        // This used to short-circuit for a peer we had approved before, or one that still held
        // a live session. Both were silent: someone could connect after you restarted the app
        // and you would see nothing at all - no prompt, no log line, no sign anyone was there.
        // Knowing who is on your machine matters more than saving a click, so a previous
        // answer now only pre-selects the switches.
        //
        // An existing session for the same peer is NOT dropped here any more. It is replaced
        // when the new request is approved (see Grant). Dropping it on request meant anyone
        // who knew a peer's id could knock that peer off just by asking to connect.
        SweepPending();

        // One waiting prompt per address. Without this anyone could stack up popups faster
        // than they can be answered. The same peer asking again just rejoins its own request.
        foreach (var waiting in _pending.Values)
        {
            if (waiting.Decided || waiting.Abandoned || waiting.RemoteAddress != remoteAddress) continue;

            if (waiting.PeerId == request.PeerId)
            {
                requestId = waiting.RequestId;
                return new SessionStatus(SessionStates.Pending, null);
            }
            return new SessionStatus(SessionStates.Denied, null, DenyReasons.Busy);
        }

        // Does the claimed PeerId check out? Once a key is on record for it, only that key will
        // do: anyone else using the id gets a warning and a stranger's defaults, not the real
        // peer's name and remembered permissions.
        var onRecord = KnownKey(request.PeerId);
        var identity =
            onRecord is null ? (verifiedKey is null ? IdentityCheck.Unverified : IdentityCheck.New)
            : string.Equals(onRecord, verifiedKey, StringComparison.Ordinal) ? IdentityCheck.Verified
            : IdentityCheck.Mismatch;

        Diagnostics.Log("identity", $"{ResolveReportedName(request)} ({request.PeerId[..Math.Min(8, request.PeerId.Length)]}…) from {remoteAddress}: {identity}");

        // "New code" retires the old one: anyone using it - including people approved before -
        // is turned away before a request reaches the screen, until they are given the new one.
        var secret = CodeSecret();
        if (!string.IsNullOrEmpty(secret) &&
            !string.Equals(secret, request.Code, StringComparison.OrdinalIgnoreCase))
        {
            Diagnostics.Log("identity", $"refused {ResolveReportedName(request)} from {remoteAddress}: old or missing code");
            return new SessionStatus(SessionStates.Denied, null, DenyReasons.CodeChanged);
        }

        // "Allow without requesting" - only for the proven owner of the key on record, and only
        // with permissions they were actually given before.
        if (identity == IdentityCheck.Verified && AutoApproves(request.PeerId) &&
            RememberedPermissions(request.PeerId) is { } remembered)
        {
            var auto = new PendingApproval
            {
                Identity = identity,
                PublicKey = verifiedKey,
                RequestId = "auto",
                PeerId = request.PeerId,
                Username = ResolveReportedName(request),
                RemoteAddress = remoteAddress,
                Os = request.Os,
                Suggested = remembered,
            };
            var granted = Grant(request, remembered, loopback: false, remoteAddress);
            AutoApproved?.Invoke(auto);
            return new SessionStatus(SessionStates.Approved, granted);
        }

        var pending = new PendingApproval
        {
            Identity = identity,
            PublicKey = verifiedKey,
            RequestId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)),
            PeerId = request.PeerId,
            // Their chosen name if they set one, else their desktop name, else a placeholder.
            // The host's own nickname for them is layered on top later; this is only the
            // best the *peer* could tell us about themselves.
            Username = ResolveReportedName(request),
            RemoteAddress = remoteAddress,
            Os = request.Os,
            // Folders and the download switch are remembered; Send and Delete never are.
            // Those two let someone write to and destroy files on this machine, so they start
            // off every time and have to be turned on deliberately, not inherited from a
            // decision made weeks ago.
            // Offered back exactly as it was granted last time, write permissions and
            // all. Downgrading them on every reconnect meant re-ticking the same three
            // switches for the same person forever - and the prompt still appears every
            // time, so nothing gets in without being looked at.
            Suggested = identity == IdentityCheck.Mismatch
                ? Permissions.ReadOnly
                : RememberedPermissions(request.PeerId) ?? Permissions.ReadOnly,
            AvailableShares = AvailableShares(),
        };

        _pending[pending.RequestId] = pending;
        requestId = pending.RequestId;

        ApprovalNeeded?.Invoke(pending);
        return new SessionStatus(SessionStates.Pending, null);
    }

    /// <summary>
    /// Forgets prompts that are finished with: undecided ones older than the five minutes a
    /// requester will wait, and decided ones the requester never came back for.
    /// </summary>
    private void SweepPending()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, pending) in _pending)
        {
            var age = now - pending.RaisedUtc;
            if ((!pending.Decided && (age > PendingLifetime || pending.Abandoned)) ||
                (pending.Decided && age > PendingLifetime * 2))
            {
                if (_pending.TryRemove(id, out _) && !pending.Decided) PendingExpired?.Invoke(pending);
            }
        }
    }

    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Raised when an unanswered prompt times out, so the UI can close its dialog.</summary>
    public event Action<PendingApproval>? PendingExpired;

    /// <summary>Whether a prompt is still waiting to be answered.</summary>
    public bool IsPending(string requestId) =>
        _pending.TryGetValue(requestId, out var p) && !p.Decided && !p.Abandoned &&
        DateTime.UtcNow - p.RaisedUtc <= PendingLifetime;

    public SessionStatus Poll(string requestId)
    {
        if (!_pending.TryGetValue(requestId, out var pending))
            return new SessionStatus(SessionStates.Denied, null);

        pending.LastPolledUtc = DateTime.UtcNow;

        // Abandon prompts nobody answered, so a stale entry cannot linger forever.
        if (!pending.Decided && DateTime.UtcNow - pending.RaisedUtc > PendingLifetime)
        {
            if (_pending.TryRemove(requestId, out _)) PendingExpired?.Invoke(pending);
            return new SessionStatus(SessionStates.Denied, null);
        }

        if (!pending.Decided) return new SessionStatus(SessionStates.Pending, null);

        _pending.TryRemove(requestId, out _);
        return pending.Grant is null
            ? new SessionStatus(SessionStates.Denied, null, pending.DenyReason ?? DenyReasons.Declined)
            : new SessionStatus(SessionStates.Approved, pending.Grant);
    }

    /// <summary>False if the request had already timed out, so the UI can say so.</summary>
    public bool Approve(string requestId, Permissions permissions)
    {
        if (!_pending.TryGetValue(requestId, out var pending) || pending.Decided || !IsPending(requestId)) return false;

        pending.Grant = Grant(new SessionRequest(pending.PeerId, pending.Username), permissions,
            loopback: false, pending.RemoteAddress);
        pending.Decided = true;
        return true;
    }

    /// <summary>Turn a peer away. Rejection lasts until restart; blocking is the caller's job to persist.</summary>
    public void Deny(string requestId, bool rememberForThisRun, string reason = DenyReasons.Declined)
    {
        if (!_pending.TryGetValue(requestId, out var pending)) return;

        pending.Grant = null;
        pending.Decided = true;
        pending.DenyReason = reason;

        if (rememberForThisRun)
        {
            lock (_gate) _rejected.Add(pending.PeerId);
        }
    }

    /// <summary>
    /// Changes what a connected peer may do, taking effect on their very next request. Nothing
    /// is torn down: the session and its tunnel stay up, they simply have different rights.
    /// </summary>
    public bool UpdatePermissions(string peerId, Permissions permissions)
    {
        if (!_byPeer.TryGetValue(peerId, out var session)) return false;

        session.Permissions = permissions;
        session.NotifyChanged();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Records that someone we are connected to is now browsing us back over their tunnel, so
    /// they appear in our connected-people list and their access is governed by a real
    /// permission set we can edit - exactly like anyone who dialled in. Replaces any earlier
    /// session for them. Returns the session so the tunnel can read its live permissions.
    /// </summary>
    public Session CreateReverse(string peerId, string username, Permissions permissions)
    {
        if (_byPeer.TryGetValue(peerId, out var stale))
        {
            _byPeer.TryRemove(peerId, out _);
            _byToken.TryRemove(stale.Token, out _);
        }

        var session = new Session
        {
            Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
            PeerId = peerId,
            Username = string.IsNullOrWhiteSpace(username) ? "Unknown" : username,
            Permissions = permissions,
            IsReverse = true,
            RemoteAddress = "tunnel",
        };

        _byToken[session.Token] = session;
        _byPeer[session.PeerId] = session;
        Changed?.Invoke();
        return session;
    }

    /// <summary>The permissions of the reverse session for this peer, or null if there is none.</summary>
    public Permissions? ReversePermissions(string peerId) =>
        _byPeer.TryGetValue(peerId, out var s) && s.IsReverse ? s.Permissions : null;

    /// <summary>Drops a peer's session immediately, e.g. when they are blocked mid-connection.</summary>
    public void Revoke(string peerId, string? reason = null)
    {
        if (!_byPeer.TryRemove(peerId, out var session)) return;
        _byToken.TryRemove(session.Token, out _);
        session.End(reason);
        SessionEnded?.Invoke(session);
        Changed?.Invoke();
    }

    /// <summary>
    /// Ends every session at once. Used when the app closes so the far side loses access the
    /// moment the window goes, rather than lingering until its next request happens to fail.
    /// The reason ("shutdown", "closed") rides back on each peer's watch so their screen can
    /// say "stopped sharing" or "closed FastShare" rather than a bare disconnect.
    /// </summary>
    public void RevokeAll(string? reason = null)
    {
        if (_byToken.IsEmpty && _byPeer.IsEmpty) return;

        var ending = _byToken.Values.ToList();
        foreach (var session in ending) session.End(reason);

        _byToken.Clear();
        _byPeer.Clear();
        foreach (var session in ending) SessionEnded?.Invoke(session);
        Changed?.Invoke();
    }

    /// <summary>
    /// Drops sessions nobody has used recently.
    ///
    /// Nothing expired before, so a peer who closed their app - or lost power, or crashed -
    /// stayed listed as connected forever. A polite goodbye covers the tidy case; this covers
    /// every other one, because a session that has not been touched in twenty seconds when
    /// the far side pings every four is not coming back.
    /// </summary>
    public int ExpireStale(TimeSpan idleFor)
    {
        var cutoff = DateTime.UtcNow - idleFor;
        var gone = 0;

        foreach (var session in _byToken.Values.ToList())
        {
            if (session.IsLoopback || session.IsReverse || session.LastSeenUtc > cutoff) continue;

            Revoke(session.PeerId, DenyReasons.Lost);
            gone++;
        }

        return gone;
    }

    /// <summary>
    /// Ends this peer's session and refuses them for the rest of the run. Weaker than a block,
    /// which is persisted - a rejection is forgotten on restart, which is what makes it the
    /// safe answer when you are not sure.
    /// </summary>
    public void RejectPeer(string peerId)
    {
        lock (_gate) _rejected.Add(peerId);
        Revoke(peerId, DenyReasons.Kicked);
    }

    /// <summary>Ends a session by its own token, for a peer saying goodbye on the way out.</summary>
    public Session? EndByToken(string token, string? reason = null)
    {
        if (!_byToken.TryGetValue(token, out var session)) return null;

        Revoke(session.PeerId, reason);
        return session;
    }

    // ---- resuming an unfinished transfer ---------------------------------------

    /// <summary>
    /// A peer asking us something about a transfer with them: "resume" (may it resume?) or
    /// "space" (not enough room - Retry once space is freed, or Discard?).
    /// </summary>
    public sealed record ResumeAsk(string Id, string PeerId, string PeerName, string What, string Kind = "resume");

    /// <summary>Raised on a request thread; the window shows the question.</summary>
    public event Action<ResumeAsk>? ResumeAsked;

    /// <summary>Each question's state: "pending", "yes", "no", or "withdrawn" (the asker gave up).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _resumeAnswers = new();

    public string AskResume(string peerId, string peerName, string what, string kind = "resume")
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        _resumeAnswers[id] = "pending";
        ResumeAsked?.Invoke(new ResumeAsk(id, peerId, peerName, what, kind));
        return id;
    }

    public void AnswerResume(string id, bool yes)
    {
        if (_resumeAnswers.TryGetValue(id, out var state) && state == "pending") _resumeAnswers[id] = yes ? "yes" : "no";
    }

    /// <summary>The asker no longer wants an answer (they chose Discard); the question closes.</summary>
    public void WithdrawResume(string id)
    {
        if (_resumeAnswers.ContainsKey(id)) _resumeAnswers[id] = "withdrawn";
    }

    /// <summary>"yes", "no", "pending", "withdrawn", or "unknown" for an id we never issued.</summary>
    public string ResumeState(string id) => _resumeAnswers.TryGetValue(id, out var state) ? state : "unknown";

    // ---- cancelling someone else's transfer ----------------------------------

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _cancelAsked =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _refuseFrom =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// We want this peer to stop the transfer they are running against us. They hear it on their
    /// next ping (2.4.25+) and cancel it themselves, which is the clean way: their undo runs and
    /// their screen says who cancelled. As a backstop for older builds, their transfer requests
    /// are refused from 8 seconds after asking - long enough for a newer peer to have stopped on
    /// its own - for 20 seconds.
    /// </summary>
    public void AskCancel(string peerId)
    {
        _cancelAsked[peerId] = true;
        _refuseFrom[peerId] = DateTime.UtcNow + TimeSpan.FromSeconds(8);
    }

    /// <summary>True once per request, for the ping that carries it.</summary>
    public bool WantsCancel(string peerId) => _cancelAsked.TryRemove(peerId, out _);

    /// <summary>Whether this peer's transfer requests are being turned away after a cancel.</summary>
    public bool RefusesTransfers(string peerId)
    {
        if (!_refuseFrom.TryGetValue(peerId, out var from)) return false;
        var now = DateTime.UtcNow;
        if (now > from + TimeSpan.FromSeconds(20)) { _refuseFrom.TryRemove(peerId, out _); return false; }
        return now >= from;
    }

    public Session? Lookup(string token)
    {
        if (!_byToken.TryGetValue(token, out var session)) return null;
        if (IsBlocked(session.PeerId))
        {
            Revoke(session.PeerId);
            return null;
        }
        session.LastSeenUtc = DateTime.UtcNow;
        return session;
    }

    /// <summary>
    /// The best name the peer could offer about themselves: the name they chose in their own
    /// settings, or failing that their desktop name, or failing that a placeholder. The
    /// host's private nickname is applied on top of this elsewhere.
    /// </summary>
    private static string ResolveReportedName(SessionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Username)) return request.Username.Trim();
        if (!string.IsNullOrWhiteSpace(request.MachineName)) return request.MachineName!.Trim();
        return "Unknown";
    }

    private SessionGrant Grant(SessionRequest request, Permissions permissions, bool loopback = false,
        string remoteAddress = "")
    {
        var session = new Session
        {
            Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
            PeerId = request.PeerId,
            Username = ResolveReportedName(request),
            Permissions = permissions,
            IsLoopback = loopback,
            RemoteAddress = remoteAddress,
        };

        // Replaces any earlier session for the same peer; reconnecting must not leave two
        // tokens alive.
        if (_byPeer.TryGetValue(session.PeerId, out var stale))
        {
            _byToken.TryRemove(stale.Token, out _);
            stale.End();
        }

        _byToken[session.Token] = session;
        _byPeer[session.PeerId] = session;
        Changed?.Invoke();

        return new SessionGrant(session.Token, permissions, Environment.MachineName, loopback ? null : CurrentCode());
    }

    // ---- share-back requests -------------------------------------------------

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _shareBackAsked =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Asks a connected peer to offer their folders back.
    ///
    /// There is no way to push this to them - they connected to us, so we cannot open anything
    /// towards them. It rides on their next ping instead, which is a few seconds at most, and
    /// is cleared once delivered so it prompts them once rather than every four seconds.
    /// </summary>
    public bool AskForShareBack(string peerId)
    {
        if (!_byPeer.ContainsKey(peerId)) return false;

        _shareBackAsked[peerId] = true;
        return true;
    }

    /// <summary>Reads and clears the flag, so the request is delivered exactly once.</summary>
    public bool WantsShareBack(string peerId) => _shareBackAsked.TryRemove(peerId, out _);

    // ---- speed-test requests -------------------------------------------------
    // Same shape as share-back: the host cannot reach a peer that connected in, so the ask
    // rides on their next ping and they run the test against us.

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _speedTestAsked =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Asks every connected peer (not ourselves, not reverse sessions) to run a speed test. Returns how many.</summary>
    public int AskForSpeedTest()
    {
        var asked = 0;
        foreach (var session in Active.Where(s => !s.IsLoopback && !s.IsReverse))
        {
            _speedTestAsked[session.PeerId] = true;
            asked++;
        }
        return asked;
    }

    public bool WantsSpeedTest(string peerId) => _speedTestAsked.TryRemove(peerId, out _);

    private static bool IsLoopback(string address) =>
        address is "127.0.0.1" or "::1" or "localhost";
}
