namespace Sts2LanCoop.Core;

public enum LanPlatformKind { Windows, Android, Unsupported }

// Android开放客机和全员SL；自动应用、建房和共用解锁保持Windows专属。
// 这里只做平台判定，具体原因文案由Mod侧组装，便于真机日志核对OS.GetName()的实际返回值。
public static class LanPlatform
{
    public static LanPlatformKind Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "windows" => LanPlatformKind.Windows,
        "android" => LanPlatformKind.Android,
        _ => LanPlatformKind.Unsupported
    };
    public static bool CanJoin(LanPlatformKind kind) => kind is LanPlatformKind.Windows or LanPlatformKind.Android;
    public static bool CanHost(LanPlatformKind kind) => kind == LanPlatformKind.Windows;
    public static bool CanSyncMods(LanPlatformKind kind) => kind == LanPlatformKind.Windows;
    public static bool CanQuickReload(LanPlatformKind kind) => kind is LanPlatformKind.Windows or LanPlatformKind.Android;
    public static bool CanShareUnlocks(LanPlatformKind kind) => kind == LanPlatformKind.Windows;
}
