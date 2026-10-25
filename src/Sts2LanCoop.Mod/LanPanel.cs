using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

// Native Godot controls/signals; session and transport work is delegated to LanSession.
internal sealed class LanPanel
{
    private readonly NMultiplayerSubmenu _menu;
    private readonly CanvasLayer _layer;
    private readonly VBoxContainer _body;
    private readonly Label _status;
    private readonly LineEdit _name;
    private readonly LineEdit _port;
    private readonly LineEdit _address;
    private readonly LineEdit _runName;
    private readonly LineEdit _reloadKey;
    private readonly CheckButton _shareMods;
    private readonly VBoxContainer _saves;
    private readonly VBoxContainer _slots;
    private AdmissionMessage? _inspected;
    private Endpoint? _inspectedEndpoint;
    private ulong? _chosenSlot;
    private CancellationTokenSource? _requestCancel;
    private readonly Button _host;
    private readonly Button _join;
    private readonly Button _close;
    private JoinFlow? _flow;
    private bool _busy;
    private bool _disposed;

    public static void Open(NMultiplayerSubmenu menu)
    {
        if (menu.HasNode("LanCoopPanel")) return;
        try { _ = new LanPanel(menu); }
        catch (Exception ex)
        {
            GD.PushError($"[STS2 LAN Co-op] 无法打开面板：{ex}");
            menu.GetNodeOrNull<CanvasLayer>("LanCoopPanel")?.QueueFree();
            var dialog = new AcceptDialog { Title = "LAN 面板无法打开", DialogText = ex.Message };
            menu.AddChild(dialog);
            dialog.Confirmed += dialog.QueueFree;
            dialog.Canceled += dialog.QueueFree;
            dialog.PopupCentered();
        }
    }

