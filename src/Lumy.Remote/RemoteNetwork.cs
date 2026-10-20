using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

namespace Lumy.Remote;

public static class RemoteNetwork
{
    /// <summary>
    /// The computer's addresses on the home network, best first: adapters with a gateway (the real Wi-Fi or
    /// Ethernet) before virtual ones (Hyper-V, VPN, VirtualBox).
    /// </summary>
    public static IReadOnlyList<string> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => (Adapter: n, Properties: n.GetIPProperties()))
            .OrderByDescending(x => x.Properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .ThenBy(x => LooksVirtual(x.Adapter))
            .SelectMany(x => x.Properties.UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
            .Select(a => a.ToString())
            .Distinct()
            .ToList();

    /// <summary>10.x, 172.16–31.x and 192.168.x: addresses that only exist inside a home or office network.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>A PNG of a QR code for <paramref name="text"/>, black on white so every phone camera reads it.</summary>
    public static byte[] QrPng(string text, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    private static bool LooksVirtual(NetworkInterface adapter)
    {
        var name = adapter.Name + " " + adapter.Description;
        return name.Contains("virtual", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VPN", StringComparison.OrdinalIgnoreCase);
    }
}
