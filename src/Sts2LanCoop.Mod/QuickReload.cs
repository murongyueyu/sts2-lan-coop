using System.Security.Cryptography;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

// This coordinator owns only our LAN session. It never reconnects or assigns character slots.
internal static class QuickReload
{
    private sealed class Attempt(ReloadFrame description, INetGameService service)
    {
        public readonly ReloadFrame Description = description;
        public readonly INetGameService Service = service;
        public readonly HashSet<ulong> Expected = description.Slots.ToHashSet();
        public readonly Dictionary<string, HashSet<ulong>> Replies = [];
        public readonly HashSet<string> Signals = [];
        public readonly CancellationTokenSource Stop = new();
        public ulong Deadline = Time.GetTicksMsec() + 30000;
        public ReloadDownload? Download;
        public SerializableRun? Save;
        public byte[]? Bytes;
        public RunEntry? Entry;
        public bool Committed;
        public bool DropOldTraffic;
        public bool Recovering;
        public string Stage = "preflight";
        public ulong StageStarted = Time.GetTicksMsec();
        public void Ack(string stage, ulong id)
        {
            if (!Replies.TryGetValue(stage, out var ids)) Replies[stage] = ids = [];
            ids.Add(id);
        }
    }
    private static Attempt? _attempt;
    private static bool _starting;
    private static bool _pumping;
    private static readonly List<Task> StartupTasks = [];
    public static void TrackStartup(Task task) { if (LanSession.Active && !task.IsCompleted) StartupTasks.Add(task); }
    private static CancellationTokenSource _gameWaits = new();
    private static readonly AsyncLocal<CancellationToken> LoadingToken = new();
    public static void CheckLoadCancellation() => LoadingToken.Value.ThrowIfCancellationRequested();
    public static CancellationToken GameWaitToken => _gameWaits.Token;
    public static bool Busy => _attempt != null || _starting;
    public static ulong Epoch { get; private set; }
    public static bool SuppressCleanupDisconnect { get; private set; }
    public static INetGameService? PreservedService => _attempt?.Service;
    public static bool DropOldTraffic => _attempt?.DropOldTraffic == true;
    public static string? LastFailure { get; private set; }
    public static int Completed { get; private set; }
    public static void Reset()
    {
        _attempt?.Stop.Cancel(); _attempt = null; _starting = false; Epoch = 0;
        var old = _gameWaits; _gameWaits = new(); old.Cancel(); old.Dispose();
        LastFailure = null; Completed = 0; SuppressCleanupDisconnect = false; ReloadUi.Reset();
    }
    public static string? BlockReason(bool ignoreBusy = false)
    {
        if (LanCapability.QuickReloadBlockReason is { } blocked) return blocked;
        if (!LanSession.Active || !RunManager.Instance.IsInProgress) return "请先进入LAN多人对局。";
        if (!LanMobileLifecycle.Foreground) return "请将游戏切回前台后再快速SL。";
        if (Busy && !ignoreBusy) return "快速SL正在进行。";
        if (SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false }) return "原版正在保存，请稍候再SL。";
        if (StartupTasks.Any(t=>!t.IsCompleted)) return "原版正在完成对局入场，请稍候再SL。";
        if (NGame.Instance?.Transition.InTransition != false || RunManager.Instance.IsCleaningUp) return "正在切换场景，请稍候。";
        if (AccessTools.Field(typeof(CombatStateSynchronizer), "_syncCompletionSource").GetValue(RunManager.Instance.CombatStateSynchronizer) is
            TaskCompletionSource pending && !pending.Task.IsCompleted) return "正在同步对局状态，请稍候再SL。";
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "QuickSL"))
            return "检测到外部QuickSL，请关闭其中一套重载功能并重启；本版内置SL需要禁用外部QuickSL。";
        return null;
    }
    public static void Request()
    {
        var blocked = BlockReason();
        if (blocked != null) { ReloadUi.Notice(blocked); return; }
        if (LanSession.HostedService != null) _ = StartHost();
        else
        {
            Send(RunManager.Instance.NetService, 1, new() { Operation = "ask", Session = AdmissionTransport.Session });
            ReloadUi.Notice("已向主机申请全员快速SL。角色和昵称保持不变。");
        }
    }
    private static ulong[] RunIds() => RunManager.Instance.DebugOnlyGetState()!.Players.Select(p => p.NetId).Order().ToArray();
    private static void CheckHostPlayers(Attempt? attempt = null)
    {
        var service = attempt?.Service as NetHostGameService ?? LanSession.HostedService ?? throw new InvalidOperationException("主机连接不存在。");
        var expected = attempt?.Expected ?? RunIds().ToHashSet();
        if (expected.Count is < 2 or > 4 || !expected.SetEquals(service.NetHost!.ConnectedPeerIds.Append(1UL)) ||
            (RunManager.Instance.RunLobby != null && !expected.SetEquals(GameBuild.Connected(RunManager.Instance.RunLobby))))
            throw new InvalidOperationException("人数未到齐：必须原开局人数全部在线，不能缺人快速SL。");
        if (AdmissionTransport.ReloadBlockReason(expected) is { } blocked) throw new InvalidOperationException(blocked);
    }
    private static async Task StartHost()
    {
        Attempt? attempt = null;
        if (Busy) return;
        _starting = true; LastFailure = null;
        try
        {
            CheckHostPlayers(); ReloadUi.Progress("检查当前保存点……", cancel: null);
            await SaveLibrary.EnsureSaved();
            CheckHostPlayers();
            var entry = SaveLibrary.ActiveEntry!;
            var bytes = SaveLibrary.Catalog.ReadPayload(entry);
            var ids = RunIds();
            if (!ids.SequenceEqual(entry.Slots.Select(s => s.NativeId).Order())) throw new InvalidOperationException("当前角色与保存点不一致。");
            var description = new ReloadFrame { Operation = "probe", Session = AdmissionTransport.Session, Request = Guid.NewGuid().ToString("N"),
                Epoch = checked(Epoch + 1), RunId = entry.Id, Revision = entry.Revision, Digest = Convert.ToHexString(SHA256.HashData(bytes)),
                Length = bytes.Length, Slots = ids };
            attempt = new(description, LanSession.HostedService!) { Bytes = bytes, Entry = entry, Save = Decode(bytes, ids) };
            _attempt = attempt; _starting = false;
            ReloadUi.Progress($"{entry.Name} · 准备数据 1/{ids.Length}", Cancel);
            Broadcast(attempt, description);
            attempt.Ack("available", 1);
            await Everyone(attempt, "available");
            Broadcast(attempt, description with { Operation = "begin" });
            for (var offset = 0; offset < bytes.Length; offset += ReloadWire.ChunkSize)
            {
                attempt.Stop.Token.ThrowIfCancellationRequested();
                Broadcast(attempt, description with { Operation = "chunk", Index = offset / ReloadWire.ChunkSize,
                    Data = bytes[offset..Math.Min(bytes.Length, offset + ReloadWire.ChunkSize)] });
                if (offset % (ReloadWire.ChunkSize * 4) == 0) await Frame();
            }
            Broadcast(attempt, description with { Operation = "prepare" }); attempt.Ack("ready", 1);
            await Everyone(attempt, "ready");
            await SaveLibrary.EnsureSaved(); CheckHostPlayers(attempt);
            if (BlockReason(ignoreBusy: true) is { } blocked) throw new InvalidOperationException(blocked);
            if (SaveLibrary.ActiveEntry?.Revision != entry.Revision) throw new InvalidOperationException("准备期间原版更新了保存点，请重新发起SL。");
            SaveLibrary.HoldForReload(entry);
            Broadcast(attempt, description with { Operation = "commit" }); Commit(attempt);
            await Everyone(attempt, "armed"); Phase(attempt, "setup");
            await Everyone(attempt, "built"); Phase(attempt, "load");
            await Everyone(attempt, "loaded");
            if (SaveManager.Instance.CurrentRunSaveTask is { } pending) await pending;
            if (SaveLibrary.LastError != null) throw new IOException("SL重载后的保存失败。", SaveLibrary.LastError);
            CheckHostPlayers(attempt); SaveLibrary.FinishReload(true);
            Broadcast(attempt, description with { Operation = "release" }); attempt.Ack("finished", 1);
            await Everyone(attempt, "finished"); Succeed(attempt);
        }
        catch (Exception ex)
        {
            if (attempt != null) await Fail(attempt, ex.Message, true);
            else { LastFailure = ex.Message; ReloadUi.Notice(ex.Message); }
        }
        finally { _starting = false; }
    }
    private static SerializableRun Decode(byte[] bytes, IEnumerable<ulong> expected)
    {
        var save = SaveLibrary.Decode(bytes);
        if (save.GameMode != GameMode.Standard || !expected.Order().SequenceEqual(save.Players.Select(p => p.NetId).Order()) ||
            !save.Players.Any(p => p.NetId == LanSession.LocalId)) throw new InvalidDataException("SL存档角色集合不匹配。");
        return RunManager.CanonicalizeSave(save, LanSession.LocalId);
    }
    public static void Receive(INetGameService service, ulong sender, ReloadFrame frame)
    {
        if (!LanSession.Active || frame.Session != AdmissionTransport.Session) return;
        var host = service is NetHostGameService;
        if ((host && !LanSession.HostedService!.NetHost!.ConnectedPeerIds.Contains(sender)) || (!host && sender != 1)) return;
        try
        {
            if (host && frame.Operation == "ask")
            {
                var blocked=BlockReason();
                if (blocked!=null) Send(service,sender,frame with { Operation="notice", Detail=blocked });
                else ReloadUi.Ask(AdmissionTransport.Name(sender) ?? sender.ToString(), Request,
                    ()=>Send(service,sender,frame with { Operation="notice", Detail="主机拒绝了本次快速SL申请。" }));
                return;
            }
            if (!host && frame.Operation=="notice") { if (!Busy) ReloadUi.Notice(frame.Detail); return; }
            if (!host && frame.Operation == "probe")
            {
                var blocked = BlockReason();
                if (blocked != null) throw new InvalidOperationException(blocked);
                if (frame.Epoch != Epoch + 1 || !RunIds().SequenceEqual(frame.Slots.Order())) throw new InvalidDataException("SL人数或加载代次不一致。");
                LanMobileLifecycle.ProbeSaveAccess();
                _attempt = new(frame, service) { Download = new(frame.Length, frame.Digest) };
                LastFailure = null; ReloadUi.Progress("等待全员前台与存档检查……", Cancel);
                Reply(_attempt, "available"); return;
            }
            var attempt = _attempt;
            if (attempt == null || attempt.Description.Request != frame.Request || attempt.Description.Epoch != frame.Epoch || attempt.Service != service) return;
            if (frame.Operation == "failed") { _ = Fail(attempt, frame.Detail, host); return; }
            if (host)
            {
                if (frame.Operation is "available" or "ready" or "armed" or "built" or "loaded" or "finished") attempt.Ack(frame.Operation, sender);
                return;
            }
            switch (frame.Operation)
            {
                case "begin": ReloadUi.Progress("接收主机保存点……", Cancel); break;
                case "chunk": attempt.Download!.Add(frame.Index, frame.Data); break;
                case "prepare":
                    if (BlockReason(ignoreBusy: true) is { } blocked) throw new InvalidOperationException(blocked);
                    attempt.Bytes = attempt.Download!.Complete(); attempt.Save = Decode(attempt.Bytes, attempt.Expected);
                    Reply(attempt, "ready"); break;
                case "commit":
                    if (attempt.Save == null || attempt.Committed) throw new InvalidOperationException("SL执行顺序无效。");
                    Commit(attempt); break;
                case "setup": case "load":
                    attempt.Signals.Add(frame.Operation); attempt.Deadline = Time.GetTicksMsec() + 30000; break;
                case "release":
                    if (!attempt.Signals.Contains("locally-loaded")) throw new InvalidOperationException("本机尚未加载完成。");
                    Reply(attempt, "finished"); Succeed(attempt); break;
            }
        }
        catch (Exception ex)
        {
            if (_attempt is { } attempt) _ = Fail(attempt, ex.Message, host);
            else if (!host)
                Send(service, 1, frame with { Operation = "failed", Data = [], Detail = ex.Message });
        }
    }
    private static void Commit(Attempt attempt)
    {
        RecordStage(attempt, "commit");
        attempt.Committed = true; attempt.DropOldTraffic = true; Epoch = attempt.Description.Epoch;
        attempt.Deadline = Time.GetTicksMsec() + 30000;
        ReloadUi.Progress("全员重新载入……角色保持不变", null);
        _ = ReloadLocally(attempt);
    }
    private static async Task ReloadLocally(Attempt attempt)
    {
        LoadRunLobby? lobby = null;
        try
        {
            var manager = RunManager.Instance;
            var oldWaits = _gameWaits; _gameWaits = new(); oldWaits.Cancel(); oldWaits.Dispose();
            manager.ActionExecutor.Cancel(); manager.ActionQueueSet.Reset();
            attempt.Service.SetGameLoading(true); attempt.Service.SetBufferMessages(true);
            foreach (var property in new[] { "CombatStateSynchronizer", "EventSynchronizer", "OneOffSynchronizer", "InputSynchronizer" })
                (AccessTools.Property(typeof(RunManager), property)?.GetValue(manager) as IDisposable)?.Dispose();
            SuppressCleanupDisconnect = true;
            try { manager.CleanUp(); } finally { SuppressCleanupDisconnect = false; }
            attempt.Stop.Token.ThrowIfCancellationRequested();
            var state = RunState.FromSerializable(attempt.Save!);
            lobby = new LoadRunLobby(attempt.Service, GameBuild.ReloadListener(reason=>{if(_attempt is {} a)_=Fail(a,reason,a.Service is NetHostGameService);}), attempt.Save!);
            GameBuild.AddReloadPlayers(lobby,attempt.Expected);
            NGame.Instance!.RemoteCursorContainer.Initialize(lobby.InputSynchronizer,GameBuild.Connected(lobby));
            NGame.Instance.ReactionContainer.InitializeNetworking(attempt.Service);
            Reply(attempt, "armed"); await Signal(attempt, "setup");
            attempt.DropOldTraffic = false;
            LoadingToken.Value = attempt.Stop.Token;
            await manager.SetUpSavedMultiplayer(state, lobby);
            attempt.Stop.Token.ThrowIfCancellationRequested();
            Reply(attempt, "built"); await Signal(attempt, "load");
            await NGame.Instance.LoadRun(state, attempt.Save!.PreFinishedRoom);
            attempt.Stop.Token.ThrowIfCancellationRequested();
            lobby.CleanUp(false); lobby = null;
            if (!attempt.Expected.SetEquals(state.Players.Select(p => p.NetId))) throw new InvalidDataException("SL后角色不一致。");
            attempt.Signals.Add("locally-loaded"); Reply(attempt, "loaded");
        }
        catch (Exception ex) { await Fail(attempt, ex.Message, attempt.Service is NetHostGameService); }
        finally { LoadingToken.Value = default; lobby?.CleanUp(false); }
    }
    private static void Reply(Attempt attempt, string phase)
    {
        RecordStage(attempt, phase);
        if (attempt.Service is NetHostGameService) attempt.Ack(phase, 1);
        else Send(attempt.Service, 1, attempt.Description with { Operation = phase, Data = [] });
    }
    private static void Phase(Attempt attempt, string phase)
    {
        RecordStage(attempt, phase);
        attempt.Signals.Add(phase); attempt.Deadline = Time.GetTicksMsec() + 30000;
        Broadcast(attempt, attempt.Description with { Operation = phase });
    }
    private static async Task Signal(Attempt attempt, string phase)
    {
        while (!attempt.Signals.Contains(phase)) { attempt.Stop.Token.ThrowIfCancellationRequested(); await Frame(); }
    }
    private static async Task Everyone(Attempt attempt, string phase)
    {
        attempt.Deadline = Time.GetTicksMsec() + 30000;
        while (!attempt.Replies.TryGetValue(phase, out var ids) || !attempt.Expected.SetEquals(ids))
        {
            attempt.Stop.Token.ThrowIfCancellationRequested(); CheckHostPlayers(attempt);
            var stage = phase switch { "available" => "检查全员前台与存档", "ready" => "准备数据", "armed" => "清理旧场景", "built" => "初始化对局", "loaded" => "重新载入", _ => "确认完成" };
            var waiting = attempt.Expected.Except(ids ?? []).Select(id => AdmissionTransport.Name(id) ?? (id == 1 ? LanSession.Settings.PlayerName : id.ToString()));
            ReloadUi.Progress($"快速SL · {stage} · {(ids?.Count ?? 0)}/{attempt.Expected.Count}\n等待：{string.Join("、",waiting)}", attempt.Committed ? null : Cancel);
            await Frame();
        }
    }
    private static void Broadcast(Attempt attempt, ReloadFrame frame)
    { foreach (var id in attempt.Expected.Where(id => id != 1)) Send(attempt.Service, id, frame); }
    private static void Send(INetGameService service, ulong target, ReloadFrame frame)
    {
        var bytes = ReloadWire.Encode(frame);
        if (service is NetHostGameService host) host.NetHost!.SendMessageToClient(target, bytes, bytes.Length, NetTransferMode.Reliable);
        else if (service is NetClientGameService client) client.NetClient!.SendMessageToHost(bytes, bytes.Length, NetTransferMode.Reliable);
    }
    public static void Cancel() { if (_attempt is { Committed: false } attempt) _ = Fail(attempt, "快速SL已取消。", attempt.Service is NetHostGameService); }
    public static void OnBackgrounded()
    {
        if (_attempt is { } attempt) _ = Fail(attempt, "有玩家将游戏切至后台，本次SL停止。", attempt.Service is NetHostGameService);
    }
    private static async Task Fail(Attempt attempt, string reason, bool broadcast)
    {
        if (_attempt != attempt || attempt.Recovering) return;
        attempt.Recovering = true; LastFailure = reason; attempt.Stop.Cancel();
        if (attempt.Committed) { var old = _gameWaits; _gameWaits = new(); old.Cancel(); old.Dispose(); }
        GD.PushError("[STS2 LAN Co-op] 快速SL失败：" + reason);
        try
        {
            var failed = attempt.Description with { Operation = "failed", Detail = reason.Length > 450 ? reason[..450] : reason, Data = [] };
            if (attempt.Service.IsConnected) { if (broadcast) Broadcast(attempt, failed); else Send(attempt.Service, 1, failed); }
            if (attempt.Committed)
            {
                if (attempt.Service is NetHostGameService && attempt.Bytes != null) await SaveLibrary.RestoreReloadPoint(attempt.Bytes);
                SaveLibrary.FinishReload(false);
                attempt.DropOldTraffic = false;
                // The old world has already been destroyed. Recover through the validated save, never continue short-handed.
                await NGame.Instance!.ReturnToMainMenu();
                // CleanUp returns early when no RunState was rebuilt yet; close the preserved session explicitly.
                if (attempt.Service.IsConnected) attempt.Service.Disconnect(NetError.Quit, now: true);
            }
            else attempt.Service.SetGameLoading(false);
        }
        catch (Exception ex)
        {
            GD.PushError("[STS2 LAN Co-op] SL恢复失败，归档仍保留：" + ex); SaveLibrary.FinishReload(false);
            if (attempt.Committed && attempt.Service.IsConnected) attempt.Service.Disconnect(NetError.Quit, now: true);
        }
        finally
        {
            if (_attempt == attempt) { _attempt = null; ReloadUi.Notice(reason + (attempt.Committed ? " 已停止本次重载，请从存档重新组队。" : " 当前对局保持运行。")); }
        }
    }
    private static void Succeed(Attempt attempt)
    {
        if (_attempt != attempt || attempt.Recovering) return;
        RecordStage(attempt, "completed");
        attempt.Service.SetGameLoading(false); _attempt = null; Completed++; ReloadUi.Hide();
        GD.Print($"[STS2 LAN Co-op] 快速SL完成，人数{attempt.Expected.Count}，代次{Epoch}，角色分配保持不变。");
    }
    public static void Tick()
    {
        StartupTasks.RemoveAll(t=>t.IsCompleted);
        ReloadUi.CheckHotkey();
        var attempt = _attempt;
        if (attempt == null || attempt.Recovering) return;
        if (!LanSession.Active || !attempt.Service.IsConnected || Time.GetTicksMsec() > attempt.Deadline)
        { _ = Fail(attempt, "连接中断或等待全员加载超时。", attempt.Service is NetHostGameService); return; }
        if (!_pumping)
        {
            _pumping = true;
            try { attempt.Service.Update(); }
            catch (Exception ex) { _ = Fail(attempt, ex.Message, attempt.Service is NetHostGameService); }
            finally { _pumping = false; }
        }
    }
    private static void RecordStage(Attempt attempt, string stage)
    {
        var now = Time.GetTicksMsec();
        GD.Print($"[STS2 LAN Co-op] SL阶段{attempt.Stage}→{stage}：{now-attempt.StageStarted}ms，托管内存{GC.GetTotalMemory(false)/1024/1024}MiB，代次{attempt.Description.Epoch}。");
        attempt.Stage = stage; attempt.StageStarted = now;
    }
    private static async Task Frame()
    {
        var tree = (SceneTree)Engine.GetMainLoop(); await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }
}
