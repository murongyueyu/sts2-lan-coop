using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Models;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class SaveLibrary
{
    public static RunEntry? ActiveEntry { get; private set; }
    private static string? _newId;
    private static string _newName = "";
    public static Exception? LastError { get; private set; }
    private static RunEntry? _reloadPin;
    private static (RunEntry Summary, byte[] Bytes)? _reloadWrite;
    public static long ArchiveGeneration { get; private set; }
    private static bool _ended;
    private static bool _ending;
    // Stop late native/extension saves from replacing the last playable checkpoint at game over.
    public static void FreezeEnding() { _ending = true; ArchiveGeneration++; }
    public static void HoldForReload(RunEntry entry)
    {
        if (_reloadPin != null || ActiveEntry?.Revision != entry.Revision) throw new InvalidOperationException("保存点已变化，请重新发起SL。");
        _reloadPin = entry; _reloadWrite = null;
        ArchiveGeneration++;
    }
    public static void FinishReload(bool success)
    {
        if (_reloadPin == null) return;
        if (success && _reloadWrite is { } written)
        {
            ActiveEntry = Catalog.Commit(written.Summary with { Id = _reloadPin.Id, Name = _reloadPin.Name, Owner = _reloadPin.Owner }, written.Bytes);
            LastError = null;
        }
        // On failure the existing manifest continues to refer to the validated recovery point.
        _reloadPin = null; _reloadWrite = null;
        if (success) _ending = false;
        ArchiveGeneration++;
    }
    public static async Task RestoreReloadPoint(byte[] bytes)
    {
        if (SaveManager.Instance.CurrentRunSaveTask is { } pending)
        { try { await pending; } catch (Exception ex) { GD.PushWarning("[STS2 LAN Co-op] 上次写入失败，尝试恢复固定保存点：" + ex.Message); } }
        var manager = (RunSaveManager)AccessTools.Field(typeof(SaveManager), "_runSaveManager").GetValue(SaveManager.Instance)!;
        await manager.SaveRun(Decode(bytes, attachExtensions: true), true);
    }
    public static string WorkingPath => ProjectSettings.GlobalizePath(
        GameBuild.ScopedPath(Path.Combine(UserDataPathProvider.SavesDir, RunSaveManager.multiplayerRunSaveFileName)));
    public static RunCatalog Catalog
    {
        get
        {
            var profile = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetDirectoryName(WorkingPath)!)))[..16];
            return new RunCatalog(ProjectSettings.GlobalizePath("user://sts2_lan_coop/catalog/" + profile));
        }
    }
    public static void New(string? name)
    {
        CheckIdleWrites();
        ImportWorking();
        _newId = Guid.NewGuid().ToString("N");
        _ended = false; _ending = false; ArchiveGeneration++;
        _newName = RunCatalog.ValidateName(string.IsNullOrWhiteSpace(name) ? $"多人冒险 {DateTime.Now:MM-dd HH:mm}" : name);
        ActiveEntry = null; LastError = null;
    }
    public static void Select(RunEntry entry)
    {
        CheckIdleWrites();
        if(entry.GameVersion!=GameBuild.Version)throw new InvalidOperationException("存档来自不同游戏版本，请使用对应版本；不能直接跨版本续局。");
        if(entry.ModEnvironment.Length>0 && entry.ModEnvironment!=ModSyncInventory.LastEnvironment &&
            !(entry.ModProfile.Count>0 && ModSyncInventory.EnvironmentHash(entry.ModProfile)==ModSyncInventory.LastEnvironment))
            throw new InvalidOperationException("存档模组环境与本次加载不同（或尚在检查）。请恢复该存档的模组版本后重试。");
        ImportWorking();
        if (entry.Owner != LanSession.Settings.PlayerId) throw new InvalidOperationException("此存档属于另一主机身份，不能更换主机角色。");
        var run = Decode(Catalog.ReadPayload(entry));
        ValidateRun(run);
        if (run.Players.Count != entry.PlayerCount || !run.Players.Select(p => p.NetId).Order().SequenceEqual(entry.Slots.Select(s => s.NativeId).Order()))
            throw new InvalidDataException("存档索引与实际角色不一致，未加载。");
        ActiveEntry = entry; _newId = null; LastError = null;
        _ended = false; _ending = false; ArchiveGeneration++;
    }
    private static void CheckIdleWrites()
    {
        if (QuickReload.Busy) throw new InvalidOperationException("请等待本次SL及恢复结束后再选择对局。");
        if (LanSession.Active) throw new InvalidOperationException("请先离开当前LAN会话。");
        if (SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false })
            throw new InvalidOperationException("上次保存尚未完成，请稍候重试。");
    }
    public static SerializableRun Decode(byte[] bytes, bool attachExtensions = false)
    {
        var save = JsonSerializer.Deserialize(bytes, JsonSerializationUtility.GetTypeInfo<SerializableRun>())
            ?? throw new InvalidDataException("多人存档为空。");
        if (attachExtensions)
        {
            if (LanSession.Active) NeuvilletteRunInterop.ValidateSave(bytes);
            RunSaveExtensions.Attach(save, bytes);
        }
        return save;
    }

    public static RunEntry? ImportWorking()
    {
        var path = File.Exists(WorkingPath) ? WorkingPath : WorkingPath + ".backup";
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var known = Catalog.Inspect().Select(i => i.Entry).FirstOrDefault(e => e?.PayloadHash == hash);
        if (known != null) return known;
        var run = Decode(bytes);
        if (run.PlatformType != PlatformType.None) return null; // Native Steam is owned by the working-slot bridge.
        ValidateRun(run);
        if (RunStateTrust.Keys.Contains(RunStorage.Key(run))) return null;
        if (Catalog.IsRetired("", RunStorage.Key(run), hash)) return null;
        var older = Catalog.Inspect().FirstOrDefault(i => i.Entry?.SourceKey == RunStorage.Key(run))?.Entry;
        if (older != null) return older;
        return Catalog.Commit(Summarize(run) with { Name = $"导入的多人冒险 {DateTime.Now:MM-dd HH:mm}",
            Owner = LanSession.Settings.PlayerId }, bytes);
    }

    private static void ValidateRun(SerializableRun run)
    {
        if (run.PlatformType != PlatformType.None || run.GameMode != GameMode.Standard || run.Players.Count is < 2 or > 4 ||
            run.Players.Count(p => p.NetId == 1) != 1)
            throw new InvalidDataException("仅支持主机ID为1的普通LAN多人存档；已保留原文件。");
    }
    internal static RunEntry Summarize(SerializableRun run) => new()
    {
        SourceKey = RunStorage.Key(run), GameMode = run.GameMode.ToString(),
        GameVersion=GameBuild.Version,
        PlayerCount = run.Players.Count, Act = run.CurrentActIndex + 1,
        Floor = run.VisitedMapCoords.Count,
        Slots = run.Players.Select(p => new SlotSummary
        {
            NativeId = p.NetId, Character = p.CharacterId?.Entry ?? "未知角色", Hp = p.CurrentHp, MaxHp = p.MaxHp, Gold = p.Gold,
            CharacterTitle = ModelDb.GetById<CharacterModel>(p.CharacterId!).Title.GetFormattedText(), Titles = Titles(p),
            Relics = p.Relics.Select(SavedItemText).ToList(), Deck = p.Deck.Select(SavedItemText).ToList(),
            Potions = p.Potions.Select(SavedItemText).ToList()
        }).ToList()
    };
    private static Dictionary<string, string> Titles(SerializablePlayer player)
    {
        var titles = new Dictionary<string, string>();
        foreach (var relic in player.Relics) titles[SavedItemText(relic)] = ModelDb.GetById<RelicModel>(relic.Id!).Title.GetFormattedText();
        foreach (var potion in player.Potions) titles[SavedItemText(potion)] = ModelDb.GetById<PotionModel>(potion.Id!).Title.GetFormattedText();
        foreach (var card in player.Deck) titles[SavedItemText(card)] = ModelDb.GetById<CardModel>(card.Id!).TitleLocString.GetFormattedText() +
            (card.CurrentUpgradeLevel > 0 ? $" +{card.CurrentUpgradeLevel}" : "");
        return titles;
    }
    private static string SavedItemText(object item)
    {
        var type = item.GetType();
        var id = type.GetProperty("Id")?.GetValue(item) ?? type.GetProperty("CardId")?.GetValue(item);
        var upgrades = type.GetProperty("CurrentUpgradeLevel")?.GetValue(item);
        return (id?.ToString() ?? item.ToString() ?? "未知内容") + (upgrades is int level && level > 0 ? $" +{level}" : "");
    }
    public static void Capture(SerializableRun written)
    {
        if (_ended || _ending && _reloadPin == null) return;
        ValidateRun(written);
        var bytes = File.ReadAllBytes(WorkingPath);
        RunStorage.Lease.Observe(bytes);
        var reread = Decode(bytes);
        if (!RunStateTrust.Trusted && _reloadPin == null)
        {
            RunStateTrust.Protect(reread);
            return;
        }
        if (reread.StartTime != written.StartTime || !reread.Players.Select(p => p.NetId).Order().SequenceEqual(written.Players.Select(p => p.NetId).Order()))
            throw new InvalidDataException("原版写入读回与当前对局不一致，未提交存档库。");
        var summary = Summarize(reread) with {ModEnvironment=ModSyncInventory.LastEnvironment,ModProfile=ModSyncInventory.LastProfile};
        if (_reloadPin != null) { _reloadWrite = (summary, bytes); return; }
        var current = ActiveEntry;
        ActiveEntry = Catalog.Commit(summary with
        {
            Id = current?.Id ?? _newId ?? Guid.NewGuid().ToString("N"),
            Name = current?.Name ?? (string.IsNullOrEmpty(_newName) ? $"多人冒险 {DateTime.Now:MM-dd HH:mm}" : _newName),
            Owner = current?.Owner ?? LanSession.Settings.PlayerId
        }, bytes);
        _newId = null; LastError = null;
        Catalog.Prune(ActiveEntry);
    }
    public static void Rename(string id, string name)
    {
        var entry = Catalog.Rename(id, name);
        if (ActiveEntry?.Id == id) ActiveEntry = entry;
    }
    public static async Task EnsureSaved(bool frozenCheckpoint = false)
    {
        if (frozenCheckpoint && !_ending) throw new InvalidOperationException("没有已固定的失败保存点。");
        if (SaveManager.Instance.CurrentRunSaveTask is { } pending)
        {
            try { await pending; }
            catch (Exception ex) when (frozenCheckpoint)
            { GD.PushWarning("[STS2 LAN Co-op] 结束时写入失败，复核已固定的有效保存点：" + ex.Message); }
        }
        if (!frozenCheckpoint && LastError != null) throw new IOException("上次存档归档失败，尚未安全保存，已保留当前游戏。", LastError);
        if (ActiveEntry == null) throw new InvalidOperationException("尚无原版有效保存点，请稍后重试保存退出。");
        _ = Catalog.ReadPayload(ActiveEntry);
        // Recovery is allowed only after the durable frozen payload has passed verification again.
        if (frozenCheckpoint) LastError = null;
    }
    public static void Failed(Exception ex) => LastError = ex;
    public static void Ended() { _ended = true; ActiveEntry = null; _newId = null; ArchiveGeneration++; }
    public static void Delete(string id, string? revision)
    {
        RunStorage.RequireIdle(); Catalog.Retire(id, revision, "deleted");
        RunStorage.RemoveWorking(Catalog, WorkingPath);
        if (ActiveEntry?.Id == id) ActiveEntry = null;
        ArchiveGeneration++;
    }
}

