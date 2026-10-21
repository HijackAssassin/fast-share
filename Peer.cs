using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace FastShare;

/// <summary>What one peer is allowed to do once you have approved them.</summary>
public sealed record Permissions(
    [property: JsonPropertyName("d")] bool Download,
    [property: JsonPropertyName("s")] bool Send,
    [property: JsonPropertyName("x")] bool Delete,
    /// <summary>Share names this peer may see. Null or empty means every shared folder.</summary>
    [property: JsonPropertyName("f")] List<string>? Shares = null,

    /// <summary>
    /// Rearranging what is already there: rename, new folder, new file, and moving things
    /// about. Separate from Send and Delete because it is neither - it adds nothing and
    /// removes nothing, but it can still make a shared folder unrecognisable.
    /// </summary>
    [property: JsonPropertyName("m")] bool Modify = false)
{
    public static Permissions ReadOnly => new(true, false, false);
    public static Permissions All => new(true, true, true, null, true);

    public string Summary
    {
        get
        {
            var parts = new List<string>(4);
            if (Download) parts.Add("download");
            if (Send) parts.Add("upload");
            if (Modify) parts.Add("modify");
            if (Delete) parts.Add("delete");
            return parts.Count == 0 ? "browse only" : string.Join(", ", parts);
        }
    }
}

/// <summary>A connecting peer asking to be let in.</summary>
/// <remarks>
/// Username and MachineName are sent separately so the host can apply its own priority when
/// choosing what to call them: a nickname the host set beats the name they chose for
/// themselves, which beats their bare desktop name. Conflating the two - sending the desktop
/// name as the username when none was set - meant the host could not tell "they picked this"
/// from "this is just their PC's name". MachineName is optional so codes from older builds,
/// which sent only the one field, still parse.
/// </remarks>
/// <remarks>
/// PublicKey, Nonce and Signature prove the PeerId: the sender signs a one-time challenge from
/// the host, bound to the host's certificate, with the private key only their install holds.
/// All three are optional so a request from an older build still parses - it simply cannot
/// prove anything, and the prompt says so.
/// </remarks>
public sealed record SessionRequest(
    [property: JsonPropertyName("id")] string PeerId,
    [property: JsonPropertyName("u")] string Username,
    [property: JsonPropertyName("m")] string? MachineName = null,
    [property: JsonPropertyName("k")] string? PublicKey = null,
    [property: JsonPropertyName("n")] string? Nonce = null,
    [property: JsonPropertyName("sig")] string? Signature = null,
    /// <summary>The secret from the code they typed (2.4.11+), checked against the host's own.</summary>
    [property: JsonPropertyName("c")] string? Code = null,
    /// <summary>What they run on, e.g. "Windows 11" (2.4.23+). Shown instead of their address.</summary>
    [property: JsonPropertyName("os")] string? Os = null);

/// <summary>How far a connecting peer's claimed identity can be trusted.</summary>
public enum IdentityCheck
{
    /// <summary>Proved it holds the key this PeerId used last time.</summary>
    Verified,

    /// <summary>Proved a key, and this PeerId has no key on record yet - first contact from this build.</summary>
    New,

    /// <summary>An older build that cannot prove anything, and no key on record to contradict it.</summary>
    Unverified,

    /// <summary>Claims a PeerId whose key is on record, but did not prove that key. Possibly an impostor.</summary>
    Mismatch,
}

/// <summary>What the host hands back once a request is approved.</summary>
public sealed record SessionGrant(
    [property: JsonPropertyName("t")] string Token,
    [property: JsonPropertyName("p")] Permissions Permissions,
    [property: JsonPropertyName("h")] string HostName,
    /// <summary>The host's current code (2.4.11+), so a saved entry follows it when it changes.</summary>
    [property: JsonPropertyName("c")] string? Code = null);

/// <summary>Polled by the connecting side while the host decides.</summary>
public sealed record SessionStatus(
    [property: JsonPropertyName("s")] string State,
    [property: JsonPropertyName("g")] SessionGrant? Grant,
    [property: JsonPropertyName("d")] string? Reason = null);

public static class SessionStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
}

/// <summary>
/// Why a connection was turned away, so the far side can say the right thing rather than a
/// blanket "declined". Optional on the wire, so older builds simply ignore it.
/// </summary>
public static class DenyReasons
{
    public const string Declined = "declined";
    public const string Kicked = "kicked";
    public const string Blocked = "blocked";

    /// <summary>Someone else on the same network already has a request waiting.</summary>
    public const string Busy = "busy";

