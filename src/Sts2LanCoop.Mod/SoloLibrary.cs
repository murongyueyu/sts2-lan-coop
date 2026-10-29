using System.Security.Cryptography;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class SoloLibrary
{
    private static RunEntry? _active;
    private static string _scope = "";
    private static string? _newId;
    private static string _name = "";
    private static bool _ended;
    private static bool _ending;
    public static void FreezeEnding() { Scope(); _ending = true; Generation++; }
    private static RunEntry? _pin;
    private static (RunEntry Entry, byte[] Bytes)? _written;
    private static CancellationTokenSource _waits = new();
    private static readonly AsyncLocal<CancellationToken> Loading = new();
    public static bool Busy { get; private set; }
    public static long Generation { get; private set; }
    public static Exception? LastError { get; private set; }
    public static CancellationToken WaitToken => _waits.Token;
    public static RunEntry? ActiveEntry { get { Scope(); return _active; } }
    public static string WorkingPath => RunStorage.PathFor(false);
    public static RunCatalog Catalog => new(ProjectSettings.GlobalizePath($"user://sts2_lan_coop/solo/{RunStorage.Scope}"), RunKind.Solo);
    private static void Scope()
    {
        if (_scope == RunStorage.Scope) return;
        if (Busy) throw new InvalidOperationException("读档期间不能切换配置档。");
        _scope = RunStorage.Scope; _active = null; _newId = null; _ended = false; _ending = false; LastError = null; Generation++;
    }
    public static void CheckCancellation() => Loading.Value.ThrowIfCancellationRequested();
    public static void Validate(SerializableRun run)
    {
        if (run.Players.Count != 1 || run.GameMode != GameMode.Standard)
            throw new InvalidDataException("本版单人存档管理先支持普通冒险；原文件已保留。");
    }
    public static RunEntry? ImportWorking()
    {
        Scope();
        if (_ending) return _active;
        if (Busy || SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false }) return _active;
        var path = File.Exists(WorkingPath) ? WorkingPath : WorkingPath + ".backup";
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path); var run = SaveLibrary.Decode(bytes); Validate(run);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)); var key = RunStorage.Key(run);
        if (Catalog.IsRetired("", key, hash)) return null;
        var known = Catalog.Inspect().Select(i => i.Entry).FirstOrDefault(e => e != null && (e.PayloadHash == hash || e.SourceKey == key));
        // A crash after game over can leave a death snapshot in the native slot. Never import it over a playable archive.
        if (run.Players.All(p => p.CurrentHp <= 0)) return known;
        _active = known != null && known.PayloadHash == hash ? known : Catalog.Commit(SaveLibrary.Summarize(run) with
        {
            Id = known?.Id ?? Guid.NewGuid().ToString("N"), Kind = RunKind.Solo, SourceKey = key,
            Owner = known?.Owner ?? run.Players[0].NetId, Name = known?.Name ?? $"单人冒险 {DateTime.Now:MM-dd HH:mm}"
        }, bytes);
        return _active;
    }
    public static void New(string? name = null)
    {
        RunStorage.RequireIdle(); Scope(); ImportWorking();
        _newId = Guid.NewGuid().ToString("N"); _name = RunCatalog.ValidateName(name ?? $"单人冒险 {DateTime.Now:MM-dd HH:mm}");
        RunEndFlow.Reset();
        _active = null; _ended = false; _ending = false; LastError = null; Generation++;
    }
    public static void Capture(SerializableRun written)
    {
        Scope(); Validate(written); if (_ended || _ending && _pin == null) return;
        var bytes = File.ReadAllBytes(WorkingPath); var actual = SaveLibrary.Decode(bytes);
        if (RunStorage.Key(actual) != RunStorage.Key(written)) throw new IOException("单人保存读回与本局不一致。");
        var current = _pin ?? _active;
        var next = SaveLibrary.Summarize(actual) with
        {
            Kind = RunKind.Solo, Id = current?.Id ?? _newId ?? Guid.NewGuid().ToString("N"),
            SourceKey = RunStorage.Key(actual), Owner = current?.Owner ?? actual.Players[0].NetId,
            Name = current?.Name ?? (_name.Length == 0 ? $"单人冒险 {DateTime.Now:MM-dd HH:mm}" : _name),
            ModEnvironment = ModSyncInventory.LastEnvironment, ModProfile = ModSyncInventory.LastProfile
        };
        if (_pin != null) { _written = (next, bytes); return; }
        _active = Catalog.Commit(next, bytes); _newId = null; LastError = null;
        Catalog.Prune(_active);
    }
    public static void Failed(Exception ex) => LastError = ex;
    public static async Task EnsureSaved(bool frozenCheckpoint = false)
    {
        if (frozenCheckpoint && !_ending) throw new InvalidOperationException("没有已固定的失败保存点。");
        if (SaveManager.Instance.CurrentRunSaveTask is { } pending)
        {
            try { await pending; }
            catch (Exception ex) when (frozenCheckpoint)
            { GD.PushWarning("[STS2 LAN Co-op] 单人结束时写入失败，复核固定保存点：" + ex.Message); }
        }
        if (!frozenCheckpoint && LastError != null) throw new IOException("单人归档尚未完成，已保留当前对局。", LastError);
        if (ActiveEntry == null) throw new InvalidOperationException("尚无有效单人保存点。");
        _ = Catalog.ReadPayload(ActiveEntry);
        if (frozenCheckpoint) LastError = null;
    }
    public static void Rename(string id, string name)
    {
        RunStorage.RequireIdle(); var entry = Catalog.Rename(id, name);
        if (_active?.Id == id) _active = entry;
    }
    public static void Delete(string id, string? revision)
    {
        RunStorage.RequireIdle(); Catalog.Retire(id, revision, "deleted");
        RunStorage.RemoveWorking(Catalog, WorkingPath);
        if (_active?.Id == id) _active = null;
        Generation++;
    }
    public static void Ended() { _ended = true; _active = null; _newId = null; Generation++; }
    public static string? BlockReason(string ending = "")
    {
        var retryEnding = RunEndFlow.CanRetry(ending);
        if (RunEndFlow.Pending && !retryEnding) return "本局已失败，请选择保留、读档重试或删除。";
        if (ending.Length > 0 && !retryEnding) return "失败处理已过期，请从存档重新继续。";
        if (!GameBuild.Supported) return "此游戏版本尚不支持快速SL。";
        if (!RunStorage.Solo || !RunManager.Instance.ShouldSave || RunManager.Instance.IsGameOver && !retryEnding) return "请先进入可保存的单人冒险。";
        if (RunManager.Instance.DebugOnlyGetState()?.GameMode != GameMode.Standard) return "本版单人快速SL先支持普通冒险。";
        if (Busy || QuickReload.Busy) return "快速SL正在进行。";
        if (LastError != null && !retryEnding) return "上次归档失败，请先完成有效保存。";
        if (!LanMobileLifecycle.Foreground) return "请切回前台后重试。";
        if (NGame.Instance?.Transition.InTransition != false || RunManager.Instance.IsCleaningUp) return "正在切换场景。";
        if (SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false }) return "原版正在保存，请稍后重试。";
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "QuickSL")) return "请禁用外部QuickSL后重启，避免重复重载。";
        return ActiveEntry == null ? "尚无有效保存点。" : null;
    }
    public static async Task Load(RunEntry entry, bool reload = false, string ending = "")
    {
        if (reload) { if (BlockReason(ending) is { } reason) throw new InvalidOperationException(reason); await EnsureSaved(ending.Length > 0); }
        else RunStorage.RequireIdle();
        Scope(); RunStorage.ValidateEnvironment(entry);
        var bytes = Catalog.ReadPayload(entry); var saved = SaveLibrary.Decode(bytes, attachExtensions: true); Validate(saved);
        if (!reload) ImportWorking();
        var previous = _active;
        var oldMain = File.Exists(WorkingPath) ? File.ReadAllBytes(WorkingPath) : null;
        var oldBackup = File.Exists(WorkingPath + ".backup") ? File.ReadAllBytes(WorkingPath + ".backup") : null;
        Busy = true; _pin = entry; _written = null; _ended = false; Generation++;
        var tornDown = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            ReloadUi.Progress("正在加载最近保存点……", null);
            var manager = RunManager.Instance;
            if (reload)
            {
                var old = _waits; _waits = new(); old.Cancel(); old.Dispose();
                manager.ActionExecutor.Cancel(); manager.ActionQueueSet.Reset();
                foreach (var property in new[] { "CombatStateSynchronizer", "EventSynchronizer", "OneOffSynchronizer", "InputSynchronizer" })
                    (AccessTools.Property(typeof(RunManager), property)?.GetValue(manager) as IDisposable)?.Dispose();
                manager.CleanUp(); tornDown = true;
            }
            _active = entry;
            WriteExact(WorkingPath, bytes);
            var state = RunState.FromSerializable(saved);
            Loading.Value = timeout.Token;
            await manager.SetUpSavedSingleplayer(state, saved);
            NGame.Instance!.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            await NGame.Instance.Transition.FadeOut();
            await NGame.Instance.LoadRun(state, saved.PreFinishedRoom).WaitAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (_written is not { } write) throw new IOException("读档后尚未完成有效保存，保留原检查点。");
            _active = Catalog.Commit(write.Entry, write.Bytes); Catalog.Prune(_active);
            LastError = null; _newId = null;
            await NGame.Instance.Transition.FadeIn();
            _ending = false;
            RunEndFlow.Reloaded(ending);
            ReloadUi.Hide();
        }
        catch
        {
            timeout.Cancel(); Loading.Value = default;
            if (reload || tornDown || RunManager.Instance.IsInProgress)
            {
                WriteExact(WorkingPath, bytes);
                _active = entry;
                await NGame.Instance!.ReturnToMainMenu();
            }
            else
            {
                WriteExact(WorkingPath, oldMain); WriteExact(WorkingPath + ".backup", oldBackup); _active = previous;
            }
            throw;
        }
        finally { Loading.Value = default; _pin = null; _written = null; Busy = false; Generation++; ReloadUi.Hide(); }
    }
    private static void WriteExact(string path, byte[]? bytes)
    {
        if (bytes == null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".solo-staging"; File.WriteAllBytes(temp, bytes); File.Move(temp, path, true);
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame.StartNewSingleplayerRun))]
internal static class PrepareNewSolo
{
    private static void Prefix(GameMode gameMode)
    { if (gameMode == GameMode.Standard) SoloLibrary.New(); }
}