[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun), [typeof(SerializableRun), typeof(bool)])]
internal static class ArchiveNativeSavePatch
{
    private static void Prefix(SerializableRun save, bool isMultiplayer) => RunStorage.TrackNativeSave(save, isMultiplayer);
    // Wrap the completed extension chain, so the archive includes append-only save metadata.
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(SerializableRun save, bool isMultiplayer, ref Task __result)
    {
        if (isMultiplayer && LanSession.Active && LanSession.HostedService != null)
            __result = Capture(__result, save, SaveLibrary.ArchiveGeneration, RunStorage.Scope);
        else if (!isMultiplayer && save.Players.Count == 1 && save.GameMode == GameMode.Standard)
            __result = CaptureSolo(__result, save, SoloLibrary.Generation, RunStorage.Scope);
    }
    private static async Task CaptureSolo(Task original, SerializableRun save, long generation, string scope)
    {
        try
        {
            await original;
            if (scope != RunStorage.Scope) return;
            if (generation != SoloLibrary.Generation)
            { RunStorage.RemoveWorking(SoloLibrary.Catalog, SoloLibrary.WorkingPath); return; }
            SoloLibrary.Capture(save);
        }
        catch (Exception ex) { SoloLibrary.Failed(ex); GD.PushError("[STS2 LAN Co-op] 单人归档失败：" + ex); throw; }
    }
    private static async Task Capture(Task original, SerializableRun save, long generation, string scope)
    {
        try
        {
            await original;
            if (scope != RunStorage.Scope) return;
            if (generation != SaveLibrary.ArchiveGeneration)
            { RunStorage.RemoveWorking(SaveLibrary.Catalog, SaveLibrary.WorkingPath); GD.PushWarning("[STS2 LAN Co-op] 忽略已结束重载代次的迟到归档。"); return; }
            SaveLibrary.Capture(save);
        }
        catch (Exception ex) { SaveLibrary.Failed(ex); GD.PushError($"[STS2 LAN Co-op] 保存归档失败：{ex}"); throw; }
    }
}

