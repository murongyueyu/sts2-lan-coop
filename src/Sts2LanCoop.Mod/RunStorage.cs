using System.Security.Cryptography;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class RunStorage
{
    private static readonly HashSet<string> _nativeSaveKeys = new(StringComparer.Ordinal);
    public static string PathFor(bool multiplayer) => ProjectSettings.GlobalizePath(GameBuild.ScopedPath(System.IO.Path.Combine(
        UserDataPathProvider.SavesDir, multiplayer ? RunSaveManager.multiplayerRunSaveFileName : RunSaveManager.runSaveFileName)));
    public static string Scope => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetDirectoryName(PathFor(false))!)))[..16];
    public static bool Solo => RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type == NetGameType.Singleplayer;
    public static string Key(SerializableRun run) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{run.StartTime}|{run.SerializableRng.Seed}|{run.GameMode}|{run.PlatformType}|" +
        string.Join(";", run.Players.OrderBy(p => p.NetId).Select(p => $"{p.NetId}:{p.CharacterId}")))));
    public static WorkingSaveLease Lease => new(ProjectSettings.GlobalizePath($"user://sts2_lan_coop/working/{Scope}.json"), PathFor(true));
    public static void AcquireLanSlot()
    {
        if (Lease.Exists) Lease.Restore();
        _nativeSaveKeys.Clear();
        Lease.Acquire(bytes =>
        {
            var run = SaveLibrary.Decode(bytes);
            if (run.PlatformType == PlatformType.Steam) return true;
            if (run.PlatformType == PlatformType.None && run.Players.Count is >= 2 and <= 4) return false;
            throw new InvalidDataException("无法识别当前多人工作存档，已保留原文件。");
        });
    }
    public static void TrackNativeSave(SerializableRun save, bool multiplayer)
    {
        if (multiplayer && LanSession.Active && LanSession.HostedService != null && save.PlatformType == PlatformType.None &&
            save.Players.Count is >= 2 and <= 4 && save.Players.Count(p => p.NetId == 1) == 1)
            _nativeSaveKeys.Add(Key(save));
    }
    public static void ObserveNativeWrite(GodotFileIo store, string path, byte[] bytes)
    {
        if (!LanSession.Active || LanSession.HostedService == null || !Lease.Exists || _nativeSaveKeys.Count == 0) return;
        var target = System.IO.Path.GetFullPath(ProjectSettings.GlobalizePath(store.GetFullPath(path)));
        if (!string.Equals(target, System.IO.Path.GetFullPath(PathFor(true)), StringComparison.OrdinalIgnoreCase)) return;
        var run = SaveLibrary.Decode(bytes);
        if (run.PlatformType != PlatformType.None || !_nativeSaveKeys.Contains(Key(run))) return;
        // Persist exact intended bytes before native backup rotation. Covers appenders such as JML/Ritsu,
        // without accepting an unknown existing backup or any write from a foreign process.
        Lease.Observe(bytes);
    }
    public static string ResolveWorkingConflict()
    {
        RequireIdle();
        return Lease.PreserveConflictAndRestore(ProjectSettings.GlobalizePath($"user://sts2_lan_coop/working-conflicts/{Scope}"));
    }
    public static void Recover()
    {
        if (SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false }) throw new IOException("请等待当前保存完成后再恢复工作存档。");
        if (!LanSession.Active && Lease.Exists) Lease.Restore();
        SaveLibrary.Catalog.RetryRetired(); SoloLibrary.Catalog.RetryRetired();
        RemoveWorking(SaveLibrary.Catalog, PathFor(true)); RemoveWorking(SoloLibrary.Catalog, PathFor(false));
    }
    public static async Task ReleaseLanSlot()
    {
        var scope = Scope; var lease = Lease;
        try
        {
            if (SaveManager.Instance.CurrentRunSaveTask is { } pending) await pending;
            if (Scope == scope && !LanSession.Active) lease.Restore();
        }
        catch (Exception ex) { GD.PushError("[STS2 LAN Co-op] 工作存档尚未归还，恢复记录已保留：" + ex); }
    }
    public static void RequireIdle()
    {
        if (QuickReload.Busy || SoloLibrary.Busy || LanSession.Active || RunManager.Instance.IsInProgress ||
            SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false })
            throw new InvalidOperationException("请先退出当前对局并等待保存、读档或快速SL完成。");
    }
    public static void ValidateEnvironment(RunEntry entry)
    {
        if (entry.GameVersion != GameBuild.Version) throw new InvalidOperationException("请使用此存档对应的游戏版本。");
        if (entry.ModEnvironment.Length > 0 && entry.ModEnvironment != ModSyncInventory.LastEnvironment &&
            !(entry.ModProfile.Count > 0 && ModSyncInventory.EnvironmentHash(entry.ModProfile) == ModSyncInventory.LastEnvironment))
            throw new InvalidOperationException("存档所需模组与本次加载不同，请先恢复对应模组。");
    }
    public static void RemoveWorking(RunCatalog catalog, string path)
    {
        foreach (var file in new[] { path, path + ".backup" })
        {
            if (!File.Exists(file)) continue;
            var bytes = File.ReadAllBytes(file); var run = SaveLibrary.Decode(bytes);
            if (catalog.IsRetired("", Key(run), Convert.ToHexString(SHA256.HashData(bytes)))) File.Delete(file);
        }
    }
    // A known relay can keep an adventure going after a vanilla end hook. Unknown extensions keep data.
    public static bool HasUnresolvedEnding()
    {
        var type = AccessTools.TypeByName("Sts2ModCompatibility.Mod.RelayCoordinator");
        if (type != null && (AccessTools.Property(type, "Active")?.GetValue(null) as bool? == true)) return true;
        var patches = Harmony.GetPatchInfo(AccessTools.Method(typeof(RunManager), nameof(RunManager.OnEnded)));
        // A foreign ending hook has no declared finality contract. Do not guess that its original call was the final ending.
        return patches != null && patches.Owners.Any(owner => owner != "sts2_lan_coop");
    }
    public static void Complete(SerializableRun run, bool solo)
    {
        if (HasUnresolvedEnding()) { GD.PushWarning("[STS2 LAN Co-op] 扩展接力结局未确认，保留续玩存档。"); return; }
        var catalog = solo ? SoloLibrary.Catalog : SaveLibrary.Catalog;
        var key = Key(run);
        var matching = catalog.Inspect().Where(i => i.Entry != null && (i.Entry.SourceKey == key ||
            Key(SaveLibrary.Decode(catalog.ReadPayload(i.Entry))) == key)).Select(i => i.Entry!).ToArray();
        foreach (var entry in matching) catalog.Retire(entry.Id, entry.Revision, "ended", key);
        RemoveWorking(catalog, PathFor(!solo));
        if (solo) SoloLibrary.Ended(); else SaveLibrary.Ended();
    }
}

