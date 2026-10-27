using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

namespace Sts2LanCoop.Mod;

// Like the native character-change guard, do not update the retired lobby UI
// when a late ready/unready message arrives after the run has begun.
[HarmonyPatch(typeof(StartRunLobby), "HandlePlayerReadyMessage")]
internal static class LobbyTransitionGuard
{
    private static readonly System.Reflection.FieldInfo? Beginning = AccessTools.Field(typeof(StartRunLobby), "_isBeginningRun");
    private static bool _logged;
    private static bool Prefix(StartRunLobby __instance)
    {
        if (!LanSession.Active || Beginning?.GetValue(__instance) is not true) return true;
        if (!_logged)
        {
            _logged = true;
            GD.Print("[STS2 LAN Co-op] 对局已开始，忽略旧大厅迟到的准备状态更新，避免访问已销毁的选角界面。");
        }
        return false;
    }
}
