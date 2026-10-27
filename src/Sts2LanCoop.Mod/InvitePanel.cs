using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class InvitePanel
{
    public static void Show(Node root, NetHostGameService host, LanSettings settings)
    {
        if (root.HasNode("LanCoopInvite")) return;
        var entry = new UiAction(root, "邀请朋友", () => Open(root, host, settings), "LanCoopInvite");
        entry.Node.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        entry.Node.OffsetLeft = -410; entry.Node.OffsetRight = -38; entry.Node.OffsetTop = 40; entry.Node.OffsetBottom = 120;
    }
    private static void Open(Node root, NetHostGameService host, LanSettings settings)
    {
        if (root.HasNode("LanInviteDetails")) return;
        var page = new NativePage { Name = "LanInviteDetails", Heading = "邀请朋友" };
        root.AddChild(page); page.CloseRequested = page.QueueFree;
        NativeUi.Label(page.Body, $"房主监听 UDP {settings.HostPort}", 28, true);
        var mode = new OptionButton { CustomMinimumSize = new Vector2(0, 54) };
        mode.AddItem("局域网"); mode.AddItem("Radmin VPN"); mode.AddItem("樱花 UDP 穿透"); page.Body.AddChild(mode);
        var addresses = new OptionButton { CustomMinimumSize = new Vector2(0, 54) }; page.Body.AddChild(addresses);
        var external = NativeUi.Input(page.Body, settings.ExternalEndpoint); external.PlaceholderText = "节点域名:外部端口";
        var status = NativeUi.Label(page.Body, "");
        IReadOnlyList<LocalAddress> available = [];
        void Refresh()
        {
            addresses.Clear(); external.Visible = mode.Selected == 2; addresses.Visible = !external.Visible;
            try
            {
                var all = LocalAddresses.Enumerate(); available = mode.Selected == 1 ? all.Where(a => a.IsRadmin).ToArray() : all;
                if (available.Count == 0 && mode.Selected == 1) available = all;
                foreach (var address in available) addresses.AddItem($"{address.Adapter} · {address.Endpoint(settings.HostPort)}");
                status.Text = external.Visible ? "请先映射房主UDP端口，再填写实际外部地址。" : "选择朋友能访问的网卡地址。";
            }
            catch (Exception ex) { status.Text = "网卡读取失败：" + ex.Message; }
        }
        mode.ItemSelected += _ => Refresh();
        new UiAction(page.Footer, "返回", page.QueueFree);
        new UiAction(page.Footer, "复制连接地址", () =>
        {
            if (mode.Selected == 2)
            {
                if (!Endpoint.TryParse(external.Text, out var endpoint, out var error)) { status.Text = error; return; }
                settings.ExternalEndpoint = endpoint!.ToString();
                try { LanSession.Save(); } catch (Exception ex) { status.Text = ex.Message; return; }
                DisplayServer.ClipboardSet(settings.ExternalEndpoint);
            }
            else
            {
                if (addresses.Selected < 0 || addresses.Selected >= available.Count) { status.Text = "请选择可访问的网卡。"; return; }
                DisplayServer.ClipboardSet(available[addresses.Selected].Endpoint(settings.HostPort));
            }
            status.Text = "地址已复制，将它发送给朋友即可。";
        });
        Refresh(); NativeUi.FocusLoop(page);
    }
}
