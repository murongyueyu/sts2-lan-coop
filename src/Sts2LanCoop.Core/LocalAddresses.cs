using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sts2LanCoop.Core;

public sealed record LocalAddress(string Adapter, string Address, bool IsRadmin)
{
    public string Endpoint(ushort port) => $"{Address}:{port}";
}

public static class LocalAddresses
{
    public static IReadOnlyList<LocalAddress> Enumerate() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => new LocalAddress(n.Name, a.Address.ToString(),
                (n.Name + " " + n.Description).Contains("Radmin", StringComparison.OrdinalIgnoreCase))))
        .OrderByDescending(a => a.IsRadmin).ThenBy(a => a.Adapter).ToArray();
}
