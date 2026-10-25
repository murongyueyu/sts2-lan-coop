using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Sts2LanCoop.Core;

public sealed record Endpoint(string Host, ushort Port)
{
    public const ushort DefaultPort = 33771;
    public override string ToString() => $"{Host}:{Port}";

    public static bool TryParse(string? input, out Endpoint? endpoint, out string error,
        ushort defaultPort = DefaultPort)
    {
        endpoint = null;
        error = "请输入 IPv4 或域名，可附带 :端口（1–65535）。";
        var text = input?.Trim() ?? "";
        if (text.Length is 0 or > 260 || defaultPort == 0 ||
            text.Any(char.IsWhiteSpace) || text.IndexOfAny(['/', '\\', '@', '?', '#', '[', ']']) >= 0)
            return false;
        var parts = text.Split(':');
        if (parts.Length > 2) return false;
        var port = defaultPort;
        if (parts.Length == 2 && (!ushort.TryParse(parts[1], NumberStyles.None,
                CultureInfo.InvariantCulture, out port) || port == 0))
            return false;

        var host = parts[0];
        if (IPAddress.TryParse(host, out var ip))
        {
            // Do not accept abbreviated/octal IPv4 forms or bind/multicast addresses as destinations.
            if (ip.AddressFamily != AddressFamily.InterNetwork || host.Split('.').Length != 4 ||
                !host.Split('.').All(s => s.Length is > 0 and <= 3 &&
                    s.All(char.IsAsciiDigit) && (s.Length == 1 || s[0] != '0')))
                return false;
            var bytes = ip.GetAddressBytes();
            if (bytes[0] is 0 or >= 224) return false;
            host = ip.ToString();
        }
        else
        {
            if (host.All(c => char.IsAsciiDigit(c) || c == '.')) return false;
            host = (host.EndsWith('.') ? host[..^1] : host).ToLowerInvariant();
            if (host.Length is 0 or > 253 || host.Split('.').Any(label =>
                    label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                    !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                return false;
        }
        endpoint = new Endpoint(host, port);
        error = "";
        return true;
    }
}
