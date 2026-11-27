using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sts2LanCoop.Core;

public sealed record LocalAddress(string Adapter, string Address, bool IsRadmin)
{
    public string Endpoint(ushort port) => $"{Address}:{port}";
}

public static class LocalAddresses
{
    // 引擎接口返回的原始地址：只保留可分享的 IPv4。
    public static IReadOnlyList<LocalAddress> Usable(IEnumerable<(string Adapter, string Address)> items) => items
        .Where(i => i.Address.Split('.').Length == 4 && System.Net.IPAddress.TryParse(i.Address, out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetwork && !i.Address.StartsWith("127.") &&
            !i.Address.StartsWith("169.254.") && i.Address != "0.0.0.0")
        .Select(i => new LocalAddress(i.Adapter, i.Address, i.Adapter.Contains("Radmin", StringComparison.OrdinalIgnoreCase)))
        .DistinctBy(a => a.Address).OrderByDescending(a => a.IsRadmin).ThenBy(a => a.Adapter).ToArray();

    public static IReadOnlyList<LocalAddress> Enumerate() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => new LocalAddress(n.Name, a.Address.ToString(),
                (n.Name + " " + n.Description).Contains("Radmin", StringComparison.OrdinalIgnoreCase))))
        .OrderByDescending(a => a.IsRadmin).ThenBy(a => a.Adapter).ToArray();
}
