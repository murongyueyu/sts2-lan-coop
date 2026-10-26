using System.Buffers.Binary;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.addons.mega_text;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

// Control traffic shares the game's UDP port but never enters its INetMessage registry.
internal static class AdmissionTransport
{
    public static string Session { get; private set; } = "";
    public static string? ClaimToken { get; set; }
    public static Dictionary<string, string> Names { get; private set; } = [];
    private static SlotReservations _reservations = new();
    private static readonly Dictionary<ulong, ulong> People = [];
    private static readonly Dictionary<ulong, PeerCapabilities> Capabilities = [];
    public static string? ReloadBlockReason(IEnumerable<ulong> slots) => slots.Where(id => id != 1)
        .Any(id => !Capabilities.TryGetValue(id, out var value) || !value.CanReload)
            ? "有玩家未声明支持全员SL协议2，请统一版本并重新入房。" : null;
    private static readonly Dictionary<ENetPacketPeer, ulong> Pending = [];
    private static ulong _lastBroadcast;
    private static int _emptyPackets;
    private static readonly System.Reflection.FieldInfo Connection = AccessTools.Field(typeof(ENetHost), "_connection");
    private static readonly System.Reflection.MethodInfo Receive = AccessTools.Method(typeof(ENetHost), "HandlePacketReceived");
    private static readonly System.Reflection.MethodInfo Handshake = AccessTools.Method(typeof(ENetHost), "DoClientHandshake");

    public static void Reset()
    {
        Session = Guid.NewGuid().ToString("N"); ClaimToken = null; Names = [];
        _reservations = new(); Pending.Clear(); People.Clear(); Capabilities.Clear(); _lastBroadcast = 0; _emptyPackets = 0;
        ModSyncTransport.Reset();
        NativeMessageCompatibility.Reset();
    }
    public static void SetNames(Dictionary<string, string> names)
    {
        var changed = Names.Count != names.Count || names.Any(p => Names.GetValueOrDefault(p.Key) != p.Value);
        Names = names;
        if (changed && Engine.GetMainLoop() is SceneTree tree) RefreshNames(tree.Root);
    }
    private static void RefreshNames(Node node)
    {
        ulong? id = node switch
        {
            NRemoteLobbyPlayer lobby when lobby.IsNodeReady() => lobby.PlayerId,
            NMultiplayerPlayerState state when state.IsNodeReady() => state.Player?.NetId,
            _ => null
        };
        if (id.HasValue && Names.TryGetValue(id.Value.ToString(), out var name) &&
            AccessTools.Field(node.GetType(), "_nameplateLabel")?.GetValue(node) is MegaLabel label)
            label.SetTextAutoSize(name);
        foreach (var child in node.GetChildren()) RefreshNames(child);
    }
    public static string? Name(ulong id) => Names.GetValueOrDefault(id.ToString());
    public static void AcceptHost(string session) => Session = session;
    private static AdmissionMessage Info() => new()
    {
        Session = Session, Capabilities = LanCapability.Capabilities, NativeMessages = NativeMessageCompatibility.Capture(), Resume = LanSession.ResumeEntry != null,
        Revision = LanSession.ResumeEntry?.Revision ?? "", Name = LanSession.ResumeEntry?.Name ?? "新的多人冒险",
        Running = RunManager.Instance.IsInProgress,
        Count = LanSession.ResumeEntry?.PlayerCount ?? 4, Slots = LanSession.ResumeEntry?.Slots ?? [],
        Occupied = (LanSession.HostedService?.NetHost?.ConnectedPeerIds ?? []).Concat(_reservations.ReservedSlots)
            .Append(1UL).Select(id => id.ToString()).Distinct().ToList(), Names = new(Names)
    };
    private static AdmissionMessage Respond(AdmissionMessage request)
    {
        var info = Info();
        if (request.Operation == "info") return info;
        if (request.Operation != "reserve") throw new InvalidDataException("未知请求。");
        if (request.Session != Session || request.Revision != info.Revision) throw new InvalidOperationException("房间已更换存档，请刷新。");
        var mapping = new NativeMessageMap(request.NativeMessages, info.NativeMessages);
        GD.Print($"[STS2 LAN Co-op] 客机消息表校验：{request.NativeMessages.Count}种，需转换{mapping.ChangedCount}种。");
        if (info.Running) throw new InvalidOperationException("对局已开始，请等待主机保存退出后重新组队。");
        var slot = info.Resume ? request.Slot : request.Person;
        if (info.Resume && !info.Slots.Any(s => s.NativeId == slot && slot != 1))
            throw new InvalidDataException("只能认领现有的客机角色。");
        var occupied = (LanSession.HostedService!.NetHost!.ConnectedPeerIds).Append(1UL).ToArray();
        if (occupied.Contains(slot)) throw new InvalidOperationException("该角色仍在线，不能重复加入或抢占。");
        if (People.Any(p => p.Value == request.Person && occupied.Contains(p.Key)))
            throw new InvalidOperationException("当前玩家身份已在房间内，不能同时认领两个角色。");
        if (!info.Resume && occupied.Length + _reservations.ReservedSlots.Count() >= 4 && !_reservations.ReservedSlots.Contains(slot))
            throw new InvalidOperationException("房间已满，最多4人。");
        var grant = _reservations.Reserve(request.Person, slot, request.Name, occupied, request.Capabilities);
        return info with { Token = grant.Token, Slot = slot };
    }