[HarmonyPatch]
internal static class TrackNativeWorkingWrites
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
    {
        AccessTools.Method(typeof(GodotFileIo), nameof(GodotFileIo.WriteFile), [typeof(string), typeof(byte[])]),
        AccessTools.Method(typeof(GodotFileIo), nameof(GodotFileIo.WriteFileAsync), [typeof(string), typeof(byte[])])
    };
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(GodotFileIo __instance, string path, byte[] bytes) => RunStorage.ObserveNativeWrite(__instance, path, bytes);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
internal static class RetireFinishedRun
{
    private static void Prefix(RunManager __instance, out bool __state) => __state = __instance.ShouldSave &&
        (RunStorage.Solo && __instance.DebugOnlyGetState()?.GameMode == GameMode.Standard || LanSession.Active && LanSession.HostedService != null);
    private static void Postfix(bool __state, bool __runOriginal, SerializableRun __result)
    {
        if (!__state || !__runOriginal || __result == null) return;
        var solo = __result.Players.Count == 1;
        // Deletion of the native working slot is the successful native end commit, not merely a dead creature.
        if (File.Exists(RunStorage.PathFor(!solo)) || File.Exists(RunStorage.PathFor(!solo) + ".backup")) return;
        try { RunStorage.Complete(__result, solo); }
        catch (Exception ex) { GD.PushError("[STS2 LAN Co-op] 已结算，归档清理未完成，保留记录以便重试：" + ex); }
    }
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu.AbandonRun))]
internal static class RetireMenuAbandon
{
    private static void Prefix(out SerializableRun? __state)
    {
        __state = null;
        var path = RunStorage.PathFor(false);
        try
        {
            if (File.Exists(path))
            {
                var run = SaveLibrary.Decode(File.ReadAllBytes(path));
                if (run.GameMode == GameMode.Standard) __state = run;
            }
        }
        catch (Exception ex) { GD.PushWarning("[STS2 LAN Co-op] 原版放弃流程保留，无法确认归档来源：" + ex.Message); }
    }
    private static void Postfix(SerializableRun? __state, bool __runOriginal)
    {
        if (!__runOriginal || __state == null || File.Exists(RunStorage.PathFor(false))) return;
        try { RunStorage.Complete(__state, true); }
        catch (Exception ex) { GD.PushError("[STS2 LAN Co-op] 放弃后的清理未完成：" + ex); }
    }
}
