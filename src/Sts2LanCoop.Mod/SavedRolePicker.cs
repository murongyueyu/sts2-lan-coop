using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.addons.mega_text;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal sealed record RoleSelection(AdmissionMessage Room, ulong Slot);

internal sealed class SavedRolePicker
{
    private readonly NativePage _page;
    private readonly Endpoint _endpoint;
    private readonly AdmissionMessage _expected;
    private AdmissionMessage _room;
    private readonly Label _summary;
    private readonly HBoxContainer _cards;
    private readonly Label _detail;
    private readonly Label _status;
    private readonly UiAction _confirm;
    private readonly UiAction _refresh;
    private readonly UiAction _back;
    private readonly TaskCompletionSource<RoleSelection?> _completion = new();
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private ulong? _selected;
    private bool _stale;
    private bool _disposed;
    private readonly Dictionary<ulong, NSubmenuButton> _buttons = [];

    public static async Task<RoleSelection?> Choose(NMultiplayerSubmenu parent, Endpoint endpoint, AdmissionMessage room, CancellationToken cancellation)
    {
        if (parent.HasNode("LanSavedRolePicker")) throw new InvalidOperationException("请先完成当前角色选择。");
        SavedRoleChoice.Validate(room);
        SavedRolePicker picker;
        try { picker = new SavedRolePicker(parent, endpoint, room, cancellation); }
        catch { parent.GetNodeOrNull<NativePage>("LanSavedRolePicker")?.QueueFree(); throw; }
        using var registration = cancellation.Register(() => picker._completion.TrySetCanceled(cancellation));
        try { return await picker._completion.Task; }
        finally
        {
            picker._stop.Cancel(); picker._stop.Dispose();
            if (GodotObject.IsInstanceValid(picker._page)) picker._page.QueueFree();
        }
    }
    private SavedRolePicker(NMultiplayerSubmenu parent, Endpoint endpoint, AdmissionMessage room, CancellationToken cancellation)
    {
        _expected = _room = room; _endpoint = endpoint;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _token = _stop.Token;
        _page = new NativePage { Name = "LanSavedRolePicker", Heading = "选择接管角色" };
        parent.AddChild(_page);
        _page.CloseRequested = () => _completion.TrySetResult(null);
        _page.TreeExiting += () => { _disposed = true; _completion.TrySetResult(null); };
        _summary = NativeUi.Label(_page.Body, "", 26);
        _summary.HorizontalAlignment = HorizontalAlignment.Center;
        _cards = new HBoxContainer { Name = "RoleCards", Alignment = BoxContainer.AlignmentMode.Center };
        _cards.AddThemeConstantOverride("separation", 18); _page.Body.AddChild(_cards);
        _detail = NativeUi.Label(_page.Body, "请选择一个可接管的角色。", 23);
        _detail.Name = "SelectedRoleDetails";
        _status = NativeUi.Label(_page.Body, "接管后保留该角色的卡组、遗物和进度。", 23);
        _status.Name = "RoleStatus";
        _back = new UiAction(_page.Footer, "返回房间", () => _completion.TrySetResult(null), "BackToRoom");
        _refresh = new UiAction(_page.Footer, "刷新角色", () => _ = Refresh(false), "RefreshRoles");
        _confirm = new UiAction(_page.Footer, "确认角色并加入", () => _ = Refresh(true), "ConfirmRole");
        Rebuild(); _back.Node.GrabFocus();
    }
    private void Rebuild()
    {
        foreach (var child in _cards.GetChildren()) { _cards.RemoveChild(child); child.QueueFree(); }
        _buttons.Clear();
        if (_selected is { } previous && !SavedRoleChoice.CanSelect(_room, previous)) _selected = null;
        _summary.Text = $"{_room.Name} · {_room.Count}人存档" + (_room.Act > 0 ? $" · 第{_room.Act}幕 / 第{_room.Floor}层" : "");
        var width = (1080f - (_room.Count - 1) * 18) / _room.Count;
        foreach (var slot in _room.Slots.OrderBy(s => s.NativeId == 1 ? 0 : 1).ThenBy(s => s.NativeId))
        {
            var card = GD.Load<PackedScene>("res://scenes/ui/submenu_button.tscn").Instantiate<NSubmenuButton>();
            card.Name = "Slot" + slot.NativeId; _cards.AddChild(card);
            card.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
            card.CustomMinimumSize = new Vector2(width, 430); card.Size = card.CustomMinimumSize;
            card.SetMeta("saved_slot", slot.NativeId.ToString());
            var portrait = card.GetNode<TextureRect>("Icon");
            Place(portrait, new Vector2(20, 16), new Vector2(width - 40, 190));
            portrait.Texture = Portrait(slot);
            var title = card.GetNode<MegaLabel>("%Title");
            Place(title, new Vector2(8, 215), new Vector2(width - 16, 42));
            title.SetTextAutoSize(Title(slot));
            var description = card.GetNode<MegaRichTextLabel>("%Description");
            description.BbcodeEnabled = false;
            Place(description, new Vector2(14, 265), new Vector2(width - 28, 155));
            description.Text = Description(slot);
            var locked = card.GetNode<TextureRect>("%Lock");
            Place(locked, new Vector2(width - 50, 24), new Vector2(28, 28));
            var allowed = !_stale && SavedRoleChoice.CanSelect(_room, slot.NativeId);
            card.SetEnabled(allowed);
            var selected = _selected == slot.NativeId;
            var border = new ReferenceRect { Name = "SelectedBorder", EditorOnly = false,
                BorderColor = new Color("efc851"), BorderWidth = 4, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = selected };
            card.AddChild(border); border.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            card.Connect(NClickableControl.SignalName.Released, Callable.From<Control>(_ => Select(slot.NativeId)));
            _buttons[slot.NativeId] = card;
        }
        UpdateSelection(); NativeUi.FocusLoop(_page);
    }
    private static string Title(SlotSummary slot) => string.IsNullOrWhiteSpace(slot.CharacterTitle) ? slot.Character : slot.CharacterTitle;
    private string Description(SlotSummary slot)
    {
        var state = slot.NativeId == 1 ? "主机 · 已锁定" : _room.Running ? "对局进行中" :
            _room.Occupied.Contains(slot.NativeId.ToString()) ? "已占用 / 已预留" : _selected == slot.NativeId ? "可接管 · 已选中" : "可接管";
        var name = _room.Names.GetValueOrDefault(slot.NativeId.ToString());
        return $"{state}\n生命 {slot.Hp} / {slot.MaxHp}\n金币 {slot.Gold}" + (name == null ? "" : $"\n玩家：{name}");
    }
    private void Select(ulong slot)
    {
        if (_disposed || _page.Busy || _stale || !SavedRoleChoice.CanSelect(_room, slot)) return;
        _selected = slot; UpdateSelection();
    }
    private void UpdateSelection()
    {
        foreach (var slot in _room.Slots)
        {
            var card = _buttons[slot.NativeId];
            card.GetNode<ReferenceRect>("SelectedBorder").Visible = _selected == slot.NativeId;
            card.GetNode<MegaRichTextLabel>("%Description").Text = Description(slot);
        }
        var selected = _room.Slots.FirstOrDefault(s => s.NativeId == _selected);
        _detail.Text = selected == null ? "请选择一个可接管的角色。" :
            $"已选角色：{Title(selected)}\n卡组（{selected.Deck.Count}）：{ItemPreview.Format(selected.Deck, selected.Titles)}\n遗物：{ItemPreview.Format(selected.Relics, selected.Titles)}";
        _confirm.Disabled = _page.Busy || _stale || selected == null;
    }
    private async Task Refresh(bool confirm)
    {
        if (_disposed || _page.Busy || _stale || _completion.Task.IsCompleted) return;
        _page.Busy = true; _confirm.Disabled = _refresh.Disabled = _back.Disabled = true;
        foreach (var button in _buttons.Values) button.Disable();
        var chosen = _selected;
        try
        {
            var fresh = await AdmissionTransport.Request(_page.GetTree(), _endpoint, new(), _token);
            if (_disposed || _completion.Task.IsCompleted) return;
            SavedRoleChoice.Validate(fresh);
            if (!SavedRoleChoice.SameAdventure(_expected, fresh))
            {
                _stale = true; _selected = null;
                _status.Text = "房间或存档已更换，请返回房间后重新查看。";
            }
            else
            {
                _room = fresh;
                if (confirm && chosen is { } id && SavedRoleChoice.CanSelect(fresh, id))
                { _completion.TrySetResult(new(fresh, id)); return; }
                _status.Text = fresh.Running ? "对局已经开始，请等待主机保存退出。" :
                    confirm ? "该角色已被认领，请重新选择。" : "角色状态已刷新。人数到齐并准备后，由房主开始。";
            }
        }
        catch (OperationCanceledException) { _completion.TrySetCanceled(_token); }
        catch (Exception ex) { if (!_disposed) _status.Text = "刷新未完成：" + ex.Message; }
        finally
        {
            if (!_disposed && !_completion.Task.IsCompleted)
            {
                _page.Busy = false; _refresh.Disabled = _stale; _back.Disabled = false;
                Rebuild();
            }
        }
    }
    private static void Place(Control node, Vector2 position, Vector2 size)
    { node.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft); node.Position = position; node.Size = size; }
    private static Texture2D Portrait(SlotSummary slot)
    {
        try
        {
            var model = ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry == slot.Character);
            if (model?.CharacterSelectIcon is { } icon) return icon;
        }
        catch (Exception ex) { GD.PushWarning($"[STS2 LAN Co-op] 角色{slot.Character}预览图不可用：{ex.Message}"); }
        return GD.Load<Texture2D>("res://images/ui/main_menu/submenu_unknown.png");
    }
}
