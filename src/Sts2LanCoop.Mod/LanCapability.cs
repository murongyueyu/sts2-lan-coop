using Godot;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

// 平台能力矩阵的运行时投影：空值表示允许，否则是给玩家看的具体原因。
// 手机接入已由用户确认；其他能力分别开发与验收，未知平台一律按不支持处理。
internal static class LanCapability
{
    public static LanPlatformKind Kind { get; } = LanPlatform.Parse(OS.GetName());
    public static string Platform => OS.GetName();
    public static bool Windows => Kind == LanPlatformKind.Windows;
    public static bool Android => Kind == LanPlatformKind.Android;
    public static bool CanInspectMods => Windows || Android;
    public static PeerCapabilities Capabilities => new()
    {
        Platform = Android ? "Android" : Platform, ReloadVersion = LanPlatform.CanQuickReload(Kind) ? 3 : 0,
        RunStateVersion = 1
    };
    public static string? JoinBlockReason => LanPlatform.CanJoin(Kind) ? null :
        $"此开发版仅在 Windows 和 Android 提供连接入口（当前系统：{Platform}）。";
    public static string? HostBlockReason => LanPlatform.CanHost(Kind) ? null :
        Kind == LanPlatformKind.Android
            ? "手机做房主需要长期监听固定 UDP 端口，手机网络通常不可达；本期只开放客机加入。"
            : $"此开发版仅在 Windows 提供建房入口（当前系统：{Platform}）。";
    public static string? ModSyncBlockReason => LanPlatform.CanSyncMods(Kind) ? null :
        Android ? "当前支持比较/下载，尚无启动前应用接口；请退出后使用启动器手动导入兼容包。" :
        $"当前平台{Platform}不支持模组自动应用。";
    public static string? QuickReloadBlockReason => LanPlatform.CanQuickReload(Kind) ? null :
        "快速SL尚未在当前平台验证：全员重载要重建场景并写入存档，本期禁用。";
    public static string? SharedUnlockBlockReason => LanPlatform.CanShareUnlocks(Kind) ? null :
        "共用解锁需要写入原版与模组两份进度存档，当前平台的路径尚未验证，本期禁用。";
}
