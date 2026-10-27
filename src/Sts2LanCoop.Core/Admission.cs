using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2LanCoop.Core;

public sealed record PeerCapabilities
{
    public string Platform { get; init; } = "";
    public int ReloadVersion { get; init; }
    public bool CanReload => ReloadVersion == 2 && Platform is "Windows" or "Android";
}

public sealed record AdmissionMessage
{
    public int Protocol { get; init; } = 5;
    public PeerCapabilities Capabilities { get; init; } = new();
    public string Operation { get; init; } = "info";
    public string Error { get; init; } = "";
    public string Session { get; init; } = "";
    public string Token { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Resume { get; init; }
    public bool Running { get; init; }
    public int Count { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong Person { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong Slot { get; init; }
    public List<SlotSummary> Slots { get; init; } = [];
    public Dictionary<string, string> Names { get; init; } = [];
    public List<string> Occupied { get; init; } = [];
    public List<string> NativeMessages { get; init; } = [];
}

public static class AdmissionWire
{
    public static readonly byte[] Magic = [0xF3, 0x53, 0x4C, 0x32];
    public static bool IsControl(byte[] data) => data.AsSpan().StartsWith(Magic);
    public static byte[] Encode(AdmissionMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message);
        if (json.Length > 512 * 1024) throw new InvalidDataException("角色资料超过传输上限。");
        return [.. Magic, .. json];
    }
    public static AdmissionMessage Decode(byte[] data)
    {
        if (!IsControl(data) || data.Length > 512 * 1024 + 4) throw new InvalidDataException("无效的联机控制报文。");
        var message = JsonSerializer.Deserialize<AdmissionMessage>(data.AsSpan(4)) ?? throw new InvalidDataException("控制报文为空。");
        if (message.Protocol != 5) throw new InvalidDataException("联机协议版本不同，请所有玩家统一安装0.7.0-alpha.8或更新的同版Mod。");
        return message;
    }
}

public sealed class SlotReservations(Func<DateTimeOffset>? clock = null)
{
    private readonly Dictionary<string, Grant> _grants = [];
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    public sealed record Grant(string Token, ulong Person, ulong Slot, string Name, DateTimeOffset Expires)
    {
        public PeerCapabilities Capabilities { get; init; } = new();
    }
    public void Expire()
    { foreach (var key in _grants.Where(p => p.Value.Expires <= _clock()).Select(p => p.Key).ToArray()) _grants.Remove(key); }
    public Grant Reserve(ulong person, ulong slot, string name, IEnumerable<ulong> occupied, PeerCapabilities? capabilities = null)
    {
        Expire();
        name = name.Trim();
        if (person <= 3000 || slot == 1 || name.Length is 0 or > 24 || name.Any(char.IsControl))
            throw new InvalidDataException("身份、昵称或角色槽位无效。");
        if (occupied.Contains(slot) || _grants.Values.Any(g => g.Slot == slot && g.Person != person))
            throw new InvalidOperationException("该角色已被其他人认领，请刷新。");
        foreach (var key in _grants.Where(p => p.Value.Person == person).Select(p => p.Key).ToArray()) _grants.Remove(key);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return _grants[token] = new Grant(token, person, slot, name, _clock().AddSeconds(30)) { Capabilities = capabilities ?? new() };
    }
    public Grant Consume(string token, ulong slot)
    {
        Expire();
        if (!_grants.TryGetValue(token, out var grant) || grant.Slot != slot)
            throw new InvalidOperationException("角色预留已过期或不属于此连接，请重新选择。");
        _grants.Remove(token);
        return grant;
    }
    public IEnumerable<ulong> ReservedSlots { get { Expire(); return _grants.Values.Select(g => g.Slot).ToArray(); } }
}
