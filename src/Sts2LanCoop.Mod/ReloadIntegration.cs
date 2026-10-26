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
        __result = new ENetPacket([__result.AllBytes[0], .. GameEpochWire.Wrap(QuickReload.Epoch, NativeMessageCompatibility.Outgoing(bytes))]);
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
        packet = NativeMessageCompatibility.Incoming(payload); return true;
    }
}
[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.OnPacketReceived))]
internal static class ReloadHostRoute
{
    private static bool Prefix(NetHostGameService __instance, ulong senderId, ref byte[] packetBytes) => ReloadRouting.Route(__instance, senderId, ref packetBytes);
}
[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.OnPacketReceived))]
internal static class ReloadClientRoute
{
    private static bool Prefix(NetClientGameService __instance, ulong senderId, ref byte[] packetBytes) => ReloadRouting.Route(__instance, senderId, ref packetBytes);
}

[HarmonyPatch(typeof(Cmd), "WaitInternal")]
internal static class ReloadCancelOldWaits
{
    private static void Postfix(ref Task __result)
    { if (LanSession.Active) __result = __result.WaitAsync(QuickReload.GameWaitToken); }
}
[HarmonyPatch(typeof(CombatStateSynchronizer), nameof(CombatStateSynchronizer.WaitForSync))]
internal static class ReloadCancelOldSync
{
    private static void Postfix(ref Task __result)
    { if (LanSession.Active) __result = __result.WaitAsync(QuickReload.GameWaitToken); }
}
[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class ReloadPreventLateScene
{
    private static void Prefix() => QuickReload.CheckLoadCancellation();
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

internal static class ReloadUi
{
    private static CanvasLayer? _overlay;
    private static Label? _status;
    private static Button? _cancel;
    private static Action? _cancelAction;
    private static bool _wasPressed;
    private static ConfirmationDialog? _request;
    private static AcceptDialog? _notice;
    private static Window Root => ((SceneTree)Engine.GetMainLoop()).Root;
    public static void Reset()
    {
        Hide();
        if (_notice != null && GodotObject.IsInstanceValid(_notice)) _notice.QueueFree();
        if (_request != null && GodotObject.IsInstanceValid(_request)) _request.QueueFree();
        _notice=null; _request=null; _wasPressed=false;
    }
    public static void Progress(string text, Action? cancel)
    {
        if (_notice != null && GodotObject.IsInstanceValid(_notice)) _notice.QueueFree();
        _notice = null;
        if (_overlay == null || !GodotObject.IsInstanceValid(_overlay))
        {
            _overlay = new CanvasLayer { Name = "LanQuickReload", Layer = 200 }; Root.AddChild(_overlay);
            var mask = new ColorRect { Color = new Color(0,0,0,0.88f), MouseFilter = Control.MouseFilterEnum.Stop };
            _overlay.AddChild(mask); mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var margin = new MarginContainer(); mask.AddChild(margin);
            margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            foreach (var side in new[] {"left","right","top","bottom"}) margin.AddThemeConstantOverride("margin_" + side, 28);
            var center = new CenterContainer(); margin.AddChild(center);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(Math.Min(780, Math.Max(180, Root.GetVisibleRect().Size.X - 80)), 0) };
            center.AddChild(box);
            _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _status.AddThemeFontSizeOverride("font_size", 26); box.AddChild(_status);
            box.AddChild(new Label { Text = "角色、昵称和存档名称保持不变。", HorizontalAlignment = HorizontalAlignment.Center });
            _cancel = new Button { Text = "取消本次SL", CustomMinimumSize = new Vector2(0,50) }; box.AddChild(_cancel);
            _cancel.Pressed += () => _cancelAction?.Invoke();
        }
        _status!.Text = text; _cancelAction = cancel; _cancel!.Visible = cancel != null;
    }
    public static void Hide()
    { if (_overlay != null && GodotObject.IsInstanceValid(_overlay)) _overlay.QueueFree(); _overlay = null; _status = null; _cancel = null; _cancelAction = null; }
    public static void Notice(string text)
    {
        Hide();
        if (_notice != null && GodotObject.IsInstanceValid(_notice)) _notice.QueueFree();
        var dialog = new AcceptDialog { Title = "多人快速SL", DialogText = text }; _notice = dialog;
        Root.AddChild(dialog); dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }
    public static void Ask(string name, Action approved, Action denied)
    {
        if (_request != null && GodotObject.IsInstanceValid(_request)) return;
        _request = new ConfirmationDialog { Title = "客机申请快速SL", DialogText = $"{name} 请求全员回到最近保存点。\n本次角色保持不变。是否执行？" };
        Root.AddChild(_request);
        var dialog = _request;
        dialog.Confirmed += () => { dialog.QueueFree(); _request = null; approved(); };
        dialog.Canceled += () => { dialog.QueueFree(); _request = null; denied(); };
        dialog.PopupCentered();
    }
    public static void CheckHotkey()
    {
        if (!LanSession.Active) { _wasPressed = false; return; }
        var binding = ReloadShortcut.Parse(LanSession.Settings.QuickReloadHotkey);
        var pressed = binding != null && Input.IsPhysicalKeyPressed((Key)((long)Key.F1 + binding.FunctionKey - 1)) &&
            Input.IsPhysicalKeyPressed(Key.Ctrl) == binding.Ctrl && Input.IsPhysicalKeyPressed(Key.Alt) == binding.Alt &&
            Input.IsPhysicalKeyPressed(Key.Shift) == binding.Shift;
        if (pressed && !_wasPressed && !QuickReload.Busy && Root.GuiGetFocusOwner() is not (LineEdit or TextEdit)) QuickReload.Request();
        _wasPressed = pressed;
    }
}

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.OnSubmenuOpened))]
internal static class ReloadPauseButton
{
    private static void Postfix(NPauseMenu __instance)
    {
        if (!LanSession.Active || __instance.HasNode("LanQuickSlButton") ||
            __instance.GetNodeOrNull<Node>("%ButtonContainer")?.HasNode("LanQuickSlButton") == true) return;
        var button = new Button { Name = "LanQuickSlButton", Text = "快速SL · 全员重载", TooltipText = "回到本局最近保存点，保留当前角色和昵称。换角色需保存退出后重新入房。" };
        if (LanCapability.Android && __instance.GetNodeOrNull<Container>("%ButtonContainer") is { } buttons)
        {
            button.Text = "申请全员快速SL";
            button.CustomMinimumSize = new Vector2(0, 60);
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            buttons.AddChild(button);
            button.AddThemeFontSizeOverride("font_size",24); button.Pressed += QuickReload.Request;
            return;
        }
        __instance.AddChild(button); button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        button.OffsetLeft = -410; button.OffsetRight = -35; button.OffsetTop = 60; button.OffsetBottom = 120;
        button.AddThemeFontSizeOverride("font_size",24); button.Pressed += QuickReload.Request;
    }
}
[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class ReloadBlockKeyboard
{
    private static bool Prefix(NGame __instance, InputEvent inputEvent)
    {
        if (!QuickReload.Busy || inputEvent is not InputEventKey) return true;
        __instance.GetViewport().SetInputAsHandled(); return false;
    }
}