    public static async Task<AdmissionMessage> Request(SceneTree tree, Endpoint endpoint, AdmissionMessage request,
        CancellationToken cancellation = default)
    {
        using var connection = new ENetConnection();
        if (connection.CreateHost() != Error.Ok) throw new IOException("无法初始化角色选择连接。");
        ENetPacketPeer? peer = null;
        try
        {
            peer = connection.ConnectToHost(endpoint.Host, endpoint.Port);
            if (peer == null) throw new IOException("无法连接房主。");
            var deadline = Time.GetTicksMsec() + 12000;
            while (Time.GetTicksMsec() < deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                for (var i = 0; i < 64 && connection.TryService(out var data); i++)
                {
                    var message = data!.Value;
                    if (message.type == ENetConnection.EventType.Connect)
                        peer.Send(0, AdmissionWire.Encode(request), 1);
                    if (message.type == ENetConnection.EventType.Disconnect) throw new IOException("房主已断开连接。");
                    if (message.type == ENetConnection.EventType.Receive && AdmissionWire.IsControl(message.packetData))
                    {
                        var response = AdmissionWire.Decode(message.packetData);
                        if (response.Error.Length > 0) throw new InvalidOperationException(response.Error);
                        return response;
                    }
                }
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            throw new TimeoutException("房间资料请求超时。请统一Mod版本，并检查地址和UDP隧道。");
        }
        finally { peer?.PeerDisconnectNow(); connection.Destroy(); }
    }

    // 期0诊断：握手报文只取前13字节，避免把角色预留令牌写进日志。
    private static string Prefix(byte[] bytes) =>
        Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, bytes.Length == 77 ? 13 : 48)));
    private static string Chain(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current != null; current = current.InnerException) parts.Add($"{current.GetType().Name}：{current.Message}");
        return string.Join(" → ", parts);
    }

    public static void Update(ENetHost host)
    {
        if (Connection.GetValue(host) is not ENetConnection connection) return;
        for (var i = 0; i < 256 && connection.TryService(out var result); i++)
        {
            var data = result!.Value;
            if (data.type == ENetConnection.EventType.Connect)
            { Pending[data.peer] = Time.GetTicksMsec(); continue; }
            if (data.type == ENetConnection.EventType.Disconnect)
            { Pending.Remove(data.peer); continue; }
            if (data.type != ENetConnection.EventType.Receive) continue;
            if (data.packetData.Length == 0)
            {
                // 防御空报文；alpha.2实测的进房失败并非空包，不能据此推断Android一定会发送空包。
                if (_emptyPackets < 3) { _emptyPackets++; GD.Print($"[STS2 LAN Co-op] 忽略空报文（{_emptyPackets}/3），已丢弃。"); }
                continue;
            }
            try
            {
                if (ModSyncTransport.IsControl(data.packetData))
                {
                    if (!Pending.ContainsKey(data.peer)) throw new InvalidDataException("同步只能通过独立连接进行。");
                    Pending[data.peer] = Time.GetTicksMsec();
                    ModSyncTransport.Serve(data.peer, data.packetData); continue;
                }
                if (AdmissionWire.IsControl(data.packetData))
                {
                    AdmissionMessage response;
                    try { response = Respond(AdmissionWire.Decode(data.packetData)); }
                    catch (Exception ex) { response = new() { Error = ex.Message }; }
                    data.peer.Send(0, AdmissionWire.Encode(response), 1);
                    continue;
                }
                if (data.packetData.Length > 0 && data.packetData[0] == 0)
                {
                    // One handshake per newly connected transport, with a short-lived slot claim.
                    if (!Pending.Remove(data.peer))
                    {
                        GD.Print($"[STS2 LAN Co-op] 忽略未预登记的0类报文：长度{data.packetData.Length}，前缀{Prefix(data.packetData)}");
                        continue;
                    }
                    if (data.packetData.Length != 77 || !data.packetData.AsSpan(9, 4).SequenceEqual(AdmissionWire.Magic))
                        throw new InvalidDataException("请所有玩家统一使用支持角色接管的Mod版本。");
                    var id = BinaryPrimitives.ReadUInt64BigEndian(data.packetData.AsSpan(1, 8));
                    if (host.ConnectedPeerIds.Contains(id)) throw new InvalidOperationException("角色仍在线。");
                    if (RunManager.Instance.IsInProgress) throw new InvalidOperationException("对局已开始。");
                    var grant = _reservations.Consume(Encoding.ASCII.GetString(data.packetData, 13, 64), id);
                    People[id] = grant.Person;
                    Capabilities[id] = grant.Capabilities;
                    var names = new Dictionary<string, string>(Names) { [id.ToString()] = grant.Name };
                    SetNames(names);
                    GD.Print($"[STS2 LAN Co-op] 入房握手已受理：角色{id}，长度{data.packetData.Length}，前缀{Prefix(data.packetData)}");
                    data.packetData = data.packetData[..9];
                    try { Receive.Invoke(host, [data]); }
                    catch (Exception ex) { throw new InvalidOperationException("原版处理握手请求时报错。", ex); }
                    try { _ = (Task)Handshake.Invoke(host, [data.peer])!; }
                    catch (Exception ex) { throw new InvalidOperationException("原版握手应答失败。", ex); }
                    _lastBroadcast = 0;
                    continue;
                }
                try { Receive.Invoke(host, [data]); }
                catch (Exception ex) { throw new InvalidOperationException($"原版处理入房报文时报错（长度{data.packetData.Length}）。", ex); }
            }
            catch (Exception ex)
            {
                // 期0诊断：记录报文长度与前缀（握手报文只取前13字节，不含令牌）和完整异常链。
                GD.PushWarning($"[STS2 LAN Co-op] 拒绝无效连接：{Chain(ex)}；长度{data.packetData.Length}，前缀{Prefix(data.packetData)}\n{ex.GetBaseException().StackTrace}");
                Pending.Remove(data.peer); data.peer.PeerDisconnectNow();
            }
        }
        foreach (var pair in Pending.ToArray())
            if (!GodotObject.IsInstanceValid(pair.Key) || Time.GetTicksMsec() - pair.Value > 15000)
            { if (GodotObject.IsInstanceValid(pair.Key)) pair.Key.PeerDisconnectNow(); Pending.Remove(pair.Key); }
        if (Time.GetTicksMsec() - _lastBroadcast >= 1000)
        {
            _lastBroadcast = Time.GetTicksMsec();
            var live = host.ConnectedPeerIds.Append(1UL).Select(id => id.ToString()).ToHashSet();
            foreach (var key in Names.Keys.Except(live).ToArray()) Names.Remove(key);
            foreach (var key in People.Keys.Where(id => !live.Contains(id.ToString())).ToArray()) People.Remove(key);
            foreach (var key in Capabilities.Keys.Where(id => !live.Contains(id.ToString())).ToArray()) Capabilities.Remove(key);
            Names["1"] = LanSession.Settings.PlayerName;
            var bytes = AdmissionWire.Encode(new() { Operation = "names", Names = new(Names) });
            // Wrap as an application message so the native client buffers it safely during handshake.
            foreach (var id in host.ConnectedPeerIds.ToArray())
                host.SendMessageToClient(id, bytes, bytes.Length, NetTransferMode.Reliable);
        }
    }
}

