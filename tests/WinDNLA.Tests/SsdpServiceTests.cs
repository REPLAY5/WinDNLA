using System.Net;
using WinDNLA.Dlna;

namespace WinDNLA.Tests;

public class SsdpServiceTests
{
    [Fact]
    public void Search_response_includes_dlnadoc_and_st_before_location()
    {
        var ssdp = new SsdpService();
        ssdp.Configure(8200, "11111111-2222-3333-4444-555555555555", "WinDNLA");
        var msg = ssdp.FormatSearchResponse(
            "urn:schemas-upnp-org:device:MediaServer:1",
            "uuid:11111111-2222-3333-4444-555555555555::urn:schemas-upnp-org:device:MediaServer:1",
            "http://192.168.1.10:8200/description.xml");

        Assert.Contains("DLNADOC/1.50", msg);
        Assert.Contains("ST: urn:schemas-upnp-org:device:MediaServer:1", msg);
        Assert.Contains("LOCATION: http://192.168.1.10:8200/description.xml", msg);
        Assert.True(msg.IndexOf("\nST:", StringComparison.Ordinal) < msg.IndexOf("\nLOCATION:", StringComparison.Ordinal));
        Assert.EndsWith("\r\n\r\n", msg);
    }

    [Fact]
    public void Notify_uuid_usn_is_not_doubled()
    {
        var ssdp = new SsdpService();
        ssdp.Configure(8200, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "WinDNLA");
        var msg = ssdp.BuildNotify(
            "uuid:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "ssdp:alive",
            "http://192.168.1.10:8200/description.xml");

        Assert.Contains("USN: uuid:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\r\n", msg);
        Assert.DoesNotContain("::uuid:", msg);
        Assert.Contains("DLNADOC/1.50", msg);
        Assert.Contains("NTS: ssdp:alive", msg);
    }

    [Fact]
    public void MediaServer_search_is_relevant()
    {
        var ssdp = new SsdpService();
        ssdp.Configure(8200, Guid.NewGuid().ToString(), "WinDNLA");
        Assert.True(ssdp.IsRelevantSearch("urn:schemas-upnp-org:device:MediaServer:1"));
        Assert.True(ssdp.IsRelevantSearch("ssdp:all"));
        Assert.False(ssdp.IsRelevantSearch("urn:schemas-upnp-org:device:MediaRenderer:1"));
    }

    [Fact]
    public void Same_subnet_uses_mask()
    {
        var mask = IPAddress.Parse("255.255.255.0");
        Assert.True(SsdpService.IsSameSubnet(
            IPAddress.Parse("192.168.1.10"), mask, IPAddress.Parse("192.168.1.50")));
        Assert.False(SsdpService.IsSameSubnet(
            IPAddress.Parse("192.168.1.10"), mask, IPAddress.Parse("192.168.2.50")));
        Assert.False(SsdpService.IsSameSubnet(
            IPAddress.Parse("192.168.1.10"), mask, IPAddress.Parse("172.24.80.1")));
    }

    [Fact]
    public void Virtual_adapters_are_detected()
    {
        Assert.True(SsdpService.IsLikelyVirtualAdapter("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter"));
        Assert.True(SsdpService.IsLikelyVirtualAdapter("Tailscale", "Tailscale Tunnel"));
        Assert.False(SsdpService.IsLikelyVirtualAdapter("Wi-Fi", "Intel(R) Wi-Fi 6 AX201"));
        Assert.False(SsdpService.IsLikelyVirtualAdapter("Ethernet", "Realtek PCIe GbE"));
    }

    [Fact]
    public void Filter_enabled_skips_disabled_addresses()
    {
        var a = new SsdpBindAddress(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"), 1, "Ethernet");
        var b = new SsdpBindAddress(IPAddress.Parse("172.24.80.1"), IPAddress.Parse("255.255.255.0"), 2, "vEthernet");
        var all = new[] { a, b };

        var filtered = SsdpService.FilterEnabled(all, ["172.24.80.1"]);
        Assert.Single(filtered);
        Assert.Equal("192.168.1.10", filtered[0].Address.ToString());

        var noneDisabled = SsdpService.FilterEnabled(all, []);
        Assert.Equal(2, noneDisabled.Count);

        var allDisabled = SsdpService.FilterEnabled(all, ["192.168.1.10", "172.24.80.1"]);
        Assert.Empty(allDisabled);
    }
}