    private LanPanel(NMultiplayerSubmenu menu)
    {
        // Load before creating UI: malformed config is preserved and logged, never regenerated silently.
        var settings = LanSession.Settings;
        _menu = menu;
        _layer = new CanvasLayer { Name = "LanCoopPanel", Layer = 100 };
        menu.AddChild(_layer);
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, 0.85f) };
        _layer.AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer();
        backdrop.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.OffsetLeft = 70; panel.OffsetRight = -70;
        panel.OffsetTop = 45; panel.OffsetBottom = -45;
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 24);
        panel.AddChild(margin);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        margin.AddChild(scroll);
        _body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 14);
        scroll.AddChild(_body);
        Text("局域网 / 地址直连 · 最多4人", 32);
        Text("v0.107.1 · 新建或继续多人冒险 · 继续时人数固定，主机角色固定", 20);
        _name = Input("当前昵称（所有玩家可见，跟随本次加入的人）", settings.PlayerName);
        _port = Input("房主 UDP 监听端口", settings.HostPort.ToString());
        _shareMods = new CheckButton {Text="允许向加入者分享本房间所需模组（请确认有权分享；不含游戏、存档和账号）",ButtonPressed=settings.ShareMods};
        _body.AddChild(_shareMods);
        _runName = Input("新对局名称", $"多人冒险 {DateTime.Now:MM-dd HH:mm}");
        _host = Button("创建四人房间", Host);
        Text("继续多人对局", 26);
        Text("选择存档后，等待相同人数到齐。客机可以由其他朋友接替。", 20);
        _saves = new VBoxContainer(); _body.AddChild(_saves);
        Button("刷新存档列表", RefreshSaves);
        Text("加入房间", 26);
        _address = Input("房主 IP / 域名:端口", settings.RecentConnections.FirstOrDefault() ?? "");
        _address.PlaceholderText = "例如 room.example.com:45678";
        Button("查看房间 / 刷新可选角色", () => _ = Inspect());
        Button("检查 / 同步主机模组", () => _ = SyncMods());
        Button("恢复最近一次同步前的模组配置", () => _ = RestoreMods());
        _slots = new VBoxContainer(); _body.AddChild(_slots);
        _join = Button("连接", () => _ = Join());
        _address.TextSubmitted += text => { if (!_busy) _ = Join(); };
        foreach (var recent in settings.RecentConnections)
            Button($"最近连接：{recent}", () => { if (!_busy) _address.Text = recent; });
        _status = Text("Radmin：填写房主虚拟 IP；樱花：填写节点域名和外部 UDP 端口。", 20);
        _reloadKey = Input("快速SL快捷键（例如Ctrl+F8；off关闭；换角色需保存退出后重新入房）", settings.QuickReloadHotkey);
        Button("保存联机和快捷键设置", () => { if (!_busy) { try { ApplySettings(); _status.Text = "设置已保存。"; } catch (Exception ex) { _status.Text = ex.Message; } } });
        _close = Button("关闭", CloseOrCancel);
        var block = LanSession.GetBlockReason();
        if (block != null)
        {
            _status.Text = block;
            _host.Disabled = _join.Disabled = true;
        }
        _layer.TreeExiting += () => { _disposed = true; _flow?.CancelToken.Cancel(); _requestCancel?.Cancel(); };
        RefreshSaves();
        ModSyncWorkflow.ShowPending(_menu, value => _address.Text=value, value => {if(!_disposed)_status.Text=value;});
    }

    private Label Text(string text, int size = 24)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", size);
        _body.AddChild(label);
        return label;
    }

    private LineEdit Input(string label, string value)
    {
        Text(label, 20);
        var input = new LineEdit { Text = value, CustomMinimumSize = new Vector2(0, 52) };
        input.AddThemeFontSizeOverride("font_size", 24);
        _body.AddChild(input);
        return input;
    }

    private Button Button(string text, Action action)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 54) };
        button.AddThemeFontSizeOverride("font_size", 24);
        button.Pressed += action;
        _body.AddChild(button);
        return button;
    }

    private void ApplySettings()
    {
        if (!ushort.TryParse(_port.Text.Trim(), out var port) || port == 0)
            throw new InvalidDataException("监听端口应为 1–65535 的整数。");
        var name = _name.Text.Trim();
        if (name.Length is 0 or > 24 || name.Any(char.IsControl))
            throw new InvalidDataException("昵称应为 1–24 个字符，不能包含控制字符。");
        LanSession.Settings.PlayerName = name;
        LanSession.Settings.HostPort = port;
        LanSession.Settings.ShareMods = _shareMods.ButtonPressed;
        _ = ReloadShortcut.Parse(_reloadKey.Text);
        LanSession.Settings.QuickReloadHotkey = _reloadKey.Text.Trim();
        LanSession.Save();
    }

    private void Host()
    {
        if (_busy) return;
        try
        {
            ApplySettings();
            var service = LanSession.Host(_menu, _runName.Text);
            _layer.QueueFree();
            InvitePanel.Show(_menu.GetTree().Root, service, LanSession.Settings);
        }
        catch (Exception ex) { _status.Text = ex.Message; GD.PushWarning($"[STS2 LAN Co-op] {ex}"); }
    }

    private async Task Join()
    {
        if (_busy) return;
        try
        {
            ApplySettings();
            if (!Endpoint.TryParse(_address.Text, out var endpoint, out var error))
            { _status.Text = error; return; }
            SetBusy(true);
            _requestCancel = new();
            if (!await ModSyncWorkflow.Check(_menu, endpoint!, text => {if(!_disposed)_status.Text=text;}, _requestCancel.Token)) return;
            _status.Text = "正在连接并检查兼容性……";
            if (_inspectedEndpoint != endpoint) { _inspected = null; _chosenSlot = null; }
            if (_inspected == null)
            {
                _inspected = await AdmissionTransport.Request(_menu.GetTree(), endpoint!, new(), _requestCancel.Token);
                _inspectedEndpoint = endpoint;
            }
            if (_inspected.Resume && !_chosenSlot.HasValue)
            { ShowSlots(_inspected); _status.Text = "这是继续对局，请先选择一个客机角色，再连接。"; return; }
            await LanSession.Join(_menu, endpoint!, flow => _flow = flow, _chosenSlot, _inspected);
            _flow = null;
            _requestCancel?.Dispose(); _requestCancel = null;
            if (!_disposed) _layer.QueueFree();
        }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = "连接已取消或超时，请检查地址、组网和 UDP 隧道。"; }
        catch (ClientConnectionFailedException ex)
        {
            if (!_disposed) _status.Text = $"连接失败：{ex.info}。请核对游戏版本、玩法 Mod 和网络状态。";
            GD.PushWarning($"[STS2 LAN Co-op] {ex}");
        }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = ex.Message;
            GD.PushWarning($"[STS2 LAN Co-op] {ex}");
        }
        finally
        {
            _flow = null;
            _requestCancel?.Dispose(); _requestCancel=null;
            if (!_disposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _host.Disabled = _join.Disabled = busy || LanSession.GetBlockReason()!=null;
        _name.Editable = _port.Editable = _address.Editable = !busy;
        _runName.Editable = !busy;
        _reloadKey.Editable = !busy;
        _shareMods.Disabled = busy;
        _close.Text = busy ? "取消连接" : "关闭";
    }

    private void CloseOrCancel()
    {
        if (_busy) { _flow?.CancelToken.Cancel(); _requestCancel?.Cancel(); }
        else _layer.QueueFree();
    }

    private async Task SyncMods()
    {
        if(_busy)return;
        try
        {
            ApplySettings();
            if(!Endpoint.TryParse(_address.Text,out var endpoint,out var error))throw new InvalidDataException(error);
            SetBusy(true);_requestCancel=new();
            await ModSyncWorkflow.Check(_menu,endpoint!,text=>{if(!_disposed)_status.Text=text;},_requestCancel.Token);
        }
        catch(OperationCanceledException){if(!_disposed)_status.Text="同步已取消，下载缓存保留。";}
        catch(Exception ex){if(!_disposed)_status.Text=ex.Message;GD.PushWarning(ex.ToString());}
        finally{_requestCancel?.Dispose();_requestCancel=null;if(!_disposed)SetBusy(false);}
    }
    private async Task RestoreMods()
    {
        if(_busy)return;
        try{SetBusy(true);_requestCancel=new();await ModSyncWorkflow.RestoreLatest(_menu,_requestCancel.Token);}
        catch(Exception ex){if(!_disposed)_status.Text=ex.Message;}
        finally{_requestCancel?.Dispose();_requestCancel=null;if(!_disposed)SetBusy(false);}
    }

    private void RefreshSaves()
    {
        if (_busy) return;
        foreach (var child in _saves.GetChildren()) { _saves.RemoveChild(child); child.QueueFree(); }
        try
        {
            SaveLibrary.ImportWorking();
            var entries = SaveLibrary.Catalog.List();
            if (entries.Count == 0) _saves.AddChild(new Label { Text = "尚无多人存档。进入对局后会在原版保存点自动归档。" });
            foreach (var entry in entries)
            {
                var row = new VBoxContainer(); _saves.AddChild(row);
                row.AddChild(new Label { Text = $"{entry.Name} · {entry.PlayerCount}人 · 第{entry.Act}幕/{entry.Floor}层 · {entry.SavedAt.ToLocalTime():MM-dd HH:mm} · {(entry.ModEnvironment.Length==0?"历史模组环境未记录":"已记录模组环境")}" });
                var rename = new LineEdit { Text = entry.Name, MaxLength = 40 }; row.AddChild(rename);
                var actions = new HBoxContainer(); row.AddChild(actions);
                var resume = new Button { Text = "继续此对局", CustomMinimumSize = new Vector2(200, 48) }; actions.AddChild(resume);
                resume.Pressed += () =>
                {
                    if (_busy) return;
                    try
                    {
                        ApplySettings();
                        var service = LanSession.Host(_menu, resume: SaveLibrary.Catalog.Read(entry.Id));
                        _layer.QueueFree(); InvitePanel.Show(_menu.GetTree().Root, service, LanSession.Settings);
                    }
                    catch (Exception ex) { _status.Text = ex.Message; GD.PushWarning(ex.ToString()); }
                };
                var change = new Button { Text = "保存名称", CustomMinimumSize = new Vector2(180, 48) }; actions.AddChild(change);
                change.Pressed += () =>
                {
                    if (_busy) return;
                    try { SaveLibrary.Rename(entry.Id, rename.Text); RefreshSaves(); }
                    catch (Exception ex) { _status.Text = ex.Message; }
                };
            }
        }
        catch (Exception ex) { _status.Text = "读取存档失败，原文件已保留：" + ex.Message; }
    }

    private async Task Inspect()
    {
        if (_busy) return;
        try
        {
            ApplySettings();
            if (!Endpoint.TryParse(_address.Text, out var endpoint, out var error)) throw new InvalidDataException(error);
            SetBusy(true); _requestCancel = new(); _status.Text = "正在获取房间和角色资料……";
            var info = await AdmissionTransport.Request(_menu.GetTree(), endpoint!, new(), _requestCancel.Token);
            if (_disposed) return;
            _inspectedEndpoint = endpoint; _inspected = info; _chosenSlot = null; ShowSlots(info);
            _status.Text = info.Running ? "对局正在进行，请等待主机保存退出后重新组队。" :
                info.Resume ? $"{info.Name}：需要{info.Count}人，请选择客机角色。" : "新房间，可以直接连接。";
        }
        catch (Exception ex) { if (!_disposed) _status.Text = ex.Message; }
        finally { _requestCancel?.Dispose(); _requestCancel = null; if (!_disposed) SetBusy(false); }
    }

    private void ShowSlots(AdmissionMessage info)
    {
        foreach (var child in _slots.GetChildren()) { _slots.RemoveChild(child); child.QueueFree(); }
        foreach (var slot in info.Slots)
        {
            var locked = slot.NativeId == 1 || info.Occupied.Contains(slot.NativeId.ToString());
            var character = string.IsNullOrEmpty(slot.CharacterTitle) ? slot.Character : slot.CharacterTitle;
            var label = $"{character} · 生命{slot.Hp}/{slot.MaxHp} · 金币{slot.Gold}";
            var choose = new Button { Text = label + (slot.NativeId == 1 ? "（主机固定）" : locked ? "（已占用）" : "（选择）"), Disabled = locked || info.Running };
            _slots.AddChild(choose);
            string Pretty(IEnumerable<string> items) => string.Join("、", items.Select(id => slot.Titles.GetValueOrDefault(id, id)));
            var detail = new Label { Text = $"遗物：{Pretty(slot.Relics)}\n药水：{Pretty(slot.Potions)}\n牌组（{slot.Deck.Count}张）：{Pretty(slot.Deck)}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(600, 0) };
            detail.AddThemeFontSizeOverride("font_size", 18); _slots.AddChild(detail);
            choose.Pressed += () =>
            {
                if (_busy) return;
                _chosenSlot = slot.NativeId; _status.Text = $"已选择{label}，点击连接。";
            };
        }
    }
}
