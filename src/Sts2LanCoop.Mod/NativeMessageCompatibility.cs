using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class NativeMessageCompatibility
{
    private static NativeMessageMap? _clientMap;
    private static int _sentLogs, _receivedLogs;
    private static readonly HashSet<string> Milestones = [];
    public static List<string> Capture()
    {
        var cache = AccessTools.Field(typeof(MessageTypes), "_cache")
            ?? throw new MissingFieldException("原版消息表读取失败：MessageTypes._cache不存在，请核对游戏构建。");
        try
        {
            // Some mobile startup paths do not initialize this cache before our admission UI.
            // Use the native initializer once; never rebuild an existing table or synthesize IDs.
            if (cache.GetValue(null) == null)
            {
                GD.Print($"[STS2 LAN Co-op] 原版消息表尚未初始化，调用MessageTypes.Initialize（{LanCapability.Platform}，{GameBuild.Version}）。");
                MessageTypes.Initialize();
            }
            var names = new List<string>();
            for (var i = 0; i < 256 && MessageTypes.TryGetMessageType(i, out var type); i++)
                names.Add(type!.FullName ?? throw new InvalidDataException("原版消息类型没有完整名称。"));
            NativeMessageMap.Validate(names);
            return names;
        }
        catch (Exception ex)
        {
            var failure = new InvalidOperationException($"读取原版消息表失败（{LanCapability.Platform}，{GameBuild.Version}，已初始化={cache.GetValue(null) != null}）：{ex.GetType().Name}：{ex.Message}", ex);
            GD.PushWarning($"[STS2 LAN Co-op] {failure}");
            throw failure;
        }
    }
    public static void Reset() { _clientMap = null; _sentLogs = _receivedLogs = 0; Milestones.Clear(); }
    public static void AcceptHost(List<string> host)
    {
        var local = Capture();
        _clientMap = new NativeMessageMap(local, host);
        GD.Print($"[STS2 LAN Co-op] 消息编号协商完成：{local.Count}种，转换{_clientMap.ChangedCount}种（以主机编号传输）。");
        foreach (var name in local.Where(n => n.EndsWith("HeartbeatRequestMessage", StringComparison.Ordinal) ||
                     n.EndsWith("ClientLobbyJoinRequestMessage", StringComparison.Ordinal)))
            GD.Print($"[STS2 LAN Co-op] 消息映射 {name}：本机{local.IndexOf(name)} → 主机{host.IndexOf(name)}。");
    }
    public static byte[] Outgoing(byte[] payload) => Convert(payload, outgoing: true);
    public static byte[] Incoming(byte[] payload) => Convert(payload, outgoing: false);
    private static byte[] Convert(byte[] payload, bool outgoing)
    {
        // v111 performs its own version/model/mod handshake before NetMessageBus traffic.
        // Its STS2 magic is not a MessageTypes ID. Keep it intact for HandshakeManager validation.
        if (GameBuild.Version == "v0.111.0" && payload.AsSpan().StartsWith("STS2"u8)) return payload;
        // Hosts already use the canonical numbering, but log their received native type too.
        var result = _clientMap == null ? payload : outgoing ? _clientMap.ToHost(payload) : _clientMap.FromHost(payload);
        ref var count = ref (outgoing ? ref _sentLogs : ref _receivedLogs);
        if (payload.Length > 0 && MessageTypes.TryGetMessageType(outgoing ? payload[0] : result[0], out var type) &&
            (count < 3 || type!.Name == "LobbyBeginRunMessage" && Milestones.Add((outgoing ? "sent:" : "received:") + type.FullName)))
        {
            if (count < 3) count++;
            GD.Print($"[STS2 LAN Co-op] 原版消息{(outgoing ? "发送" : "接收")}：{type!.Name}，编号{payload[0]}→{result[0]}，长度{payload.Length}。");
        }
        return result;
    }
}
