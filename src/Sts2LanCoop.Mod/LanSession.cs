using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class LanSession
{
    public static bool Active { get; private set; }
    public static ulong LocalId { get; private set; }
    public static NetHostGameService? HostedService { get; private set; }
    public static RunEntry? ResumeEntry { get; private set; }
    private static int _generation;
    private static SettingsStore? _store;
    private static LanSettings? _settings;
    public static LanSettings Settings => _settings ??= Store.Load();
    private static SettingsStore Store => _store ??= new SettingsStore(
        ProjectSettings.GlobalizePath("user://sts2_lan_coop/settings.json"));

    public static void Save() => Store.Save(Settings);

    // 与平台无关的公共检查：游戏版本、外部QuickSL和重复的LAN Mod。
    public static string? GetBlockReason()
    {
        var version = MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        if (!GameBuild.Supported)
            return $"当前游戏版本 {version ?? "未知"}，此DLL支持v0.107.1和v0.111.0。";
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name == "QuickSL"))
            return "本版已内置全员快速SL。请禁用外部QuickSL并重启，再建立LAN会话，避免两套重载同时执行。";
        var conflict = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
        {
            var name = a.GetName().Name ?? "";
            return name.Contains("sts2_lan_connect", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("LAN.Multiplayer", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("LanSpire", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("DirectConnectIP", StringComparison.OrdinalIgnoreCase);
        });
        return conflict == null ? null : $"检测到其他 LAN Mod：{conflict.GetName().Name}。请在独立测试配置中仅启用一个 LAN Mod，并重启游戏。";
    }

    // 期1起按能力判定：Android只放宽客机加入，主机等其他入口各自返回原因。
    public static string? GetJoinBlockReason() => LanCapability.JoinBlockReason ?? GetBlockReason();
    public static string? GetHostBlockReason() => LanCapability.HostBlockReason ?? GetBlockReason();

    private static NSubmenuStack Stack(NMultiplayerSubmenu menu) =>
        (NSubmenuStack)(AccessTools.Field(typeof(NSubmenu), "_stack")?.GetValue(menu)
            ?? throw new MissingFieldException("NSubmenu._stack"));

    private static void Begin(ulong id)
    {
        if (Active) throw new InvalidOperationException("已有 LAN 会话，请先离开房间。");
        var block = GetBlockReason();
        if (block != null) throw new InvalidOperationException(block);
        RitsuNetworkInterop.Ensure();
        Settings.Validate();
        Save();
        LocalId = id;
        Active = true;
        _generation++;
        AdmissionTransport.Reset();
        QuickReload.Reset();
    }

    public static NetHostGameService Host(NMultiplayerSubmenu menu, string? name = null, RunEntry? resume = null)
    {
        if (GetHostBlockReason() is { } blocked) throw new InvalidOperationException(blocked);
        if (resume == null) SaveLibrary.New(name); else SaveLibrary.Select(resume);
        var run = resume == null ? null : SaveLibrary.Decode(SaveLibrary.Catalog.ReadPayload(resume));
        if (run != null) run = RunManager.CanonicalizeSave(run, 1);
        Begin(1); // v0.107.1 ENetHost.NetId is natively fixed to 1.
        ResumeEntry = resume;
        var generation = _generation;
        var host = GameBuild.Host();
        try
        {
            var error = host.StartENetHost(Settings.HostPort, LanSettings.MaximumPlayers);
            if (error.HasValue) throw new InvalidOperationException($"建房失败：{error.Value}。请检查 UDP 端口是否被占用。");
            HostedService = host;
            ModSyncTransport.Warm();
            host.Disconnected += _ => { if (_generation == generation) End(); };
            if (run == null)
            {
                var screen = Stack(menu).GetSubmenuType<NCharacterSelectScreen>();
                screen.InitializeMultiplayerAsHost(host, LanSettings.MaximumPlayers);
                Stack(menu).Push(screen);
            }
            else
            {
                var screen = Stack(menu).GetSubmenuType<NMultiplayerLoadGameScreen>();
                screen.InitializeAsHost(host, run);
                Stack(menu).Push(screen);
            }
            return host;
        }
        catch
        {
            host.Disconnect(NetError.InternalError, now: true);
            End();
            throw;
        }
    }

    public static async Task Join(NMultiplayerSubmenu menu, Endpoint endpoint, Action<JoinFlow> onFlow,
        ulong? selectedSlot = null, AdmissionMessage? inspected = null)
    {
        if (QuickReload.Busy) throw new InvalidOperationException("请等待本次SL及恢复结束后再入房。");
        if (GetJoinBlockReason() is { } blocked) throw new InvalidOperationException(blocked);
        // Preserve any native LAN save before the client can overwrite its working file.
        SaveLibrary.ImportWorking();
        Begin(Settings.PlayerId);
        var generation = _generation;
        var flow = GameBuild.Join();
        flow.CancelToken.CancelAfter(TimeSpan.FromSeconds(20));
        var transferred = false;
        try
        {
            onFlow(flow);
            var info = inspected ?? await AdmissionTransport.Request(menu.GetTree(), endpoint, new(), flow.CancelToken.Token);
            if (info.Resume && !selectedSlot.HasValue)
                throw new InvalidOperationException("请先查看房间并选择一个已有的客机角色。");
            var grant = await AdmissionTransport.Request(menu.GetTree(), endpoint, new()
            {
                Operation = "reserve", Session = info.Session, Revision = info.Revision,
                NativeMessages = NativeMessageCompatibility.Capture(),
                Capabilities = LanCapability.Capabilities,
                Person = Settings.PlayerId, Name = Settings.PlayerName, Slot = selectedSlot ?? Settings.PlayerId
            }, flow.CancelToken.Token);
            LocalId = grant.Slot;
            AdmissionTransport.AcceptHost(info.Session);
            NativeMessageCompatibility.AcceptHost(grant.NativeMessages);
            AdmissionTransport.ClaimToken = grant.Token;
            AdmissionTransport.SetNames(grant.Names);
            AdmissionTransport.Names[LocalId.ToString()] = Settings.PlayerName;
            var result = await flow.Begin(new ENetClientConnectionInitializer(LocalId, endpoint.Host, endpoint.Port), menu.GetTree());
            if (result.gameMode != GameMode.Standard) throw new InvalidOperationException("仅支持普通合作对局。");
            if (result.sessionState == RunSessionState.InLobby && result.joinResponse.HasValue && !info.Resume)
            {
                var screen = Stack(menu).GetSubmenuType<NCharacterSelectScreen>();
                screen.InitializeMultiplayerAsClient(GameBuild.Service(flow)!, result.joinResponse.Value);
                Stack(menu).Push(screen);
            }
            else if (result.sessionState == RunSessionState.InLoadedLobby && result.loadJoinResponse.HasValue && info.Resume)
            {
                var screen = Stack(menu).GetSubmenuType<NMultiplayerLoadGameScreen>();
                screen.InitializeAsClient(GameBuild.Service(flow)!, result.loadJoinResponse.Value);
                Stack(menu).Push(screen);
            }
            else throw new InvalidOperationException("房间状态已变化，请刷新后重新加入。");
            GameBuild.Service(flow)!.Disconnected += _ => { if (_generation == generation) End(); };
            transferred = true;
            Settings.RememberSuccessfulConnection(endpoint);
            try { Save(); }
            catch (IOException ex) { GD.PushWarning($"[STS2 LAN Co-op] 已入房，但连接历史保存失败：{ex.Message}"); }
            catch (UnauthorizedAccessException ex) { GD.PushWarning($"[STS2 LAN Co-op] 已入房，但连接历史保存失败：{ex.Message}"); }
        }
        finally
        {
            if (!transferred)
            {
                GameBuild.Service(flow)?.Disconnect(NetError.CancelledJoin, now: true);
                End();
            }
            flow.CancelToken.Dispose();
        }
    }

    private static void End()
    {
        Active = false;
        LocalId = 0;
        HostedService = null;
        ResumeEntry = null;
        AdmissionTransport.Reset();
    }
}
