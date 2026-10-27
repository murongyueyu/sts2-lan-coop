using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

// Run cleanup is synchronous; only its own Quit call may keep this exact session alive.
[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.Disconnect))]
internal static class ReloadHostDisconnect
{
    private static bool Prefix(NetHostGameService __instance, NetError reason) =>
        !(QuickReload.SuppressCleanupDisconnect && QuickReload.PreservedService == __instance && reason == NetError.Quit);
}
[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.Disconnect))]
internal static class ReloadClientDisconnect
{
    private static bool Prefix(NetClientGameService __instance, NetError reason) =>
        !(QuickReload.SuppressCleanupDisconnect && QuickReload.PreservedService == __instance && reason == NetError.Quit);
}

[HarmonyPatch(typeof(ENetPacket), nameof(ENetPacket.FromAppMessage))]
internal static class ReloadGameEpoch
{
    private static void Postfix(ref ENetPacket __result)
    {
        if (!LanSession.Active) return;
        var bytes = __result.AsAppMessage();
        if (ReloadWire.Matches(bytes) || AdmissionWire.IsControl(bytes)) return;
        var payload = RitsuNetworkInterop.IsSidecar(bytes) ? bytes : NativeMessageCompatibility.Outgoing(bytes);
        __result = new ENetPacket([__result.AllBytes[0], .. GameEpochWire.Wrap(QuickReload.Epoch, payload)]);
    }
}

internal static class ReloadRouting
{
    public static bool Route(INetGameService service, ulong sender, ref byte[] packet)
    {
        if (!LanSession.Active) return true;
        if (ReloadWire.Matches(packet))
        {
            try { QuickReload.Receive(service, sender, ReloadWire.Decode(packet)); }
            catch (Exception ex) { GD.PushWarning("[STS2 LAN Co-op] 拒绝SL控制包：" + ex.Message); }
            return false;
        }
        if (QuickReload.DropOldTraffic || !GameEpochWire.TryRead(QuickReload.Epoch, packet, out var payload)) return false;
        packet = RitsuNetworkInterop.IsSidecar(payload) ? payload : NativeMessageCompatibility.Incoming(payload);
        return true;
    }
}
[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.OnPacketReceived))]
internal static class ReloadHostRoute
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NetHostGameService __instance, ulong senderId, ref byte[] packetBytes) => ReloadRouting.Route(__instance, senderId, ref packetBytes);
}
[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.OnPacketReceived))]
internal static class ReloadClientRoute
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NetClientGameService __instance, ulong senderId, ref byte[] packetBytes) => ReloadRouting.Route(__instance, senderId, ref packetBytes);
}

