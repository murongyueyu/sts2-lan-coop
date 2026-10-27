using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

// Native Godot controls/signals; session and transport work is delegated to LanSession.
internal enum LanPage { Create, Join, Settings }

internal sealed class LanPanel
{
    private readonly NMultiplayerSubmenu _menu;
    private readonly NativePage _layer;
    private VBoxContainer _target = null!;
    private readonly VBoxContainer _body;
    private readonly Label _status;
    private readonly LineEdit _name;
    private readonly LineEdit _port;
    private readonly LineEdit _address;
    private readonly LineEdit _runName;
    private readonly LineEdit _reloadKey;
    private readonly CheckButton _shareMods;
    private readonly VBoxContainer _slots;
    private AdmissionMessage? _inspected;
    private Endpoint? _inspectedEndpoint;
    private CancellationTokenSource? _requestCancel;
    private readonly UiAction _host;
    private readonly UiAction _join;
    private readonly UiAction _sync;
    private readonly UiAction _restore;
    private readonly UiAction? _organizeWorkshop;
    private readonly UiAction? _restoreWorkshop;
    private readonly UiAction _unlock;
    private readonly UiAction _close;
    private readonly string? _hostBlock;
    private readonly string? _joinBlock;
    private JoinFlow? _flow;
    private bool _busy;
    private bool _disposed;

    public static void Open(NMultiplayerSubmenu menu, LanPage page = LanPage.Create)
    {
        if (menu.HasNode("LanCoopPanel")) return;
        try { _ = new LanPanel(menu, page); }
        catch (Exception ex)
        {
            GD.PushError($"[STS2 LAN Co-op] 无法打开面板：{ex}");
            menu.GetNodeOrNull<NativePage>("LanCoopPanel")?.QueueFree();
            var dialog = new AcceptDialog { Title = "LAN 面板无法打开", DialogText = ex.Message };
            menu.AddChild(dialog);
            dialog.Confirmed += dialog.QueueFree;
            dialog.Canceled += dialog.QueueFree;
            dialog.PopupCentered();
        }
    }