    /// <summary>The code they used has been replaced with a new one (and their PC is not one approved before).</summary>
    public const string CodeChanged = "code-changed";

    /// <summary>What the host sends on the watch when the app itself is closing.</summary>
    public const string Closed = "closed";

    /// <summary>What the host sends on the watch when sharing is stopped on purpose.</summary>
    public const string Shutdown = "shutdown";

    // Why a session ended on the far side's account (2.4.25), for "connection closed" tags.
    // Unknown to older clients, which read them as a plain disconnect.

    /// <summary>We chose "Stop sharing with them".</summary>
    public const string Removed = "removed";

    /// <summary>They closed FastShare (goodbye sent on the way out).</summary>
    public const string PeerClosed = "peer-closed";

    /// <summary>They disconnected from us but kept FastShare open.</summary>
    public const string PeerLeft = "peer-left";

    /// <summary>Nothing from their PC for 20 seconds: powered off, network gone, or crashed.</summary>
    public const string Lost = "lost";
}

/// <summary>
/// This installation's stable identity.
///
/// Codes are short and therefore guessable by design, so the identity is what block lists and
/// remembered permissions key on - not the address, which changes, and not the code, which is
/// public. Generated once and kept in config.
/// </summary>
public sealed class PeerIdentity
{
    public required string Id { get; init; }
    public required string Username { get; init; }

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// A new signing key for this install (ECDSA P-256), as the base64 private key kept in
    /// config. The PeerId stays the same random value it always was - everything already
    /// keyed on it on both machines keeps working - and the key is what proves it from now on.
    /// </summary>
    public static string NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportPkcs8PrivateKey());
    }

    public static ECDsa LoadKey(string privateKey)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        return key;
    }

    public static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// What gets signed. The host's certificate fingerprint is part of it, so a signature made
    /// for one host is useless anywhere else: someone in the middle relaying a challenge from
    /// the real host would have to present the real host's certificate, which they cannot.
    /// </summary>
    private static byte[] Statement(string peerId, string nonce, string hostFingerprint) =>
        System.Text.Encoding.UTF8.GetBytes($"fastshare-session-v1|{peerId}|{nonce}|{hostFingerprint.ToLowerInvariant()}");

    public static string Sign(ECDsa key, string peerId, string nonce, string hostFingerprint) =>
        Convert.ToBase64String(key.SignData(Statement(peerId, nonce, hostFingerprint), HashAlgorithmName.SHA256));

    public static bool Verify(string publicKey, string signature, string peerId, string nonce, string hostFingerprint)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Statement(peerId, nonce, hostFingerprint),
                Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>A peer the user has permanently blocked. Listed in Settings.</summary>
public sealed class BlockedPeer
{
    public string PeerId { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTime BlockedUtc { get; set; }
}

/// <summary>A peer we have approved before, so we can offer the same answer again.</summary>
public sealed class KnownPeer
{
    public string PeerId { get; set; } = "";
    public string Username { get; set; } = "";
    public bool Download { get; set; } = true;
    public bool Send { get; set; }
    public bool Modify { get; set; }
    public bool Delete { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Folders granted last time. Null means all of them.</summary>
    public List<string>? Shares { get; set; }

    /// <summary>Local nickname for this peer. Stays on this machine; they never see it.</summary>
    public string? Nickname { get; set; }

    /// <summary>
    /// The public key this peer proved last time (base64). Once recorded, a request using this
    /// PeerId without proving the same key is flagged as a possible impostor rather than being
    /// greeted by name with their remembered permissions pre-ticked.
    /// </summary>
    public string? PublicKey { get; set; }

    /// <summary>
    /// Let them in without a prompt. Only honoured when they prove the key on record
    /// (<see cref="IdentityCheck.Verified"/>) - anyone else claiming their PeerId still gets
    /// the prompt, with a warning.
    /// </summary>
    public bool AutoApprove { get; set; }

    public Permissions ToPermissions() => new(Download, Send, Delete, Shares, Modify);

    public void Remember(Permissions granted)
    {
        Download = granted.Download;
        Send = granted.Send;
        Modify = granted.Modify;
        Delete = granted.Delete;
        Shares = granted.Shares;
        LastSeenUtc = DateTime.UtcNow;
    }
}

/// <summary>Reply to a handshake request or poll.</summary>
public sealed record SessionResponse(
    [property: JsonPropertyName("s")] string State,
    [property: JsonPropertyName("g")] SessionGrant? Grant,
    [property: JsonPropertyName("r")] string? RequestId,
    [property: JsonPropertyName("d")] string? Reason = null);
