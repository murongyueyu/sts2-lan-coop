using Godot;

namespace Sts2LanCoop.Mod;

internal partial class LanMobileLifecycle : Node
{
    public static bool Foreground { get; private set; } = true;
    private static bool _hosting;
    private static bool _keptOn;
    private static ulong _hostLeftAt;
    // 手机主机进后台时主循环会停，客机可能超时：建房期间保持屏幕常亮，回前台后提示。
    public static void HostingStarted()
    {
        if (!LanCapability.Android || _hosting) return;
        _hosting = true; _hostLeftAt = 0;
        _keptOn = DisplayServer.ScreenIsKeptOn();
        DisplayServer.ScreenSetKeepOn(true);
    }
    public static void HostingEnded()
    {
        if (!_hosting) return;
        _hosting = false; _hostLeftAt = 0;
        DisplayServer.ScreenSetKeepOn(_keptOn);
    }
    private bool _paused;
    private bool _focused = true;
    public override void _Notification(int what)
    {
        if (!LanCapability.Android) return;
        if (what == MainLoop.NotificationApplicationPaused) _paused = true;
        else if (what == MainLoop.NotificationApplicationResumed) _paused = false;
        else if (what == MainLoop.NotificationApplicationFocusOut) _focused = false;
        else if (what == MainLoop.NotificationApplicationFocusIn) _focused = true;
        else return;
        SetForeground(!_paused && _focused);
    }
    internal static void SetForeground(bool foreground)
    {
        Foreground = foreground;
        if (!foreground)
        {
            if (_hosting && _hostLeftAt == 0) _hostLeftAt = Time.GetTicksMsec();
            QuickReload.OnBackgrounded();
        }
        else if (_hosting && _hostLeftAt != 0)
        {
            var away = (Time.GetTicksMsec() - _hostLeftAt) / 1000;
            _hostLeftAt = 0;
            GD.Print($"[STS2 LAN Co-op] 手机主机回到前台，离开约{away}秒。");
            if (away >= 3) NativeUi.Notice(((SceneTree)Engine.GetMainLoop()).Root, "房主刚才离开了游戏",
                $"离开约 {away} 秒。期间朋友可能已掉线，请确认人数；开房和对局时请让游戏保持在前台。");
        }
    }
    internal static void ProbeSaveAccess()
    {
        if (!LanCapability.Android) return;
        var root = Path.GetDirectoryName(SaveLibrary.WorkingPath) ?? throw new IOException("无法确定手机多人存档目录。");
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, ".lan-sl-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None))
            { stream.WriteByte(0x53); stream.Flush(true); }
            if (!File.ReadAllBytes(probe).SequenceEqual(new byte[] {0x53})) throw new IOException("手机存档目录读回校验失败。");
        }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }
}