    private LanPanel(NMultiplayerSubmenu menu, LanPage page)
    {
        var settings = LanSession.Settings;
        _menu = menu;
        _hostBlock = LanSession.GetHostBlockReason(); _joinBlock = LanSession.GetJoinBlockReason();
        _layer = new NativePage { Name = "LanCoopPanel", Heading = page switch
        { LanPage.Create => "创建房间", LanPage.Join => "加入房间", _ => "联机设置" } };
        menu.AddChild(_layer); _body = _layer.Body;
        _layer.CloseRequested = CloseOrCancel;
        _target = _body;
        _name = Input("玩家昵称", settings.PlayerName);
        VBoxContainer Section(LanPage kind)
        {
            var group = new VBoxContainer { Visible = page == kind };
            group.AddThemeConstantOverride("separation", 12); _body.AddChild(group); _target = group; return group;
        }
        Section(LanPage.Create);
        Text("创建含房主最多4人的联机房间。", 24);
        _runName = Input("对局名称", $"多人冒险 {DateTime.Now:MM-dd HH:mm}");
        _port = Input("UDP 监听端口", settings.HostPort.ToString());
        _shareMods = new CheckButton { Text = "允许分享本房间所需模组", ButtonPressed = settings.ShareMods };
        _shareMods.AddThemeFontSizeOverride("font_size", 24);
        _shareMods.AddThemeColorOverride("font_color", new Color("382410"));
        _target.AddChild(_shareMods);
        Text("创建后，在大厅选择角色并等待朋友加入。", 22);
        _host = Button("创建房间", Host); _host.Disabled = _hostBlock != null;
        if (_hostBlock != null) Text(_hostBlock, 20);
        Section(LanPage.Join);
        _address = Input("房主 IP / 域名:端口", settings.RecentConnections.FirstOrDefault() ?? "");
        _address.PlaceholderText = "192.168.1.8:33771";
        Button("查看房间", () => _ = Inspect());
        _sync = Button(LanCapability.Android ? "下载主机模组" : "检查 / 同步模组", () => _ = SyncMods());
        _sync.Disabled = !LanCapability.CanInspectMods;
        _slots = new VBoxContainer(); _target.AddChild(_slots);
        _join = Button("连接房间", () => _ = Join()); _join.Disabled = _joinBlock != null;
        _address.TextSubmitted += text => { if (!_busy && _joinBlock == null) _ = Join(); };
        foreach (var recent in settings.RecentConnections)
            Button(recent, () => { if (!_busy) { _address.Text = recent; _inspected = null; } });
        if (LanCapability.Android) Text("下载后须退出游戏，通过启动器手动导入兼容包并重启。", 20);
        Text("仅要求玩法模组及其依赖一致，美化模组可保留各自配置。", 20);
        Section(LanPage.Settings);
        _restore = Button("恢复同步前的模组配置", () => _ = RestoreMods());
        _restore.Disabled = LanCapability.ModSyncBlockReason != null;
        if (LanCapability.Windows)
        {
            _organizeWorkshop = Button("整理 Steam 工坊原目录", () => _ = OrganizeWorkshop());
            _restoreWorkshop = Button("恢复工坊目录整理", () => _ = OrganizeWorkshop(true));
        }
        _unlock = Button("共用原版 / 模组解锁", () => { if (!_busy) SharedUnlocks.Configure(_menu, text => { if (!_disposed) _status!.Text = text; }); });
        _unlock.Disabled = LanCapability.SharedUnlockBlockReason != null;
        _reloadKey = Input("快速SL快捷键（off关闭）", settings.QuickReloadHotkey);
        Button("保存设置", () => { try { ApplySettings(); _status!.Text = "设置已保存。"; } catch (Exception ex) { _status!.Text = ex.Message; } });
        _target = _body;
        _status = Text(page == LanPage.Join ? "填写局域网、虚拟组网地址或外部UDP隧道地址。" : "", 22);
        _close = new UiAction(_layer.Footer, "返回", CloseOrCancel);
        _layer.TreeExiting += () => { _disposed = true; _flow?.CancelToken.Cancel(); _requestCancel?.Cancel(); };
        ModSyncWorkflow.ShowPending(_menu, value => _address.Text = value, value => { if (!_disposed) _status!.Text = value; });
        WorkshopOrganizationWorkflow.ShowLastResult(value => { if (!_disposed) _status!.Text = value; });
        NativeUi.FocusLoop(_layer); _name.GrabFocus();
    }

    private Label Text(string text, int size = 24) => NativeUi.Label(_target, text, size);
    private LineEdit Input(string label, string value)
    {
        Text(label, 22);
        return NativeUi.Input(_target, value);
    }

