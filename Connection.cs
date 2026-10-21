using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FastShare;

public enum ConnectionKind
{
    /// <summary>Our own shared folders, read off this disk. Not a connection, but browsed like one.</summary>
    Local,

    /// <summary>A host we opened a connection to.</summary>
    Outbound,

    /// <summary>Someone who connected to us and is sharing back down that same socket.</summary>
    Peer,
}

/// <summary>
/// One thing the Browse tab can be pointed at.
///
/// There used to be a single <c>_source</c>, which meant opening a second connection silently
/// replaced the first - connect to someone, then look at your own folders, and their files were
/// simply gone. Everything a browse session needs now lives here instead, one instance per
/// connection, so several can be open at once and switching between them restores exactly
/// where you were.
/// </summary>
public sealed class Connection : INotifyPropertyChanged
{
    /// <summary>Stable key. The peer id where there is one, otherwise host:port, or "local".</summary>
    public required string Id { get; init; }

    public required ConnectionKind Kind { get; init; }
    public required IFileSource Source { get; init; }

    /// <summary>Set for <see cref="ConnectionKind.Outbound"/>: the client and who it reached.</summary>
    public ApiClient? Api { get; init; }
    public ConnectionInfo? Info { get; set; }

    /// <summary>Set for <see cref="ConnectionKind.Peer"/>: the tunnel they opened to us.</summary>
    public PeerTunnel? Peer { get; init; }

    /// <summary>
    /// For a peer: a client whose requests go down their tunnel to their own FastShare, so we
    /// can upload, delete, rename and create there as their User Permissions allow. Null until
    /// they answer (an older build never does, and stays download-only).
    /// </summary>
    public ApiClient? WriteApi { get; set; }

    /// <summary>What uploads, deletes, renames and new folders go through, whichever way we are connected.</summary>
    public ApiClient? Writer => Api ?? WriteApi;

    /// <summary>What the far side currently allows us. Refreshed by every ping.</summary>
    public Permissions? Granted { get; set; }

    /// <summary>Last measured round trip, so the connected-to list can show a real figure.</summary>
    public double? LastPingMs { get; set; }

    /// <summary>Cancels the long-poll that waits for the far side to end this session.</summary>
    public CancellationTokenSource? Watch { get; set; }

    /// <summary>
    /// Pings in a row that have failed. One lost ping used to close the connection outright -
    /// and any transfer riding on it - even though the host keeps the session for twenty
    /// seconds and a Wi-Fi blip is over in two. It now takes several in a row.
    /// </summary>
    public int MissedPings { get; set; }

    /// <summary>The host said no to dev-mode diagnostics (its dev mode is off); stop sending.</summary>
    public bool DiagnosticsRefused { get; set; }

    /// <summary>
    /// This connection's top-level folders, cached so switching between open connections does
    /// not wait on a round trip. Refreshed by the sweep and whenever its share list changes.
    /// </summary>
    public List<string>? TopLevel { get; set; }

    /// <summary>The share names they last told us we may see, so a change can be spotted.</summary>
    public string[] VisibleShares { get; set; } = [];

    /// <summary>
    /// A share list that disagreed with the last one, held back until a second ping agrees.
    ///
    /// One odd reply used to be enough to announce that a folder had been withdrawn and eject
    /// the user from it. Requiring two in a row costs four seconds on a real change and makes
    /// a transient blip cost nothing at all.
    /// </summary>
    public string[]? PendingShares { get; set; }

    // ---- where the user was, kept per connection so switching back is lossless ----
    public string CurrentPath { get; set; } = "";
    public Stack<string> History { get; } = new();
    public Stack<string> Forward { get; } = new();

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; Raise(); Raise(nameof(Detail)); }
    }

    /// <summary>
    /// What can be written to: our own outbound connections, and our own shares over
    /// localhost (which is just this disk, so every operation is allowed there).
    /// </summary>
    public bool CanWrite => Kind is ConnectionKind.Outbound or ConnectionKind.Local ||
                            (Kind == ConnectionKind.Peer && WriteApi is not null);

    /// <summary>Home for our own folders, a person otherwise.</summary>
    public string Glyph => Kind == ConnectionKind.Local ? "" : "";

    public string Detail => Kind switch
    {
        ConnectionKind.Local => "your own shared folders",
        ConnectionKind.Peer => "sharing with you",
        _ => Info is null ? "connected" : $"{Info.Host}:{Info.Port}",
    };

    /// <summary>
    /// What closing this would actually do, spelled out. Stopping browsing a peer does not stop
    /// sharing TO them - those are two separate things riding the same socket, and conflating
    /// them meant "stop looking at their files" quietly cut off their access to ours.
    /// </summary>
    public string CloseVerb => Kind switch
    {
        ConnectionKind.Local => "Close my shares",
        _ => "Stop browsing",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