[HarmonyPatch(typeof(NPauseMenu), "OnSaveAndQuitButtonPressed")]
internal static class SaveExitGuard
{
    private static bool _busy;
    private static bool Prefix(NPauseMenu __instance)
    {
        if (QuickReload.Busy || SoloLibrary.Busy) return false;
        if (RunStorage.Solo && RunManager.Instance.DebugOnlyGetState()?.GameMode == GameMode.Standard)
        {
            if (!_busy) _ = GuardSolo(__instance);
            return false;
        }
        if (!LanSession.Active || LanSession.HostedService == null) return true;
        if (!_busy) _ = Guard(__instance);
        return false;
    }
    private static async Task GuardSolo(NPauseMenu menu)
    {
        _busy = true;
        try { await SoloLibrary.EnsureSaved(); await NGameReturn(); }
        catch (Exception ex) { NativeUi.Notice(menu, "保存并退出未完成", ex.Message); }
        finally { _busy = false; }
    }
    private static async Task Guard(NPauseMenu menu)
    {
        _busy = true;
        try
        {
            await SaveLibrary.EnsureSaved();
            if (!RunStateTrust.Trusted && !await NativeUi.Confirm(menu, "保留有效保存点并退出", "本局已发生状态失步。将保留最近有效归档，失步后的进度不会覆盖它。", "退出")) return;
            await NGameReturn();
        }
        catch (Exception ex)
        {
            GD.PushError($"[STS2 LAN Co-op] 保存并退出被阻止：{ex}");
            var dialog = new AcceptDialog { Title = "保存并退出未完成", DialogText = ex.Message };
            menu.AddChild(dialog); dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
        }
        finally { _busy = false; }
    }
    private static Task NGameReturn() => MegaCrit.Sts2.Core.Nodes.NGame.Instance!.ReturnToMainMenu();
}
