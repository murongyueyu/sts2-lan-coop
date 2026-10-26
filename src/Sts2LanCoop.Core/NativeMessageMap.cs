namespace Sts2LanCoop.Core;

// The host's table is the wire numbering. Payloads and native validation stay unchanged.
public sealed class NativeMessageMap
{
    private readonly byte[] _toHost;
    private readonly byte[] _fromHost;
    public int ChangedCount { get; }

    public NativeMessageMap(IReadOnlyList<string> local, IReadOnlyList<string> host)
    {
        Validate(local); Validate(host);
        var hostIds = host.Select((name, id) => (name, id)).ToDictionary(p => p.name, p => p.id, StringComparer.Ordinal);
        var missing = local.Except(host, StringComparer.Ordinal).Concat(host.Except(local, StringComparer.Ordinal)).Take(4).ToArray();
        if (missing.Length != 0)
            throw new InvalidDataException("双方原版消息类型集合不同，请核对游戏构建和模组：" + string.Join(", ", missing));
        _toHost = new byte[local.Count]; _fromHost = new byte[host.Count];
        for (var i = 0; i < local.Count; i++)
        {
            var target = hostIds[local[i]];
            _toHost[i] = (byte)target; _fromHost[target] = (byte)i;
            if (i != target) ChangedCount++;
        }
    }

    public static void Validate(IReadOnlyList<string> names)
    {
        if (names == null || names.Count is < 1 or > 256 || names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 512 || n.Any(char.IsControl)) ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new InvalidDataException("原版消息类型表无效或包含重复类型，请核对游戏构建与启动器。");
    }

    public byte[] ToHost(byte[] payload) => Translate(payload, _toHost);
    public byte[] FromHost(byte[] payload) => Translate(payload, _fromHost);
    private static byte[] Translate(byte[] payload, byte[] ids)
    {
        if (payload.Length < 9 || payload[0] >= ids.Length)
            throw new InvalidDataException("原版消息头无效或消息编号未协商，请统一Mod版本并重新入房。");
        if (ids[payload[0]] == payload[0]) return payload;
        var result = (byte[])payload.Clone(); result[0] = ids[payload[0]]; return result;
    }
}
