using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinDNLA.Dlna;

public sealed class SsdpService : IAsyncDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private const int MulticastPort = 1900;

    /// <summary>LG WebOS Photo/Video ignores UPnP servers whose SSDP SERVER lacks DLNADOC.</summary>
    public const string ServerToken = "Windows/10 UPnP/1.0 DLNADOC/1.50 WinDLNA/1.0";

    private static readonly string[] VirtualNicMarkers =
    [
        "virtualbox", "vmware", "hyper-v", "vethernet", "wsl", "docker",
        "vpn", "tap-windows", "tap0", "tun0", "wireguard", "tailscale",
        "zerotier", "bluetooth", "pseudo-interface", "wi-fi direct",
        "hosted network", "virtual adapter", "openvpn", "nordlynx",
        "anyconnect", "hamachi", "radmin", "npcap", "loopback"
    ];

    private readonly ILogger<SsdpService>? _logger;
    private readonly List<SsdpEndpoint> _senders = [];
    private UdpClient? _receiver;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _httpPort = 8200;
    private string _uuid = "";
    private string _friendlyName = "WinDLNA";
    private int _bootId = 1;
    private IReadOnlyCollection<string> _disabledAddresses = [];

    public SsdpService(ILogger<SsdpService>? logger = null) => _logger = logger;

    public void Configure(int httpPort, string uuid, string friendlyName, IReadOnlyCollection<string>? disabledAddresses = null)
    {
        _httpPort = httpPort;
        _uuid = uuid;
        _friendlyName = friendlyName;
        _disabledAddresses = disabledAddresses ?? [];
    }

    /// <summary>Legacy overload kept for callers that still pass a full LOCATION URL.</summary>
    public void Configure(string locationUrl, string uuid, string friendlyName)
    {
        _uuid = uuid;
        _friendlyName = friendlyName;
        if (Uri.TryCreate(locationUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
            _httpPort = uri.Port;
    }

    public async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts = new CancellationTokenSource();

        var binds = GetEnabledBinds(_disabledAddresses).ToList();
        if (binds.Count == 0)
        {
            _logger?.LogError("SSDP: no IPv4 interfaces to advertise on");
            return;
        }

        try
        {
            _receiver = CreateReceiveClient(binds);
            _logger?.LogInformation("SSDP receive bound 0.0.0.0:{Port} on {Count} interface(s)", MulticastPort, binds.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SSDP receive bind on 0.0.0.0:{Port} failed", MulticastPort);
        }

        foreach (var bind in binds)
        {
            try
            {
                var client = CreateSendClient(bind.Address);
                _senders.Add(new SsdpEndpoint(client, bind));
                _logger?.LogInformation("SSDP send {Nic} {IP}/{Mask} if={If}", bind.NicName, bind.Address, bind.Mask, bind.IfIndex);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SSDP send bind failed on {IP}", bind.Address);
            }
        }

        if (_receiver is null && _senders.Count == 0)
        {
            _logger?.LogError("SSDP not started — no sockets");
            return;
        }

        _loop = Task.WhenAll(
            ListenAsync(_cts.Token),
            NotifyLoopAsync(_cts.Token),
            NotifyBurstAsync(_cts.Token));
        _logger?.LogInformation("SSDP started port={Port} uuid={Uuid} name={Name}", _httpPort, _uuid, _friendlyName);
        await SendNotifyAsync("ssdp:alive").ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        try { await SendNotifyAsync("ssdp:byebye").ConfigureAwait(false); } catch { /* ignore */ }
        try { _cts?.Cancel(); } catch { /* ignore */ }
        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
            catch { /* ignore timeout */ }
        }

        foreach (var ep in _senders)
        {
            try { ep.Client.Dispose(); } catch { /* ignore */ }
        }
        _senders.Clear();
        if (_receiver is not null)
        {
            try { _receiver.Dispose(); } catch { /* ignore */ }
            _receiver = null;
        }
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    private UdpClient CreateReceiveClient(IReadOnlyList<SsdpBindAddress> binds)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.ExclusiveAddressUse = false;
        client.Client.ReceiveBufferSize = 64 * 1024;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        client.MulticastLoopback = false;
        try { client.Ttl = 4; } catch { /* ignore */ }

        var joined = new HashSet<int>();
        foreach (var bind in binds)
        {
            try
            {
                if (bind.IfIndex > 0 && joined.Add(bind.IfIndex))
                {
                    client.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(MulticastAddress, bind.IfIndex));
                    _logger?.LogInformation("SSDP IGMP join if={If} {Nic} {IP}", bind.IfIndex, bind.NicName, bind.Address);
                }
                else if (bind.IfIndex <= 0)
                {
                    client.JoinMulticastGroup(MulticastAddress, bind.Address);
                    _logger?.LogInformation("SSDP IGMP join via {IP} {Nic}", bind.Address, bind.NicName);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SSDP IGMP join failed {Nic} {IP}", bind.NicName, bind.Address);
            }
        }

        return client;
    }

    private static UdpClient CreateSendClient(IPAddress ip)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.ExclusiveAddressUse = false;
        client.Client.Bind(new IPEndPoint(ip, MulticastPort));
        client.MulticastLoopback = false;
        try { client.Ttl = 4; } catch { /* ignore */ }
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
        }
        catch { /* ignore */ }
        try
        {
            var iface = BitConverter.ToInt32(ip.GetAddressBytes(), 0);
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, iface);
        }
        catch { /* ignore */ }
        return client;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var recv = _receiver;
        if (recv is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await recv.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(result.Buffer);
                if (!text.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase))
                    continue;

                var st = ExtractHeader(text, "ST");
                if (st is null || !IsRelevantSearch(st))
                {
                    _logger?.LogDebug("SSDP M-SEARCH ignored ST={ST} from {Remote}", st, result.RemoteEndPoint);
                    continue;
                }

                var sender = ChooseSender(result.RemoteEndPoint.Address);
                if (sender is null)
                {
                    _logger?.LogWarning("SSDP M-SEARCH ST={ST} from {Remote} — no send socket", st, result.RemoteEndPoint);
                    continue;
                }

                _logger?.LogInformation(
                    "SSDP M-SEARCH ST={ST} from {Remote} reply LOCATION={Location}",
                    st, result.RemoteEndPoint, BuildLocation(sender.Bind.Address));

                // LG WebOS gives up quickly; do not wait the full MX seconds.
                var mx = ParseMx(text);
                if (mx > 0)
                {
                    var delayMs = Random.Shared.Next(0, 120);
                    if (delayMs > 0)
                        await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }

                var location = BuildLocation(sender.Bind.Address);
                foreach (var response in BuildSearchResponses(st, location))
                {
                    var bytes = Encoding.ASCII.GetBytes(response);
                    await sender.Client.SendAsync(bytes, bytes.Length, result.RemoteEndPoint).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SSDP receive error");
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private SsdpEndpoint? ChooseSender(IPAddress remote)
    {
        var same = _senders.Where(s => IsSameSubnet(s.Bind.Address, s.Bind.Mask, remote)).ToList();
        if (same.Count == 1) return same[0];
        if (same.Count > 1)
            return same.FirstOrDefault(s => IsRfc1918(s.Bind.Address)) ?? same[0];

        return _senders.FirstOrDefault(s => IsRfc1918(s.Bind.Address))
               ?? _senders.FirstOrDefault();
    }

    private async Task NotifyBurstAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            await SendNotifyAsync("ssdp:alive").ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
            await SendNotifyAsync("ssdp:alive").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task NotifyLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                await SendNotifyAsync("ssdp:alive").ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public Task AnnounceAliveAsync() => SendNotifyAsync("ssdp:alive");

    private async Task SendNotifyAsync(string nts)
    {
        if (_httpPort <= 0 || string.IsNullOrEmpty(_uuid) || _senders.Count == 0) return;

        _logger?.LogDebug("SSDP NOTIFY {Nts} via {Count} endpoints", nts, _senders.Count);

        var multicast = new IPEndPoint(MulticastAddress, MulticastPort);
        foreach (var ep in _senders)
        {
            var location = BuildLocation(ep.Bind.Address);
            var messages = new[]
            {
                BuildNotify("upnp:rootdevice", nts, location),
                BuildNotify($"uuid:{_uuid}", nts, location),
                BuildNotify("urn:schemas-upnp-org:device:MediaServer:1", nts, location),
                BuildNotify("urn:schemas-upnp-org:service:ContentDirectory:1", nts, location),
                BuildNotify("urn:schemas-upnp-org:service:ConnectionManager:1", nts, location)
            };

            foreach (var msg in messages)
            {
                var bytes = Encoding.ASCII.GetBytes(msg);
                try { await ep.Client.SendAsync(bytes, bytes.Length, multicast).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "SSDP NOTIFY failed via {IP}", ep.Bind.Address);
                }
            }
        }
    }

    private string BuildLocation(IPAddress ip) => $"http://{ip}:{_httpPort}/description.xml";

    internal string BuildNotify(string nt, string nts, string location)
    {
        var usn = nt.Equals($"uuid:{_uuid}", StringComparison.OrdinalIgnoreCase)
            ? $"uuid:{_uuid}"
            : $"uuid:{_uuid}::{nt}";
        return
            "NOTIFY * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "CACHE-CONTROL: max-age=1800\r\n" +
            $"LOCATION: {location}\r\n" +
            "NT: " + nt + "\r\n" +
            "NTS: " + nts + "\r\n" +
            $"SERVER: {ServerToken}\r\n" +
            $"USN: {usn}\r\n" +
            $"BOOTID.UPNP.ORG: {_bootId}\r\n" +
            "CONFIGID.UPNP.ORG: 1\r\n" +
            "\r\n";
    }

    internal IEnumerable<string> BuildSearchResponses(string st, string location)
    {
        if (st.Equals("ssdp:all", StringComparison.OrdinalIgnoreCase))
        {
            yield return FormatSearchResponse("upnp:rootdevice", $"uuid:{_uuid}::upnp:rootdevice", location);
            yield return FormatSearchResponse($"uuid:{_uuid}", $"uuid:{_uuid}", location);
            yield return FormatSearchResponse(
                "urn:schemas-upnp-org:device:MediaServer:1",
                $"uuid:{_uuid}::urn:schemas-upnp-org:device:MediaServer:1",
                location);
            yield return FormatSearchResponse(
                "urn:schemas-upnp-org:service:ContentDirectory:1",
                $"uuid:{_uuid}::urn:schemas-upnp-org:service:ContentDirectory:1",
                location);
            yield return FormatSearchResponse(
                "urn:schemas-upnp-org:service:ConnectionManager:1",
                $"uuid:{_uuid}::urn:schemas-upnp-org:service:ConnectionManager:1",
                location);
            yield break;
        }

        var usn = st.Equals($"uuid:{_uuid}", StringComparison.OrdinalIgnoreCase)
            ? $"uuid:{_uuid}"
            : $"uuid:{_uuid}::{st}";
        yield return FormatSearchResponse(st, usn, location);
    }

    internal string FormatSearchResponse(string st, string usn, string location) =>
        "HTTP/1.1 200 OK\r\n" +
        "CACHE-CONTROL: max-age=1800\r\n" +
        $"DATE: {DateTime.UtcNow:R}\r\n" +
        $"ST: {st}\r\n" +
        $"USN: {usn}\r\n" +
        "EXT:\r\n" +
        $"SERVER: {ServerToken}\r\n" +
        $"LOCATION: {location}\r\n" +
        $"BOOTID.UPNP.ORG: {_bootId}\r\n" +
        "CONFIGID.UPNP.ORG: 1\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n";

    internal bool IsRelevantSearch(string st)
    {
        if (st.Equals("ssdp:all", StringComparison.OrdinalIgnoreCase)) return true;
        if (st.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase)) return true;
        if (st.Equals($"uuid:{_uuid}", StringComparison.OrdinalIgnoreCase)) return true;
        if (st.Contains("MediaServer", StringComparison.OrdinalIgnoreCase)) return true;
        if (st.Contains("ContentDirectory", StringComparison.OrdinalIgnoreCase)) return true;
        if (st.Contains("ConnectionManager", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static string? ExtractHeader(string text, string name)
    {
        var prefix = name + ":";
        return text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..]
            .Trim();
    }

    private static int ParseMx(string text)
    {
        var mx = ExtractHeader(text, "MX");
        if (mx is null || !int.TryParse(mx, out var seconds) || seconds < 0) return 1;
        return Math.Clamp(seconds, 0, 5);
    }

    public static IEnumerable<IPAddress> GetLocalIPv4(IReadOnlyCollection<string>? disabledAddresses = null) =>
        GetEnabledBinds(disabledAddresses).Select(b => b.Address);

    /// <summary>All up IPv4 (except loopback), including virtual NICs — for the UI picker.</summary>
    public static IReadOnlyList<SsdpBindAddress> GetSelectableIPv4() =>
        EnumerateBindAddresses(skipVirtual: false).ToList();

    public static IReadOnlyList<SsdpBindAddress> GetEnabledBinds(IReadOnlyCollection<string>? disabledAddresses)
    {
        var all = GetSelectableIPv4();
        return FilterEnabled(all, disabledAddresses);
    }

    public static IReadOnlyList<SsdpBindAddress> FilterEnabled(
        IReadOnlyList<SsdpBindAddress> all,
        IReadOnlyCollection<string>? disabledAddresses)
    {
        if (all.Count == 0) return all;
        if (disabledAddresses is null || disabledAddresses.Count == 0)
            return all;
        var disabled = disabledAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.Where(b => !disabled.Contains(b.Address.ToString())).ToList();
    }

    public static IReadOnlyList<SsdpBindAddress> GetAdvertisableIPv4()
    {
        var preferred = EnumerateBindAddresses(skipVirtual: true).ToList();
        if (preferred.Count > 0) return preferred;
        return EnumerateBindAddresses(skipVirtual: false).ToList();
    }

    public static IPAddress? GetPreferredIPv4(IReadOnlyCollection<string>? disabledAddresses = null)
    {
        var all = GetEnabledBinds(disabledAddresses);
        if (all.Count == 0) return null;
        return all.FirstOrDefault(a => IsRfc1918(a.Address))?.Address
               ?? all.FirstOrDefault(a => !IsApipa(a.Address))?.Address
               ?? all[0].Address;
    }

    internal static IEnumerable<SsdpBindAddress> EnumerateBindAddresses(bool skipVirtual)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                or NetworkInterfaceType.Ppp)
                continue;
            if (!ni.SupportsMulticast) continue;
            if (skipVirtual && IsLikelyVirtualAdapter(ni.Name, ni.Description))
                continue;

            var ifIndex = 0;
            try { ifIndex = ni.GetIPProperties().GetIPv4Properties()?.Index ?? 0; }
            catch { /* some NICs throw */ }

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var mask = ua.IPv4Mask ?? IPAddress.Parse("255.255.255.0");
                yield return new SsdpBindAddress(ua.Address, mask, ifIndex, ni.Name, ni.Description);
            }
        }
    }

    public static bool IsLikelyVirtualAdapter(string name, string description)
    {
        var hay = $"{name} {description}";
        return VirtualNicMarkers.Any(m => hay.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSameSubnet(IPAddress local, IPAddress mask, IPAddress remote)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork ||
            remote.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var l = ToUInt32(local);
        var r = ToUInt32(remote);
        var m = ToUInt32(mask);
        return (l & m) == (r & m);
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static bool IsApipa(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    private static bool IsRfc1918(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        if (b[0] == 10) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        return false;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private sealed record SsdpEndpoint(UdpClient Client, SsdpBindAddress Bind);
}

public sealed record SsdpBindAddress(IPAddress Address, IPAddress Mask, int IfIndex, string NicName, string Description = "");