[HarmonyPatch(typeof(ENetHost), nameof(ENetHost.Update))]
internal static class AdmissionHostUpdate
{
    private static bool Prefix(ENetHost __instance)
    {
        if (!LanSession.Active || LanSession.HostedService?.NetHost != __instance) return true;
        AdmissionTransport.Update(__instance); return false;
    }
}

[HarmonyPatch(typeof(ENetHost), nameof(ENetHost.StartHost))]
internal static class AdmissionCapacity
{
    private static void Prefix(ref int maxClients) { if (LanSession.Active) maxClients = Math.Max(maxClients, 12); }
}

[HarmonyPatch(typeof(ENetPacket), nameof(ENetPacket.FromHandshakeRequest))]
internal static class AdmissionClientClaim
{
    private static void Postfix(ref ENetPacket __result)
    {
        if (LanSession.Active && AdmissionTransport.ClaimToken is { } token)
            __result = new ENetPacket([.. __result.AllBytes, .. AdmissionWire.Magic, .. Encoding.ASCII.GetBytes(token)]);
    }
}

[HarmonyPatch(typeof(ENetClient), "HandleMessageReceived")]
internal static class AdmissionClientNames
{
    private static bool Prefix(ENetServiceData data)
    {
        if (!LanSession.Active) return true;
        // 空报文无法被原版ENetPacket解析（PacketType取[0]越界），直接丢弃。
        if (data.packetData.Length == 0) return false;
        if (data.packetData.Length < 2) return true;
        var packet = new ENetPacket(data.packetData);
        if (packet.PacketType != ENetPacketType.ApplicationMessage) return true;
        var bytes = packet.AsAppMessage();
        if (!AdmissionWire.IsControl(bytes)) return true;
        var message = AdmissionWire.Decode(bytes);
        if (message.Operation == "names") AdmissionTransport.SetNames(message.Names);
        return false;
    }
}

