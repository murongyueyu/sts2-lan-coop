using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform.Steam;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class MultiplayerUi
{
    public static void Attach(NMultiplayerSubmenu menu)
    {
        if (menu.HasNode("LanMenuTabs")) return;
        ModSyncInventory.CaptureStartup();
        try { RunStorage.Recover(); }
        catch (Exception ex) { NativeUi.Notice(menu, "存档恢复待处理", ex.Message); }
        var tabs = new Control { Name = "LanMenuTabs" }; menu.AddChild(tabs);
        tabs.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); tabs.MouseFilter = Control.MouseFilterEnum.Ignore;
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        tabs.AddChild(row); row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        row.OffsetLeft = -380; row.OffsetRight = 380; row.OffsetTop = 50; row.OffsetBottom = 130;
        var cards = new Control { Name = "LanCards", MouseFilter = Control.MouseFilterEnum.Ignore };
        menu.AddChild(cards); cards.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var holder = new Control { Name = "CardLayout", Size = new Vector2(1200, 850) }; cards.AddChild(holder);
        var create = NativeUi.Card(holder, "CreateLan", "创建房间", "创建含房主最多4人的联机房间。", "host", () => LanPanel.Open(menu, LanPage.Create));
        var join = NativeUi.Card(holder, "JoinLan", "加入房间", "输入地址，加入朋友的房间。", "join", () => LanPanel.Open(menu, LanPage.Join));
        var resume = NativeUi.Card(holder, "ContinueLan", "继续对局", "选择存档，继续上次的冒险。", "load", () => SaveBrowser.Open(menu, false));
        var all = new[] { create, join, resume };
        for (var i = 0; i < all.Length; i++)
        {
            all[i].SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
            all[i].Position = new Vector2(30 + i * 390, 40); all[i].Size = new Vector2(330, 705);
        }
        var settings = new UiAction(holder, "联机设置", () => LanPanel.Open(menu, LanPage.Settings));
        settings.Node.Position = new Vector2(414, 760);
        void Fit()
        {
            var scale = Math.Min(menu.Size.X / 1420, (menu.Size.Y - 150) / 900);
            holder.Scale = Vector2.One * scale;
            holder.Position = new Vector2((menu.Size.X - 1200 * scale) / 2, 160);
        }
        menu.Resized += Fit; Fit();
        var unavailable = new VBoxContainer { Name = "SteamUnavailable", Visible = false };
        menu.AddChild(unavailable); unavailable.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        unavailable.OffsetLeft = -450; unavailable.OffsetRight = 450; unavailable.OffsetTop = -100; unavailable.OffsetBottom = 150;
        var unavailableTitle = NativeUi.Label(unavailable, "Steam 服务未启用", 38, true);
        unavailableTitle.HorizontalAlignment = HorizontalAlignment.Center;
        var unavailableText = NativeUi.Label(unavailable, "请通过 Steam 启动游戏后使用好友联机。\n也可以切换到局域网 / 直连。", 26);
        unavailableText.HorizontalAlignment = HorizontalAlignment.Center;
        unavailableText.AddThemeColorOverride("font_color", new Color("fff6e3"));
        UiAction? steam = null, lan = null;
        var initial = true;
        void Switch(bool local)
        {
            if (menu.HasNode("LanCoopPanel") || menu.HasNode("RunSaveBrowser")) return;
            menu.GetNode<Control>("ButtonContainer").Visible = !local && SteamInitializer.Initialized;
            unavailable.Visible = !local && !SteamInitializer.Initialized;
            cards.Visible = local;
            if (!initial)
            {
                LanSession.Settings.PreferLanMenu = local;
                try { LanSession.Save(); } catch (IOException ex) { NativeUi.Notice(menu, "菜单偏好未保存", ex.Message); }
            }
            steam!.Node.Modulate = local ? new Color(.65f, .65f, .65f) : new Color(1, .9f, .6f);
            lan!.Node.Modulate = local ? new Color(1, .9f, .6f) : new Color(.65f, .65f, .65f);
            NativeUi.FocusLoop(menu);
            if (local) (LanCapability.Android ? join : create).GrabFocus();
        }
        steam = new UiAction(row, "Steam 联机", () => Switch(false), "SteamTab");
        lan = new UiAction(row, "局域网 / 直连", () => Switch(true), "LanTab");
        create.SetEnabled(LanCapability.HostBlockReason == null);
        resume.SetEnabled(LanCapability.HostBlockReason == null);
        steam.Node.TooltipText = SteamInitializer.Initialized ? "使用原版Steam好友联机" : "当前Steam服务不可用";
        Switch(!SteamInitializer.Initialized || LanSession.Settings.PreferLanMenu == true);
        initial = false;
    }
}

[HarmonyPatch(typeof(NSingleplayerSubmenu), nameof(NSingleplayerSubmenu._Ready))]
internal static class SoloMenuLibraryButton
{
    private static void Postfix(NSingleplayerSubmenu __instance)
    {
        ModSyncInventory.CaptureStartup();
        var action = new UiAction(__instance, "选择存档", () => SaveBrowser.Open(__instance, true), "SoloSaveLibrary");
        action.Node.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        action.Node.OffsetLeft = -186; action.Node.OffsetRight = 186; action.Node.OffsetTop = -120; action.Node.OffsetBottom = -40;
    }
}

[HarmonyPatch(typeof(NMainMenu), "OnContinueButtonPressed")]
internal static class SoloContinueLibrary
{
    private static bool Prefix(NMainMenu __instance)
    {
        try
        {
            var path = SoloLibrary.WorkingPath;
            if (File.Exists(path) && SaveLibrary.Decode(File.ReadAllBytes(path)).GameMode != MegaCrit.Sts2.Core.Runs.GameMode.Standard) return true;
            SaveBrowser.Open(__instance, true); return false;
        }
        catch (Exception ex) { NativeUi.Notice(__instance, "存档列表无法打开", ex.Message); return false; }
    }
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu.RefreshButtons))]
internal static class RefreshSoloLibraryEntry
{
    private static void Postfix(NMainMenu __instance)
    {
        if (!GameBuild.Supported || !SoloLibrary.Catalog.Inspect().Any()) return;
        // A finished/deleted working slot must not hide the remaining independent adventures.
        var button = __instance.GetNode<NButton>("MainMenuTextButtons/ContinueButton");
        button.Visible = true; button.Enable();
    }
}
