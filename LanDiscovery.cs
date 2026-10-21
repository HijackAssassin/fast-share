using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace FastShare;

/// <summary>
/// Finds a PC on the same local network by its code, so typing the code works in the same house.
///
/// A code is the PC's public internet address. Two PCs behind the same router share that address,
/// so dialling it means going out to the router and straight back in - slower, and many routers
/// refuse it outright. Instead, before dialling, the visitor asks the local network "is the PC with
/// this code here?" in one broadcast. A sharing FastShare whose own code matches answers, and the
/// visitor connects straight to its local address instead.
///
/// It is only a pointer. The answer carries nothing but a port; everything after it is the normal
/// connection - certificate pinning, identity, approval - so a false answer gains nobody anything
/// beyond a failed connection.
/// </summary>
public static class LanDiscovery
{
    private const string Ask = "FSD1?";
    private const string Answer = "FSD1!";

    /// <summary>"Who is sharing here?" (2.4.20-2.4.23). Answered with <see cref="Present"/>, sharing PCs only.</summary>
    private const string Roll = "FSD1*";
    private const string Present = "FSD1=";

    /// <summary>
    /// "Who is here?" (2.4.24). Every open FastShare answers with <see cref="Present2"/>:
    /// port, code (empty unless sharing), id, sharing 1/0, system, name - name last, so a
    /// stray line break in it cannot shift the other fields.
    /// </summary>
    private const string Roll2 = "FSD2*";
    private const string Present2 = "FSD2=";

    /// <summary>A FastShare found on this network by <see cref="ListAsync"/>.</summary>
    public sealed record LanPeer(string Host, int Port, string Code, string PeerId, string Name,
        bool Sharing = true, string? Os = null);

    /// <summary>What this copy says about itself when asked who is here.</summary>
    public sealed record Self(string PeerId, string Name, string Os);

    /// <summary>
    /// Listens on UDP <paramref name="port"/>. While <paramref name="currentCode"/> gives a code
    /// (we are sharing) it answers lookups for that code; with <paramref name="identity"/> it also
    /// answers "who is here?", sharing or not. Dispose to stop.
    /// </summary>
    public static IDisposable Respond(int port, Func<string?> currentCode, Func<Self>? identity = null)
    {
        var stop = new CancellationTokenSource();
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient { ExclusiveAddressUse = false };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (Exception ex)
        {
            Diagnostics.Log("lan", $"cannot listen for local lookups on UDP {port}", ex);
            udp?.Dispose();
            return stop;
        }

        var socket = udp;
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult message;
                try { message = await socket.ReceiveAsync(stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }   // e.g. an ICMP "port unreachable" echo

                if (message.Buffer.Length > 64) continue;
                var text = Encoding.ASCII.GetString(message.Buffer);

                // The code (with its secret) goes to anyone on this network who asks. They still
                // need approving; it only saves someone in the same house typing it.
                if ((text == Roll || text == Roll2) && identity is not null)
                {
                    var present = currentCode();
                    if (text == Roll && present is null) continue;   // the old question is for sharing PCs only
                    try
                    {
                        var self = identity();
                        var reply = Encoding.UTF8.GetBytes(text == Roll
                            ? $"{Present}{port}\n{present}\n{self.PeerId}\n{self.Name}"
                            : $"{Present2}{port}\n{present}\n{self.PeerId}\n{(present is null ? 0 : 1)}\n{self.Os}\n{self.Name}");
                        await socket.SendAsync(reply, message.RemoteEndPoint, stop.Token);
                    }
                    catch { /* the asker is gone; nothing to do */ }
                    continue;
                }

                if (!text.StartsWith(Ask, StringComparison.Ordinal)) continue;

                // "FSD1?<address>" or, from 2.4.18, "FSD1?<address>#<secret>". Two PCs behind one
                // router on the same port have the same address part, so a secret in the question
                // must match ours too - that is what tells them apart. A question without one
                // (older builds, or a plain code) is answered on the address alone, as before.
                var code = currentCode();
                var ours = code is null ? null : ShortCode.AddressPart(code);
                ShortCode.TryParse(code ?? "", out _, out _, out var ourSecret);
                var asked = text[Ask.Length..].Trim();
                var hash = asked.IndexOf('#');
                var askedAddress = hash < 0 ? asked : asked[..hash];
                var askedSecret = hash < 0 ? null : asked[(hash + 1)..];
                if (ours is null || !string.Equals(askedAddress, ours, StringComparison.Ordinal)) continue;
                if (askedSecret is { Length: > 0 } && !string.Equals(askedSecret, ourSecret, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var reply = Encoding.ASCII.GetBytes($"{Answer}{port}");
                    await socket.SendAsync(reply, message.RemoteEndPoint, stop.Token);
                }
                catch { /* the asker is gone; nothing to do */ }
            }
        });

