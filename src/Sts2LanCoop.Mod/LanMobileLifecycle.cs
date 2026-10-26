using Godot;

namespace Sts2LanCoop.Mod;

internal partial class LanMobileLifecycle : Node
{
    public static bool Foreground { get; private set; } = true;
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
        if (!foreground) QuickReload.OnBackgrounded();
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
