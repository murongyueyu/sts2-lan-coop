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
        ArchiveGeneration++;
    }
    public static async Task RestoreReloadPoint(byte[] bytes)
    {
        if (SaveManager.Instance.CurrentRunSaveTask is { } pending)
        { try { await pending; } catch (Exception ex) { GD.PushWarning("[STS2 LAN Co-op] 上次写入失败，尝试恢复固定保存点：" + ex.Message); } }
        var manager = (RunSaveManager)AccessTools.Field(typeof(SaveManager), "_runSaveManager").GetValue(SaveManager.Instance)!;
        await manager.SaveRun(Decode(bytes), true);
    }
    public static string WorkingPath => ProjectSettings.GlobalizePath(
        SaveManager.Instance.GetProfileScopedPath(Path.Combine(UserDataPathProvider.SavesDir, RunSaveManager.multiplayerRunSaveFileName)));
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
        _newName = RunCatalog.ValidateName(string.IsNullOrWhiteSpace(name) ? $"多人冒险 {DateTime.Now:MM-dd HH:mm}" : name);
        ActiveEntry = null; LastError = null;
    }
    public static void Select(RunEntry entry)
    {
        CheckIdleWrites();
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
    }
    private static void CheckIdleWrites()
    {
        if (QuickReload.Busy) throw new InvalidOperationException("请等待本次SL及恢复结束后再选择对局。");
        if (LanSession.Active) throw new InvalidOperationException("请先离开当前LAN会话。");
        if (SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false })
            throw new InvalidOperationException("上次保存尚未完成，请稍候重试。");
    }
    public static SerializableRun Decode(byte[] bytes) => JsonSerializer.Deserialize(bytes,
        JsonSerializationUtility.GetTypeInfo<SerializableRun>()) ?? throw new InvalidDataException("多人存档为空。");

    public static RunEntry? ImportWorking()
    {
        var path = File.Exists(WorkingPath) ? WorkingPath : WorkingPath + ".backup";
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var known = Catalog.List().FirstOrDefault(e => e.PayloadHash == hash);
        if (known != null) return known;
        var run = Decode(bytes);
        ValidateRun(run);
        return Catalog.Commit(Summarize(run) with { Name = $"导入的多人冒险 {DateTime.Now:MM-dd HH:mm}",
            Owner = LanSession.Settings.PlayerId }, bytes);
    }

    private static void ValidateRun(SerializableRun run)
    {
        if (run.PlatformType != PlatformType.None || run.GameMode != GameMode.Standard || run.Players.Count is < 2 or > 4 ||
            run.Players.Count(p => p.NetId == 1) != 1)
            throw new InvalidDataException("仅支持主机ID为1的普通LAN多人存档；已保留原文件。");
    }
    private static RunEntry Summarize(SerializableRun run) => new()
    {
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
        ValidateRun(written);
        var bytes = File.ReadAllBytes(WorkingPath);
        var reread = Decode(bytes);
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
    }
    public static void Rename(string id, string name)
    {
        var entry = Catalog.Rename(id, name);
        if (ActiveEntry?.Id == id) ActiveEntry = entry;
    }
    public static async Task EnsureSaved()
    {
        if (SaveManager.Instance.CurrentRunSaveTask is { } pending) await pending;
        if (LastError != null) throw new IOException("上次存档归档失败，尚未安全保存，已保留当前游戏。", LastError);
        if (ActiveEntry == null) throw new InvalidOperationException("尚无原版有效保存点，请稍后重试保存退出。");
        _ = Catalog.ReadPayload(ActiveEntry);
    }
    public static void Failed(Exception ex) => LastError = ex;
}

[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun), [typeof(SerializableRun), typeof(bool)])]
internal static class ArchiveNativeSavePatch
{
    private static void Postfix(SerializableRun save, bool isMultiplayer, ref Task __result)
    {
        if (isMultiplayer && LanSession.Active && LanSession.HostedService != null)
            __result = Capture(__result, save, SaveLibrary.ArchiveGeneration);
    }
    private static async Task Capture(Task original, SerializableRun save, long generation)
    {
        try
        {
            await original;
            if (generation != SaveLibrary.ArchiveGeneration) { GD.PushWarning("[STS2 LAN Co-op] 忽略已结束重载代次的迟到归档。"); return; }
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
        if (QuickReload.Busy) return false;
        if (!LanSession.Active || LanSession.HostedService == null) return true;
        if (!_busy) _ = Guard(__instance);
        return false;
    }
    private static async Task Guard(NPauseMenu menu)
    {
        _busy = true;
        try { await SaveLibrary.EnsureSaved(); await NGameReturn(); }
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