        return new Stopper(stop, socket);
    }

    /// <summary>
    /// Asks the local network for the PC with this code. Returns its local address and port, or
    /// null if nobody answered within <paramref name="timeout"/> (not on this network, not
    /// sharing, or an older FastShare).
    /// </summary>
    public static async Task<(string Host, int Port)?> FindAsync(string addressPart, int port, TimeSpan timeout) =>
        (await FindAllAsync(addressPart, null, port, timeout)).FirstOrDefault() is { Host: not null } first ? first : null;

    /// <summary>
    /// Every other PC on this network answering to the code, within <paramref name="timeout"/>.
    /// With a secret, asks with it first (only that exact code answers); if nobody does, asks
    /// once more without it, which reaches FastShare 2.4.11-2.4.17 hosts that only know addresses.
    /// </summary>
    public static async Task<List<(string Host, int Port)>> FindAllAsync(string addressPart, string? secret,
        int port, TimeSpan timeout)
    {
        var found = await AskAsync(Ask + addressPart + (string.IsNullOrEmpty(secret) ? "" : "#" + secret), port, timeout);
        if (found.Count == 0 && !string.IsNullOrEmpty(secret))
            found = await AskAsync(Ask + addressPart, port, TimeSpan.FromMilliseconds(Math.Min(400, timeout.TotalMilliseconds)));
        return found;
    }

    private static async Task<List<(string Host, int Port)>> AskAsync(string question, int port, TimeSpan timeout)
    {
        var own = OwnAddresses();
        var found = new List<(string Host, int Port)>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            var ask = Encoding.ASCII.GetBytes(question);

            foreach (var target in BroadcastTargets())
            {
                try { await udp.SendAsync(ask, new IPEndPoint(target, port)); } catch { }
            }

            using var deadline = new CancellationTokenSource(timeout);
            while (true)
            {
                UdpReceiveResult reply;
                try { reply = await udp.ReceiveAsync(deadline.Token); }
                catch (OperationCanceledException) { return found; }
                catch (SocketException) { continue; }

                var text = Encoding.ASCII.GetString(reply.Buffer);
                if (!text.StartsWith(Answer, StringComparison.Ordinal) ||
                    !int.TryParse(text[Answer.Length..], out var answeredPort) || answeredPort is < 1 or > 65535)
                    continue;

                // Our own FastShare answering for our own code: that is not the PC being looked for.
                var from = reply.RemoteEndPoint.Address.MapToIPv4();
                if (own.Contains(from)) continue;

                // Broadcast to several addresses can bring the same answer back more than once.
                if (!found.Exists(f => f.Host == from.ToString())) found.Add((from.ToString(), answeredPort));
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("lan", "local lookup failed", ex);
            return found;
        }
    }

    /// <summary>
    /// Every other FastShare on this network listening on one of <paramref name="ports"/>,
    /// collected for <paramref name="timeout"/>: open ones from 2.4.24 (sharing or not), and
    /// sharing ones from 2.4.20-2.4.23, which only know the older question.
    /// </summary>
    public static async Task<List<LanPeer>> ListAsync(IEnumerable<int> ports, string ownPeerId, TimeSpan timeout)
    {
        var found = new List<LanPeer>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            var questions = new[] { Encoding.ASCII.GetBytes(Roll2), Encoding.ASCII.GetBytes(Roll) };
            var targets = BroadcastTargets();
            var home = HomeNetworks();

            foreach (var port in ports.Distinct())
                foreach (var target in targets)
                    foreach (var ask in questions)
                    {
                        try { await udp.SendAsync(ask, new IPEndPoint(target, port)); } catch { }
                    }

            using var deadline = new CancellationTokenSource(timeout);
            while (true)
            {
                UdpReceiveResult reply;
                try { reply = await udp.ReceiveAsync(deadline.Token); }
                catch (OperationCanceledException) { return found; }
                catch (SocketException) { continue; }

                if (reply.Buffer.Length > 1024) continue;
                var text = Encoding.UTF8.GetString(reply.Buffer);
                var from = reply.RemoteEndPoint.Address.MapToIPv4().ToString();
                if (Parse(text, from) is not { } peer) continue;

                // Ourselves, known by identity rather than address: another copy on this same PC
                // is still another FastShare.
                if (string.Equals(peer.PeerId, ownPeerId, StringComparison.OrdinalIgnoreCase)) continue;

                // One PC can answer once per network it shares with us - the house network and
                // VPN adapters such as Radmin (26.x, 100 Mbit/s) or ProtonVPN (10.2.0.x) - and a
                // 2.4.24 copy answers both questions. The house network's address wins, and the
                // newer answer (which says whether it is sharing, and its system) is kept.
                var same = found.FindIndex(f => f.PeerId == peer.PeerId);
                if (same < 0) { found.Add(peer); continue; }

                var old = found[same];
                var host = Rank(peer.Host, home) > Rank(old.Host, home) ? peer.Host : old.Host;
                var richer = peer.Os is not null ? peer : old;
                found[same] = richer with { Host = host };
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("lan", "listing this network failed", ex);
            return found;
        }
    }

    /// <summary>One answer to either question, or null if it is not one.</summary>
    private static LanPeer? Parse(string text, string from)
    {
        var modern = text.StartsWith(Present2, StringComparison.Ordinal);
        if (!modern && !text.StartsWith(Present, StringComparison.Ordinal)) return null;

        var parts = text[(modern ? Present2 : Present).Length..].Split('\n');
        if (parts.Length < (modern ? 6 : 4) || !int.TryParse(parts[0], out var port) || port is < 1 or > 65535) return null;

        var code = parts[1].Trim();
        var sharing = modern ? parts[3] == "1" : true;
        if (sharing && ShortCode.AddressPart(code) is null) return null;

        var name = string.Join(" ", parts[(modern ? 5 : 3)..]).Trim();
        return new LanPeer(from, port, sharing ? code : "", parts[2].Trim(),
            name.Length == 0 ? from : name[..Math.Min(name.Length, 64)],
            sharing, modern && parts[4].Trim().Length > 0 ? parts[4].Trim()[..Math.Min(parts[4].Trim().Length, 32)] : null);
    }

    /// <summary>2: on a private network of ours that has a router (the house network); 1: private; 0: other.</summary>
    private static int Rank(string host, List<(uint Net, uint Mask)> home)
    {
        if (!ShortCode.IsPrivate(host) || !IPAddress.TryParse(host, out var ip)) return 0;
        var value = ToUInt(ip);
        return home.Exists(n => (value & n.Mask) == n.Net) ? 2 : 1;
    }

    /// <summary>Our private IPv4 networks with a real default gateway - not VPN or VM adapters.</summary>
    private static List<(uint Net, uint Mask)> HomeNetworks()
    {
        var list = new List<(uint, uint)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                     !g.Address.Equals(IPAddress.Any))) continue;

                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                    if (!ShortCode.IsPrivate(unicast.Address.ToString())) continue;
                    var mask = ToUInt(unicast.IPv4Mask);
                    list.Add((ToUInt(unicast.Address) & mask, mask));
                }
            }
        }
        catch { }
        return list;
    }

    private static uint ToUInt(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    /// <summary>The all-ones broadcast plus each local network's own broadcast address.</summary>
    private static List<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | ~mask[i]);
                    var target = new IPAddress(broadcast);
                    if (!targets.Contains(target)) targets.Add(target);
                }
            }
        }
        catch { /* the all-ones broadcast alone usually does it */ }
        return targets;
    }

    private static HashSet<IPAddress> OwnAddresses()
    {
        var set = new HashSet<IPAddress> { IPAddress.Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork) set.Add(unicast.Address);
        }
        catch { }
        return set;
    }

    private sealed class Stopper(CancellationTokenSource stop, UdpClient socket) : IDisposable
    {
        public void Dispose()
        {
            try { stop.Cancel(); } catch { }
            socket.Dispose();
            stop.Dispose();
        }
    }
}
