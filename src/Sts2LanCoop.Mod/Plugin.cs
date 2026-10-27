using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform.Null;

namespace Sts2LanCoop.Mod;

[ModInitializer(nameof(Initialize))]
public static class Plugin
{
    public static void Initialize()
    {
        var harmony = new Harmony("sts2_lan_coop");
        try
        {
            harmony.PatchAll(typeof(Plugin).Assembly);
            ((SceneTree)Engine.GetMainLoop()).Root.CallDeferred(Node.MethodName.AddChild, new LanMobileLifecycle { Name = "LanMobileLifecycle" });
            ((SceneTree)Engine.GetMainLoop()).ProcessFrame += QuickReload.Tick;
            // 平台日志供期0真机核查OS.GetName()/OS.GetVersion()的实际返回值。
            GD.Print($"[STS2 LAN Co-op] 0.8.0-alpha.3 loaded on {LanCapability.Platform} {OS.GetVersion()}; one DLL supports v0.107.1 / v0.111.0; admission 5, SL 2, mod sync 2.");
            GD.Print($"[STS2 LAN Co-op] capabilities: join={Allowed(LanCapability.JoinBlockReason)}, host={Allowed(LanCapability.HostBlockReason)}, SL={Allowed(LanCapability.QuickReloadBlockReason)}, mod packages={LanCapability.CanInspectMods}, auto apply={Allowed(LanCapability.ModSyncBlockReason)}.");
        }
        catch
        {
            harmony.UnpatchAll(harmony.Id);
            throw;
        }
    }
    private static string Allowed(string? blockReason) => blockReason == null ? "allowed" : "blocked: " + blockReason;
}

[HarmonyPatch(typeof(NMultiplayerSubmenu), nameof(NMultiplayerSubmenu._Ready))]
internal static class MultiplayerMenuPatch
{
    private static void Postfix(NMultiplayerSubmenu __instance)
    {
        MultiplayerUi.Attach(__instance);
    }
}

// Change only the null-platform local identity while our own LAN session is active.
// Steam identity and the native game-message registry are untouched.
[HarmonyPatch(typeof(NullPlatformUtilStrategy), nameof(NullPlatformUtilStrategy.GetLocalPlayerId))]
internal static class LocalIdentityPatch
{
    private static bool Prefix(ref ulong __result)
    {
        if (!LanSession.Active) return true;
        __result = LanSession.LocalId;
        return false;
    }
}

[HarmonyPatch(typeof(NullPlatformUtilStrategy), nameof(NullPlatformUtilStrategy.GetPlayerName))]
internal static class LocalNamePatch
{
    private static bool Prefix(ulong playerId, ref string __result)
    {
        if (!LanSession.Active) return true;
        var name = playerId == LanSession.LocalId ? LanSession.Settings.PlayerName : AdmissionTransport.Name(playerId);
        if (name == null) return true;
        __result = name;
        return false;
    }
}