    private UiAction Button(string text, Action action) => new(_target, text, action);

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

        }
        catch (Exception ex) { _status!.Text = ex.Message; GD.PushWarning($"[STS2 LAN Co-op] {ex}"); }
    }

    private async Task Join()
    {
        if (_busy) return;
        try
        {
            ApplySettings();
            if (!Endpoint.TryParse(_address.Text, out var endpoint, out var error))
            { _status!.Text = error; return; }
            SetBusy(true);
            _requestCancel = new();
            // Platform-specific file checks precede the unchanged native admission.
            if (LanCapability.Windows &&
                !await ModSyncWorkflow.Check(_menu, endpoint!, text => {if(!_disposed)_status.Text=text;}, _requestCancel.Token)) return;
            if (LanCapability.Android &&
                !await MobileModSync.Check(_menu, endpoint!, text => {if(!_disposed)_status.Text=text;}, _requestCancel.Token, offerDownload: false)) return;
            _status!.Text = "正在连接并检查兼容性……";
            if (_inspectedEndpoint != endpoint) { _inspected = null; }
            if (_inspected == null)
            {
                _inspected = await AdmissionTransport.Request(_menu.GetTree(), endpoint!, new(), _requestCancel.Token);
                _inspectedEndpoint = endpoint;
            }
            ulong? selectedSlot = null;
            if (_inspected.Resume)
            {
                _status.Text = "请选择要接管的存档角色。";
                var choice = await SavedRolePicker.Choose(_menu, endpoint!, _inspected, _requestCancel.Token);
                if (choice == null) { _status.Text = "已返回房间，尚未认领角色。"; return; }
                _inspected = choice.Room; selectedSlot = choice.Slot;
            }
            await LanSession.Join(_menu, endpoint!, flow => _flow = flow, selectedSlot, _inspected);
            _flow = null;
            _requestCancel?.Dispose(); _requestCancel = null;
            if (!_disposed) _layer.QueueFree();
        }
        catch (OperationCanceledException) { if (!_disposed) _status!.Text = "连接已取消或超时，请检查地址、组网和 UDP 隧道。"; }
        catch (ClientConnectionFailedException ex)
        {
            if (!_disposed) _status!.Text = $"连接失败：{ex.info}。本机游戏 {GameBuild.Version}；请核对双方游戏版本与 Mod 版本是否一致，以及 UDP 隧道。";
            GD.PushWarning($"[STS2 LAN Co-op] {ex}");
        }
        catch (Exception ex)
        {
            if (!_disposed) _status!.Text = ex.Message;
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
        _busy = busy; _layer.Busy = busy;
        _host.Disabled = busy || _hostBlock != null;
        _join.Disabled = busy || _joinBlock != null;
        _sync.Disabled = busy || !LanCapability.CanInspectMods;
        _restore.Disabled = busy || LanCapability.ModSyncBlockReason != null;
        if (_organizeWorkshop != null) _organizeWorkshop.Disabled = busy;
        if (_restoreWorkshop != null) _restoreWorkshop.Disabled = busy;
        _unlock.Disabled = busy || LanCapability.SharedUnlockBlockReason != null;
        _name.Editable = _port.Editable = _address.Editable = !busy;
        _runName.Editable = !busy;
        _reloadKey.Editable = !busy;
        _shareMods.Disabled = busy || _hostBlock != null;
        _close.Text = busy ? "取消连接" : "返回";
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
            if (LanCapability.Android)
                await MobileModSync.Check(_menu,endpoint!,text=>{if(!_disposed)_status.Text=text;},_requestCancel.Token, offerDownload: true);
            else await ModSyncWorkflow.Check(_menu,endpoint!,text=>{if(!_disposed)_status.Text=text;},_requestCancel.Token);
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
    private async Task OrganizeWorkshop(bool restore = false)
    {
        if (_busy) return;
        try
        {
            SetBusy(true); _requestCancel = new();
            await WorkshopOrganizationWorkflow.Organize(_menu, text => { if (!_disposed) _status!.Text = text; }, _requestCancel.Token, restore);
        }
        catch (OperationCanceledException) { if (!_disposed) _status!.Text = "工坊整理已取消，目录未改动。"; }
        catch (Exception ex) { if (!_disposed) _status!.Text = ex.Message; GD.PushWarning(ex.ToString()); }
        finally { _requestCancel?.Dispose(); _requestCancel = null; if (!_disposed) SetBusy(false); }
    }

    private async Task Inspect()
    {
        if (_busy) return;
        try
        {
            ApplySettings();
            if (!Endpoint.TryParse(_address.Text, out var endpoint, out var error)) throw new InvalidDataException(error);
            SetBusy(true); _requestCancel = new(); _status!.Text = "正在获取房间和角色资料……";
            var info = await AdmissionTransport.Request(_menu.GetTree(), endpoint!, new(), _requestCancel.Token);
            if (_disposed) return;
            _inspectedEndpoint = endpoint; _inspected = info; ShowSlots(info);
            _status!.Text = info.Running ? "对局正在进行，请等待主机保存退出后重新组队。" :
                info.Resume ? $"{info.Name}：需要{info.Count}人，点击连接房间选择接管角色。" : "新房间，可以直接连接。";
        }
        catch (Exception ex) { if (!_disposed) _status!.Text = ex.Message; }
        finally { _requestCancel?.Dispose(); _requestCancel = null; if (!_disposed) SetBusy(false); }
    }

    private void ShowSlots(AdmissionMessage info)
    {
        foreach (var child in _slots.GetChildren()) { _slots.RemoveChild(child); child.QueueFree(); }
        NativeUi.Label(_slots, info.Resume ? $"{info.Name} · {info.Count}人存档" : "新的多人冒险", 28, true);
        NativeUi.Label(_slots, info.Running ? "对局正在进行。" : info.Resume ? "连接时选择已有角色，保留原来的完整进度。" : "连接后进入原版选角大厅。");
    }
}