[HarmonyPatch(typeof(Cmd), "WaitInternal")]
internal static class ReloadCancelOldWaits
{
    private static void Postfix(ref Task __result)
    { if (LanSession.Active) __result = __result.WaitAsync(QuickReload.GameWaitToken);
      else if (RunStorage.Solo) __result = __result.WaitAsync(SoloLibrary.WaitToken); }
}
[HarmonyPatch(typeof(CombatStateSynchronizer), nameof(CombatStateSynchronizer.WaitForSync))]
internal static class ReloadCancelOldSync
{
    private static void Postfix(ref Task __result)
    { if (LanSession.Active) __result = __result.WaitAsync(QuickReload.GameWaitToken);
      else if (RunStorage.Solo) __result = __result.WaitAsync(SoloLibrary.WaitToken); }
}
[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class ReloadPreventLateScene
{
    private static void Prefix() { QuickReload.CheckLoadCancellation(); SoloLibrary.CheckCancellation(); }
}
[HarmonyPatch(typeof(NCharacterSelectScreen), "StartNewMultiplayerRun")]
internal static class ReloadTrackNewRun
{
    private static void Postfix(Task __result) => QuickReload.TrackStartup(__result);
}
[HarmonyPatch(typeof(NMultiplayerLoadGameScreen), "StartRun")]
internal static class ReloadTrackSavedRun
{
    private static void Postfix(Task __result) => QuickReload.TrackStartup(__result);
}

internal static class ReloadCommand
{
    public static bool Busy => QuickReload.Busy || SoloLibrary.Busy;
    public static string? BlockReason() => RunStorage.Solo ? SoloLibrary.BlockReason() : QuickReload.BlockReason();
    public static void Request()
    {
        if (RunStorage.Solo) _ = Solo(); else QuickReload.Request();
    }
    private static async Task Solo()
    {
        try
        {
            if (SoloLibrary.BlockReason() is { } blocked) { ReloadUi.Notice(blocked); return; }
            await SoloLibrary.Load(SoloLibrary.ActiveEntry!, true);
        }
        catch (Exception ex) { ReloadUi.Notice("快速SL未完成：" + ex.Message); GD.PushError(ex.ToString()); }
    }
}

internal static class ReloadUi
{
    private static NativePage? _overlay;
    private static Label? _status;
    private static UiAction? _cancel;
    private static Action? _cancelAction;
    private static bool _wasPressed;
    private static bool _asking;
    private static Window Root => ((SceneTree)Engine.GetMainLoop()).Root;
    public static void Reset() { Hide(); NativeUi.Dismiss("QuickSlNotice"); NativeUi.Dismiss("QuickSlRequest"); _wasPressed = false; _asking = false; }
    public static void Progress(string text, Action? cancel)
    {
        NativeUi.Dismiss("QuickSlNotice");
        if (_overlay == null || !GodotObject.IsInstanceValid(_overlay))
        {
            _overlay = new NativePage { Name = "LanQuickReload", Heading = "快速SL", Dialog = true };
            Root.AddChild(_overlay); _status = NativeUi.Label(_overlay.Body, text, 28);
            _cancel = new UiAction(_overlay.Footer, "取消", () => _cancelAction?.Invoke());
            _overlay.CloseRequested = () => _cancelAction?.Invoke();
        }
        _status!.Text = text; _cancelAction = cancel; _cancel!.Node.Visible = cancel != null;
        _overlay.Busy = cancel == null;
    }
    public static void Hide()
    { if (_overlay != null && GodotObject.IsInstanceValid(_overlay)) _overlay.QueueFree(); _overlay = null; _status = null; _cancel = null; _cancelAction = null; }
    public static void Notice(string text) { Hide(); NativeUi.Dismiss("QuickSlNotice"); _ = NativeUi.Confirm(Root, "快速SL", text, null, "QuickSlNotice"); }
    public static async void Ask(string name, Action approved, Action denied)
    {
        if (_asking) return; _asking = true;
        try
        {
            if (await NativeUi.Confirm(Root, "快速SL", $"{name}请求全员返回最近保存点，角色保持不变。是否执行？", "确认", "QuickSlRequest")) approved();
            else denied();
        }
        finally { _asking = false; }
    }
    public static void CheckHotkey()
    {
        if (!LanSession.Active && !RunStorage.Solo) { _wasPressed = false; return; }
        var binding = ReloadShortcut.Parse(LanSession.Settings.QuickReloadHotkey);
        var pressed = binding != null && Input.IsPhysicalKeyPressed((Key)((long)Key.F1 + binding.FunctionKey - 1)) &&
            Input.IsPhysicalKeyPressed(Key.Ctrl) == binding.Ctrl && Input.IsPhysicalKeyPressed(Key.Alt) == binding.Alt &&
            Input.IsPhysicalKeyPressed(Key.Shift) == binding.Shift;
        if (pressed && !_wasPressed && !ReloadCommand.Busy && Root.GuiGetFocusOwner() is not (LineEdit or TextEdit)) ReloadCommand.Request();
        _wasPressed = pressed;
    }
}

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.OnSubmenuOpened))]
internal static class ReloadPauseButton
{
    private static void Postfix(NPauseMenu __instance)
    {
        if (!LanSession.Active && !RunStorage.Solo) return;
        var parent = __instance.GetNode<Control>("%ButtonContainer");
        var button = parent.GetNodeOrNull<MegaCrit.Sts2.Core.Nodes.GodotExtensions.NButton>("LanQuickSlButton");
        if (button == null)
        {
            var action = new UiAction(parent, "快速SL", ReloadCommand.Request, "LanQuickSlButton");
            button = action.Node;
            var before = parent.GetNodeOrNull<Control>("GiveUp") ?? parent.GetNode<Control>("SaveAndQuit");
            parent.MoveChild(button, before.GetIndex());
        }
        button.TooltipText = ReloadCommand.BlockReason() ?? "返回本局最近保存点，保留当前角色。";
        button.SetEnabled(!ReloadCommand.Busy);
        NativeUi.FocusLoop(parent);
    }
}
[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class ReloadBlockKeyboard
{
    private static bool Prefix(NGame __instance, InputEvent inputEvent)
    {
        if (NativeUi.HandleInput(inputEvent)) return false;
        if (!ReloadCommand.Busy || inputEvent is not InputEventKey) return true;
        __instance.GetViewport().SetInputAsHandled(); return false;
    }
}
