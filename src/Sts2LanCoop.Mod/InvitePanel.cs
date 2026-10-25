using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class InvitePanel
{
    public static void Show(Node root, NetHostGameService host, LanSettings settings)
    {
        var layer = new CanvasLayer { Layer = 30, Name = "LanCoopInvite" };
        root.AddChild(layer);
        var panel = new PanelContainer();
        layer.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        panel.OffsetLeft = -465; panel.OffsetRight = -20;
        panel.OffsetTop = 20;
        var body = new VBoxContainer { CustomMinimumSize = new Vector2(425, 0) };
        panel.AddChild(body);
        var toggle = new Button { Text = "邀请朋友 / 连接地址", CustomMinimumSize = new Vector2(0, 48) };
        body.AddChild(toggle);
        var content = new VBoxContainer { Visible = false };
        body.AddChild(content);
        toggle.Pressed += () => content.Visible = !content.Visible;
        var mode = new OptionButton();
        mode.AddItem("局域网"); mode.AddItem("Radmin VPN"); mode.AddItem("樱花 UDP 穿透");
        content.AddChild(mode);
        var addresses = new OptionButton();
        content.AddChild(addresses);
        var external = new LineEdit { Text = settings.ExternalEndpoint, PlaceholderText = "节点域名:外部端口", Visible = false };
        content.AddChild(external);
        var status = new Label { Text = $"房主监听 UDP {settings.HostPort}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        content.AddChild(status);
        var copy = new Button { Text = "复制连接地址", CustomMinimumSize = new Vector2(0, 48) };
        content.AddChild(copy);
        IReadOnlyList<LocalAddress> available = [];
        void Refresh()
        {
            addresses.Clear();
            var isExternal = mode.Selected == 2;
            external.Visible = isExternal;
            addresses.Visible = !isExternal;
            try
            {
                var all = LocalAddresses.Enumerate();
                available = mode.Selected == 1 ? all.Where(a => a.IsRadmin).ToArray() : all;
                // Fall back to manual adapter selection when Radmin cannot be identified by name.
                if (mode.Selected == 1 && available.Count == 0) available = all;
                foreach (var address in available) addresses.AddItem($"{address.Adapter} · {address.Endpoint(settings.HostPort)}");
                status.Text = isExternal ? "先在樱花映射此房间的 UDP 端口，再填写实际外部地址。" :
                    available.Count == 0 ? "未找到可用 IPv4 网卡，请检查网络。" : "选择朋友能访问的网卡地址。";
                copy.Disabled = !isExternal && available.Count == 0;
            }
            catch (Exception ex) { status.Text = $"网卡读取失败：{ex.Message}"; copy.Disabled = !isExternal; }
        }
        mode.ItemSelected += _ => Refresh();
        copy.Pressed += () =>
        {
            string value;
            if (mode.Selected == 2)
            {
                if (!Endpoint.TryParse(external.Text, out var endpoint, out var error)) { status.Text = error; return; }
                value = endpoint!.ToString();
                settings.ExternalEndpoint = value;
                try { LanSession.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { status.Text = $"外部地址保存失败：{ex.Message}"; return; }
            }
            else
            {
                if (addresses.Selected < 0 || addresses.Selected >= available.Count) return;
                value = available[addresses.Selected].Endpoint(settings.HostPort);
            }
            DisplayServer.ClipboardSet(value);
            status.Text = "地址已复制，将它发送给加入者。";
        };
        Action<MegaCrit.Sts2.Core.Entities.Multiplayer.NetErrorInfo> onDisconnected = _ =>
        { if (GodotObject.IsInstanceValid(layer)) layer.QueueFree(); };
        host.Disconnected += onDisconnected;
        layer.TreeExiting += () => host.Disconnected -= onDisconnected;
        Refresh();
    }
}
