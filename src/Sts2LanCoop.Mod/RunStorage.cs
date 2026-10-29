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
        if (type != null && AccessTools.Property(type, "Active")?.GetValue(null) is true)
        {
            // The known relay gates WinRun before OnEnded. Confirm its ledger has no remaining leg.
            try
            {
                var adapter = AccessTools.TypeByName("Sts2ModCompatibility.Mod.RelayLedgerAdapter");
                var relay = AccessTools.TypeByName("Sts2ModCompatibility.Core.ActRelay");
                var state = RunManager.Instance.DebugOnlyGetState();
                if (adapter == null || relay == null || state == null) return true;
                var ledger = AccessTools.Method(adapter, "Read").Invoke(null, [state]);
                if (ledger != null && AccessTools.Property(ledger.GetType(), "Started")?.GetValue(ledger) is true &&
                    (AccessTools.Property(ledger.GetType(), "BridgePending")?.GetValue(ledger) is true ||
                     AccessTools.Method(relay, "Next").Invoke(null, [ledger]) != null)) return true;
            }
            catch (Exception ex) { GD.PushWarning("[STS2 LAN Co-op] 接力结局无法确认：" + ex.Message); return true; }
        }
        var patches = Harmony.GetPatchInfo(AccessTools.Method(typeof(RunManager), nameof(RunManager.OnEnded)));
        // A foreign ending hook has no declared finality contract. Do not guess that its original call was the final ending.
        return patches != null && patches.Owners.Any(owner => owner != "sts2_lan_coop");
    }
    public static void Complete(SerializableRun run, bool solo, bool explicitEnd = false, string reason = "ended")
    {
        if (!explicitEnd && !solo && !RunStateTrust.Trusted)
        {
            GD.PushWarning("[STS2 LAN Co-op] 本局已失步，结束通知不清理最近有效续玩归档；恢复或手动管理后再处理。");
            return;
        }
        if (!explicitEnd && HasUnresolvedEnding()) { GD.PushWarning("[STS2 LAN Co-op] 扩展接力结局未确认，保留续玩存档。"); return; }
        var catalog = solo ? SoloLibrary.Catalog : SaveLibrary.Catalog;
        var key = Key(run);
        var matching = catalog.Inspect().Where(i => i.Entry != null && Matches(catalog, i.Entry, key)).Select(i => i.Entry!).ToArray();
        if (matching.Length == 0 && !catalog.IsRetired("", key))
            catalog.Retire(Guid.NewGuid().ToString("N"), null, reason, key);
        foreach (var entry in matching) catalog.Retire(entry.Id, entry.Revision, reason, key);
        catalog.RetryRetired();
        RemoveWorking(catalog, PathFor(!solo));
        if (solo) SoloLibrary.Ended(); else SaveLibrary.Ended();
    }
    private static bool Matches(RunCatalog catalog, RunEntry entry, string key)
    {
        if (entry.SourceKey.Length > 0) return entry.SourceKey == key;
        try { return Key(SaveLibrary.Decode(catalog.ReadPayload(entry))) == key; }
        catch (Exception ex)
        {
            GD.PushWarning("[STS2 LAN Co-op] 旧归档无法确认来源，保留此条目：" + entry.Id + "，" + ex.Message);
            return false;
        }
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
        AccessTools.Field(typeof(RunManager), "_runHistoryWasUploaded").GetValue(__instance) is not true &&
        __instance.DebugOnlyGetState()?.GameMode == GameMode.Standard && (RunStorage.Solo || LanSession.Active);
    private static void Postfix(RunManager __instance, bool isVictory, bool __state, bool __runOriginal, SerializableRun __result)
    {
        if (!__state || !__runOriginal || __result == null) return;
        var solo = __result.Players.Count == 1;
        if (solo || LanSession.Active)
        {
            if (!isVictory && !__instance.IsAbandoned)
            {
                if (__instance.DebugOnlyGetState()?.Players.All(p => p.Creature.IsDead) != true) return;
                if (solo) SoloLibrary.FreezeEnding();
                else if (LanSession.HostedService != null) SaveLibrary.FreezeEnding();
                RunEndFlow.Begin(__result);
                return;
            }
            if (!solo && LanSession.HostedService == null) return;
            if (!__instance.IsAbandoned && RunStorage.HasUnresolvedEnding())
            {
                GD.PushWarning("[STS2 LAN Co-op] 最终结局尚无法确认，续玩归档保留，请在存档管理中处理。");
                NativeUi.Notice(((SceneTree)Engine.GetMainLoop()).Root, "续玩存档已保留", "有模组修改了结束流程，暂时无法确认最终结局。请在存档管理中确认并删除本局存档。");
                return;
            }
            if (solo) SoloLibrary.FreezeEnding(); else SaveLibrary.FreezeEnding();
            _ = CompleteEnded(__result, solo, __instance.IsAbandoned ? "abandoned" : "victory");
            return;
        }
    }
    private static async Task CompleteEnded(SerializableRun run, bool solo, string reason)
    {
        var catalog = solo ? SoloLibrary.Catalog : SaveLibrary.Catalog;
        var path = solo ? SoloLibrary.WorkingPath : SaveLibrary.WorkingPath;
        try
        {
            // Persist retirement before yielding or disconnecting. A late writer checks this tombstone.
            RunStorage.Complete(run, solo, explicitEnd: true, reason: reason);
            if (SaveManager.Instance.CurrentRunSaveTask is { } pending)
            {
                try { await pending; }
                catch (Exception ex) { GD.PushWarning("[STS2 LAN Co-op] 结束时保存失败，仍清理已确认终局：" + ex.Message); }
            }
            RunStorage.RemoveWorking(catalog, path);
        }
        catch (Exception ex)
        {
            GD.PushError("[STS2 LAN Co-op] 已结算，清理待重试：" + ex);
            NativeUi.Notice(((SceneTree)Engine.GetMainLoop()).Root, "存档清理待重试", "本局已结束，存档清理未完成。请返回存档管理或重启重试。\n" + ex.Message);
        }
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
        try { RunStorage.Complete(__state, true, explicitEnd: true, reason: "abandoned"); }
        catch (Exception ex) { GD.PushError("[STS2 LAN Co-op] 放弃后的清理未完成：" + ex); }
    }
}

// Native multiplayer abandon disconnects the host BEFORE its asynchronous kill-all reaches OnEnded.
// Capture authority and identity at the confirmed action, not from LanSession.Active at game over.
[HarmonyPatch(typeof(RunManager), nameof(RunManager.Abandon))]
internal static class RetireLanAbandon
{
    private static void Prefix(RunManager __instance, out SerializableRun? __state)
    {
        __state = null;
        if (!LanSession.Active || LanSession.HostedService == null || !__instance.ShouldSave ||
            __instance.DebugOnlyGetState()?.GameMode != GameMode.Standard) return;
        __state = __instance.ToSave(null);
    }
    private static void Postfix(SerializableRun? __state, bool __runOriginal)
    {
        if (__state == null || !__runOriginal) return;
        SaveLibrary.FreezeEnding();
        try { RunStorage.Complete(__state, false, explicitEnd: true, reason: "abandoned"); }
        catch (Exception ex)
        {
            GD.PushError("[STS2 LAN Co-op] 主机已放弃，归档清理待重试：" + ex);
            NativeUi.Notice(((SceneTree)Engine.GetMainLoop()).Root, "存档清理待重试", "主机已确认放弃，本局归档清理未完成。\n" + ex.Message);
        }
    }
}
