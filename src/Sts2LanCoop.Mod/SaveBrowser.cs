using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class SaveBrowser
{
    public static void Open(Control parent, bool solo)
    {
        if (parent.HasNode("RunSaveBrowser")) return;
        var workingConflict = false;
        try { RunStorage.Recover(); if (solo) SoloLibrary.ImportWorking(); else SaveLibrary.ImportWorking(); }
        catch (Exception ex) { NativeUi.Notice(parent, "部分存档未能导入", ex.Message); }
        try { workingConflict = !solo && RunStorage.Lease.HasConflict(); }
        catch (Exception ex) { NativeUi.Notice(parent, "工作槽记录无法读取", ex.Message); }
        var page = new NativePage { Name = "RunSaveBrowser", Heading = solo ? "选择单人存档" : "继续多人对局" };
        parent.AddChild(page); page.CloseRequested = page.QueueFree;
        var columns = new HBoxContainer(); page.Body.AddChild(columns); columns.AddThemeConstantOverride("separation", 32);
        var list = new VBoxContainer { CustomMinimumSize = new Vector2(430, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var details = new VBoxContainer { CustomMinimumSize = new Vector2(580, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        columns.AddChild(list); columns.AddChild(details);
        var catalog = solo ? SoloLibrary.Catalog : SaveLibrary.Catalog;
        CatalogItem? selected = null;
        var status = NativeUi.Label(page.Body, "", 22);
        void Clear(Node node) { foreach (var child in node.GetChildren()) { node.RemoveChild(child); child.QueueFree(); } }
        void Show(CatalogItem item)
        {
            selected = item; Clear(details);
            if (item.Entry is not { } entry) NativeUi.Label(details, "存档读取失败：" + item.Error, 24);
            else
            {
                NativeUi.Label(details, entry.Name, 34, true);
                NativeUi.Label(details, $"第{entry.Act}幕 / 第{entry.Floor}层 · {entry.PlayerCount}人\n{entry.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n游戏版本：{entry.GameVersion}");
                foreach (var slot in entry.Slots)
                {
                    NativeUi.Label(details, $"{slot.CharacterTitle} · HP {slot.Hp}/{slot.MaxHp} · 金币 {slot.Gold}", 26, true);
                    string Pretty(IEnumerable<string> values) => ItemPreview.Format(values, slot.Titles);
                    NativeUi.Label(details, $"卡组（{slot.Deck.Count}）：{Pretty(slot.Deck)}\n遗物：{Pretty(slot.Relics)}\n药水：{Pretty(slot.Potions)}", 20);
                }
                var rename = NativeUi.Input(details, entry.Name, 40);
                new UiAction(details, "保存名称", () =>
                {
                    try { if (solo) SoloLibrary.Rename(entry.Id, rename.Text); else { RunStorage.RequireIdle(); SaveLibrary.Rename(entry.Id, rename.Text); } Refresh(entry.Id); }
                    catch (Exception ex) { status.Text = ex.Message; }
                });
            }
            new UiAction(details, "删除存档", () => _ = Delete(item));
            NativeUi.FocusLoop(page);
        }
        async Task Delete(CatalogItem item)
        {
            if (page.Busy) return;
            page.Busy = true;
            try
            {
                var title = item.Entry?.Name ?? item.Id;
                var summary = item.Entry is { } e ? $"\n第{e.Act}幕 / 第{e.Floor}层 · {e.SavedAt.ToLocalTime():MM-dd HH:mm}" : "\n索引损坏，仅清理该存档目录。";
                if (!await NativeUi.Confirm(page, "删除存档", $"删除「{title}」？{summary}\n此操作删除本局续玩存档及其历史修订，不能撤销。", "删除")) return;
                if (solo) SoloLibrary.Delete(item.Id, item.Entry?.Revision); else SaveLibrary.Delete(item.Id, item.Entry?.Revision);
                Refresh(); status.Text = "存档已删除。";
                NGame.Instance?.MainMenu?.RefreshButtons();
            }
            catch (Exception ex) { status.Text = "清理未完成，可重试：" + ex.Message; }
            finally { if (GodotObject.IsInstanceValid(page)) page.Busy = false; }
        }
        async Task ResolveConflict()
        {
            if (page.Busy) return;
            page.Busy = true;
            try
            {
                if (!await NativeUi.Confirm(page, "处理工作档冲突", "先完整备份当前多人主档、备份档和保护记录，再恢复LAN借用前的工作槽。\n已归档对局和其他存档不删除。未知附加数据保留在冲突备份中，不会自动导入。\n请先停止其他游戏进程，避免同时改写。", "备份并恢复")) return;
                var location = RunStorage.ResolveWorkingConflict();
                page.QueueFree();
                NativeUi.Notice(parent, "工作槽已恢复", "完整副本已保存到：\n" + location + "\n请重新打开继续对局选择存档。");
            }
            catch (Exception ex) { status.Text = "恢复未完成，现有文件保留：" + ex.Message; }
            finally { if (GodotObject.IsInstanceValid(page)) page.Busy = false; }
        }
        void Refresh(string? id = null)
        {
            Clear(list); Clear(details); selected = null;
            var items = catalog.Inspect().OrderByDescending(i => i.Entry?.SavedAt).ToArray();
            NativeUi.Label(list, solo ? "单人存档" : "多人存档", 30, true);
            foreach (var item in items)
            {
                var text = item.Entry is { } e ? (e.Name.Length > 18 ? e.Name[..18] + "…" : e.Name) : $"读取异常 · {item.Id[..8]}";
                new UiAction(list, text, () => Show(item));
                if (item.Entry is { } summary) NativeUi.Label(list, $"第{summary.Act}幕 / 第{summary.Floor}层 · {summary.SavedAt.ToLocalTime():MM-dd HH:mm}", 21);
            }
            if (items.Length == 0) NativeUi.Label(details, "尚无存档，开始新的冒险后会在原版保存点自动归档。");
            else Show(items.FirstOrDefault(i => i.Id == id) ?? items[0]);
            NativeUi.FocusLoop(page);
        }
        new UiAction(page.Footer, "返回", page.QueueFree);
        if (workingConflict) new UiAction(page.Footer, "处理工作档冲突", () => _ = ResolveConflict());
        if (solo) new UiAction(page.Footer, "新建冒险", () =>
        {
            try
            {
                RunStorage.RequireIdle();
                var stack = NGame.Instance!.MainMenu!.SubmenuStack;
                var screen = stack.GetSubmenuType<NCharacterSelectScreen>(); screen.InitializeSingleplayer();
                page.QueueFree(); stack.Push(screen);
            }
            catch (Exception ex) { status.Text = ex.Message; }
        });
        new UiAction(page.Footer, "继续此对局", () => _ = Continue());
        async Task Continue()
        {
            if (page.Busy || selected?.Entry is not { } entry) return;
            page.Busy = true;
            try
            {
                if (solo) { await SoloLibrary.Load(entry); if (GodotObject.IsInstanceValid(page)) page.QueueFree(); }
                else
                {
                    var menu = parent as NMultiplayerSubmenu ?? throw new InvalidOperationException("多人菜单已关闭。");
                    LanSession.Host(menu, resume: entry); page.QueueFree();
                }
            }
            catch (Exception ex) { if (GodotObject.IsInstanceValid(status)) status.Text = ex.Message; else ReloadUi.Notice(ex.Message); }
            finally { if (GodotObject.IsInstanceValid(page)) page.Busy = false; }
        }
        Refresh();
    }
}
