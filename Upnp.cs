using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace FastShare;

/// <summary>
/// Asks the router to forward the share port automatically.
///
/// Hand-rolled rather than pulled from a library, because the whole protocol needed here is
/// three messages: a UDP multicast asking who is a gateway, an HTTP GET for that gateway's
/// device description, and one SOAP call to add the mapping. A dependency for that would be
/// more code to ship than the code itself.
///
/// It fails often and that is fine. Plenty of routers ship with UPnP disabled - Google Fiber
/// among them - so this is an attempt, never a guarantee. Whether the port is genuinely
/// reachable is settled by <see cref="Diagnostics.CheckPortAsync"/>, not by what the router
/// claims here.
/// </summary>
public static class Upnp
{
    private const string Multicast = "239.255.255.250";
    private const int SsdpPort = 1900;

    /// <summary>The two service types that can forward a port; either will do.</summary>
    private static readonly string[] WanServices =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    /// <summary>A gateway we can talk to: where to send SOAP, and which service to name.</summary>
    public sealed record Gateway(Uri ControlUrl, string ServiceType);

    public enum MapResult { NoGateway, Refused, Mapped }

    // =======================================================================
    // Discovery
    // =======================================================================

    /// <summary>
    /// Finds the router, or null if nothing answers. Broadcasts on every interface that has a
    /// gateway, since a PC with a VPN or a virtual switch has several and only one of them
    /// leads to the real router.
    /// </summary>
    public static async Task<Gateway?> DiscoverAsync(CancellationToken ct = default)
    {
        var request = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {Multicast}:{SsdpPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");

        var addresses = LocalAddresses();
        if (addresses.Count == 0)
        {
            Diagnostics.Log("upnp", "no interface has a gateway");
            return null;
        }

        // Every interface is searched at once. Done one after another, a PC with a VPN or a
        // virtual switch pays the full timeout per adapter - three of them turned a 3-second
        // check into 9 seconds of the user waiting.
        using var shared = CancellationTokenSource.CreateLinkedTokenSource(ct);
        shared.CancelAfter(TimeSpan.FromSeconds(3));

        var searches = addresses.Select(local => SearchAsync(local, request, shared.Token)).ToList();

        while (searches.Count > 0)
        {
            var finished = await Task.WhenAny(searches);
            searches.Remove(finished);

            Gateway? gateway;
            try { gateway = await finished; }
            catch { continue; }

            if (gateway is null) continue;

            Diagnostics.Log("upnp", $"gateway at {gateway.ControlUrl} ({gateway.ServiceType})");
            await shared.CancelAsync();   // stop the others; we have one
            return gateway;
        }

        Diagnostics.Log("upnp", "no gateway answered");
        return null;
    }

    /// <summary>Multicasts from one interface and waits for the first answer that is a gateway.</summary>
    private static async Task<Gateway?> SearchAsync(IPAddress local, byte[] request, CancellationToken ct)
    {
        try
        {
            using var socket = new UdpClient(new IPEndPoint(local, 0));
            await socket.SendAsync(request, request.Length,
                new IPEndPoint(IPAddress.Parse(Multicast), SsdpPort));

            while (!ct.IsCancellationRequested)
            {
                var reply = await socket.ReceiveAsync(ct);
                var location = LocationOf(Encoding.ASCII.GetString(reply.Buffer));
                if (location is null) continue;

                var gateway = await DescribeAsync(location, ct);
                if (gateway is not null) return gateway;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log("upnp", $"discovery on {local} failed", ex);
        }
        catch (OperationCanceledException) { }

        return null;
    }

    /// <summary>Every IPv4 address on an interface that has a default gateway.</summary>
    private static List<IPAddress> LocalAddresses()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var properties = adapter.GetIPProperties();
                if (properties.GatewayAddresses.Count == 0) continue;

                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                        found.Add(address.Address);
                }
            }
        }
        catch (Exception ex) { Diagnostics.Log("upnp", "could not enumerate interfaces", ex); }

        return found;
    }

    private static string? LocationOf(string response)
    {
        foreach (var line in response.Split("\r\n"))
        {
            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                return line["LOCATION:".Length..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Reads the gateway's device description and picks out the service that forwards ports.
    /// The control URL in it may be relative, so it is resolved against the description's own
    /// address rather than assumed absolute.
    /// </summary>
    private static async Task<Gateway?> DescribeAsync(string location, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out var descriptionUrl)) return null;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var xml = XDocument.Parse(await http.GetStringAsync(descriptionUrl, ct));

            XNamespace ns = "urn:schemas-upnp-org:device-1-0";

            foreach (var service in xml.Descendants(ns + "service"))
            {
                var type = service.Element(ns + "serviceType")?.Value;
                if (type is null || !WanServices.Contains(type)) continue;

                var control = service.Element(ns + "controlURL")?.Value;
                if (string.IsNullOrWhiteSpace(control)) continue;

                return new Gateway(new Uri(descriptionUrl, control), type);
            }
        }
        catch (Exception ex) { Diagnostics.Log("upnp", $"could not read {location}", ex); }

        return null;
    }

    // =======================================================================
    // Mapping
    // =======================================================================

    /// <summary>
    /// Asks the router to forward <paramref name="port"/> to this PC, TCP.
    ///
    /// The lease is left permanent (0). A timed lease sounds tidier, but a transfer running
    /// past the expiry would have the port pulled out from under it, and plenty of routers
    /// silently reject any non-zero lease anyway.
    /// </summary>
    public static async Task<MapResult> MapPortAsync(int port, CancellationToken ct = default)
    {
        var gateway = await DiscoverAsync(ct);
        if (gateway is null) return MapResult.NoGateway;

        var local = LocalAddressTowards(gateway.ControlUrl);
        if (local is null)
        {
            Diagnostics.Log("upnp", "no local address faces the gateway");
            return MapResult.Refused;
        }

        var body =
            $"<u:AddPortMapping xmlns:u=\"{gateway.ServiceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            "<NewProtocol>TCP</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort>" +
            $"<NewInternalClient>{local}</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            "<NewPortMappingDescription>FastShare</NewPortMappingDescription>" +
            "<NewLeaseDuration>0</NewLeaseDuration>" +
            "</u:AddPortMapping>";

        var ok = await SoapAsync(gateway, "AddPortMapping", body, ct);
        Diagnostics.Log("upnp", ok
            ? $"router accepted a mapping for TCP {port} -> {local}"
            : $"router refused a mapping for TCP {port}");

        return ok ? MapResult.Mapped : MapResult.Refused;
    }

    /// <summary>Removes a mapping we added. Best effort; a stale mapping is harmless.</summary>
    public static async Task RemovePortAsync(int port, CancellationToken ct = default)
    {
        var gateway = await DiscoverAsync(ct);
        if (gateway is null) return;

        var body =
            $"<u:DeletePortMapping xmlns:u=\"{gateway.ServiceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            "<NewProtocol>TCP</NewProtocol>" +
            "</u:DeletePortMapping>";

        await SoapAsync(gateway, "DeletePortMapping", body, ct);
    }

    private static async Task<bool> SoapAsync(Gateway gateway, string action, string body,
        CancellationToken ct)
    {
        try
        {
            var envelope =
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                $"<s:Body>{body}</s:Body></s:Envelope>";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", $"\"{gateway.ServiceType}#{action}\"");

            using var response = await http.PostAsync(gateway.ControlUrl, content, ct);
            if (response.IsSuccessStatusCode) return true;

            // The fault body says WHY, which is the difference between "UPnP is off" and
            // "something else already has that port".
            Diagnostics.Log("upnp",
                $"{action} returned {(int)response.StatusCode}: " +
                Summarise(await response.Content.ReadAsStringAsync(ct)));

            return false;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("upnp", $"{action} failed", ex);
            return false;
        }
    }

    /// <summary>Pulls the useful part out of a SOAP fault instead of logging a wall of XML.</summary>
    private static string Summarise(string fault)
    {
        try
        {
            var description = XDocument.Parse(fault).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;

            if (!string.IsNullOrWhiteSpace(description)) return description;
        }
        catch { }

        return fault.Length > 200 ? fault[..200] : fault;
    }

    /// <summary>
    /// Which of this PC's addresses the router would see. Found by opening a UDP socket
    /// towards it - no traffic is sent, but the OS picks the route, which is exactly the
    /// answer we need and far more reliable than guessing at the first adapter.
    /// </summary>
    private static IPAddress? LocalAddressTowards(Uri gateway)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(gateway.Host, gateway.Port == -1 ? 80 : gateway.Port);
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("upnp", "could not work out the local address", ex);
            return null;
        }
    }
}