[HarmonyPatch(typeof(LoadRunLobby), nameof(LoadRunLobby.IsAboutToBeginGame))]
internal static class FixedResumeCount
{
    private static void Postfix(LoadRunLobby __instance, ref bool __result)
    {
        if (LanSession.Active)
            __result &= GameBuild.Connected(__instance).SetEquals(__instance.Run.Players.Select(p => p.NetId));
    }
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen.ShouldAllowRunToBegin))]
internal static class FixedResumeConfirmation
{
    private static bool Prefix(NMultiplayerLoadGameScreen __instance, ref Task<bool> __result)
    {
        if (!LanSession.Active) return true;
        var lobby = (LoadRunLobby)AccessTools.Field(typeof(NMultiplayerLoadGameScreen), "_runLobby").GetValue(__instance)!;
        __result = Task.FromResult(GameBuild.Connected(lobby).SetEquals(lobby.Run.Players.Select(p => p.NetId)));
        return false;
    }
}

[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), nameof(NMultiplayerLoadGameScreen._Process))]
internal static class ResumeWaitingText
{
    private static void Postfix(NMultiplayerLoadGameScreen __instance)
    {
        if (!LanSession.Active) return;
        if (AccessTools.Field(typeof(NMultiplayerLoadGameScreen), "_runLobby").GetValue(__instance) is not LoadRunLobby lobby) return;
        var label = __instance.GetNodeOrNull<Label>("LanResumeCount");
        if (label == null)
        {
            label = new Label { Name = "LanResumeCount", MouseFilter = Control.MouseFilterEnum.Ignore };
            label.AddThemeFontSizeOverride("font_size", 24); __instance.AddChild(label);
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft); label.Position = new Vector2(40, 20);
        }
        label.Text = $"继续对局 · {GameBuild.Connected(lobby).Count}/{lobby.Run.Players.Count}人 · 人数到齐并全部准备后开始（主机角色固定）";
    }
}
