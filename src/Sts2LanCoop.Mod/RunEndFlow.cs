using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

// A decision belongs to one ended world, not merely to the currently selected archive.
internal static class RunEndFlow
{
    private sealed class Decision(SerializableRun run, INetGameService service)
    {
        public readonly SerializableRun Run = run;
        public readonly INetGameService Service = service;
        public readonly string Key = RunStorage.Key(run);
        public readonly string Scope = RunStorage.Scope;
        public readonly string Session = AdmissionTransport.Session;
        public readonly ulong Epoch = QuickReload.Epoch;
        public readonly ulong[] Slots = run.Players.Select(p => p.NetId).Order().ToArray();
        public readonly bool Solo = service.Type == NetGameType.Singleplayer;
        public readonly bool Host = service.Type == NetGameType.Singleplayer || service is NetHostGameService;
        public string Token = service.Type == NetGameType.Singleplayer || service is NetHostGameService ? Guid.NewGuid().ToString("N") : "";
        public RunCatalog Catalog => Solo ? SoloLibrary.Catalog : SaveLibrary.Catalog;
        public RunEntry? Entry;
        public string Error = "";
        public readonly HashSet<ulong> Ready = [1];
        public readonly HashSet<ulong> ExitReady = [1];
        public readonly HashSet<ulong> Left = [1];
        public NativePage? Page;
        public Label? Status;
        public UiAction? Retry;
        public UiAction? Keep;
        public UiAction? Delete;
        public bool Operating;
        public bool Leaving;
        public bool ExitPrepared;
        public ulong NextBroadcast;
        public int Frames;
    }
    private static Decision? _decision;
    public static bool Pending => _decision != null;
    public static bool CanRetry(string token) => token.Length == 32 && _decision is { Leaving: false } d &&
        d.Token == token && Current(d) && (d.Solo || d.Service.IsConnected);
    private static bool Current(Decision d) => ReferenceEquals(_decision, d) && d.Scope == RunStorage.Scope &&
        (d.Solo || d.Session == AdmissionTransport.Session && d.Epoch == QuickReload.Epoch) &&
        ReferenceEquals(d.Service, RunManager.Instance.NetService);
    public static void Reset() { Hide(_decision); _decision = null; }
    private static void Hide(Decision? d)
    {
        if (d?.Page is { } page && GodotObject.IsInstanceValid(page)) page.QueueFree();
        if (d != null) { d.Page = null; d.Status = null; d.Retry = null; }
    }
    public static void Begin(SerializableRun run)
    {
        if ((!RunStorage.Solo && !LanSession.Active) || run.GameMode != GameMode.Standard || run.Players.Count is < 1 or > 4) return;
        var key = RunStorage.Key(run);
        if (_decision is { } existing && existing.Key == key && Current(existing)) return;
        Reset();
        var d = new Decision(run, RunManager.Instance.NetService);
        _decision = d;
        if (d.Host)
        {
            try
            {
                var entry = (d.Solo ? SoloLibrary.ActiveEntry : SaveLibrary.ActiveEntry) ?? throw new IOException("没有可继续的有效保存点。");
                var bytes = d.Catalog.ReadPayload(entry);
                var saved = SaveLibrary.Decode(bytes, attachExtensions: true);
                RunStorage.ValidateEnvironment(entry);
                if (RunStorage.Key(saved) != key || saved.Players.All(p => p.CurrentHp <= 0))
                    throw new IOException("保存点不属于本局或已是死亡状态，不能用于重试。");
                d.Entry = entry;
            }
            catch (Exception ex) { d.Error = ex.Message; GD.PushWarning("[STS2 LAN Co-op] 失败保存点不可用：" + ex); }
        }
    }
    public static void Reloaded(string token)
    {
        if (_decision is { } d && d.Token == token && token.Length > 0) Reset();
    }
    public static void ReloadFailed(string token)
    {
        if (_decision is { } d && d.Token == token && token.Length > 0 && !d.Service.IsConnected)
            _ = LeaveSafely(d);
    }
    public static void Tick()
    {
        var d = _decision;
        if (d == null) return;
        // During a committed reload the epoch changes before its new world is ready.
        if (QuickReload.Busy || SoloLibrary.Busy) { Hide(d); return; }
        if (d.Leaving) return;
        if (!Current(d) && (d.Solo || LanSession.Active)) { Reset(); return; }
        if (++d.Frames < 4 || NGame.Instance?.Transition.InTransition != false) return;
        if (d.Page == null || !GodotObject.IsInstanceValid(d.Page)) Show(d);
        if (d.Keep != null) d.Keep.Disabled = d.Operating;
        if (d.Delete != null) d.Delete.Disabled = d.Operating;
        if (d.Retry != null) d.Retry.Disabled = d.Operating || d.Entry == null || !Current(d) || !d.Solo && !d.Service.IsConnected;
        if (d.Page != null)
            foreach (var button in NativeUi.Walk(d.Page).OfType<MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenuButton>().Where(b => b.IsEnabled))
                button.FocusMode = Control.FocusModeEnum.All;
        if (!d.Solo && !d.Service.IsConnected && !d.Operating && d.Status != null)
            d.Status.Text = "连接已中断。有效归档仍保留，可返回大厅重新组队继续。";
        if (!d.Solo && d.Host && Current(d) && d.Service.IsConnected && Time.GetTicksMsec() >= d.NextBroadcast)
        {
            d.NextBroadcast = Time.GetTicksMsec() + 1000;
            Broadcast(d, "end-wait");
        }
    }
    private static void Show(Decision d)
    {
        var page = new NativePage { Name = d.Solo ? "SoloRunFailure" : "LanRunFailure", Heading = d.Host ? "本局失败，如何处理存档？" : "本局失败", Dialog = true,
            FocusBehaviorRecursive = Control.FocusBehaviorRecursiveEnum.Enabled };
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(page); d.Page = page;
        var text = d.Host
            ? d.Entry is { } e ? $"{e.Name}\n保存点：第{e.Act}幕 · 第{e.Floor}层 · {e.SavedAt.ToLocalTime():MM-dd HH:mm:ss}\n" +
                (d.Solo ? "读档重试会回到此保存点，保留本局角色和进度。" : "读档重试会让全队回到此保存点，保留角色和昵称。")
                : "没有可继续的有效保存点。\n" + d.Error + "\n返回时将保留现有材料，但不能保证能够续玩。"
            : "等待主机选择保留存档、全队读档重试或删除存档并返回大厅。";
        d.Status = NativeUi.Label(page.Body, text, 26);
        var keep = d.Keep = new UiAction(page.Footer, d.Solo ? "保留存档并返回主菜单" : d.Host ? "保留存档并返回大厅" : "离开并返回大厅", () => _ = Choose(d, false, false), "KeepEndingSave");
        if (d.Host)
        {
            d.Retry = new UiAction(page.Footer, "读档重试本局", () => _ = Choose(d, true, false), "RetryEndingSave");
            d.Retry.Disabled = d.Entry == null;
            d.Delete = new UiAction(page.Footer, d.Solo ? "删除存档并返回主菜单" : "删除存档并返回大厅", () => _ = Choose(d, false, true), "DeleteEndingSave");
        }
        // Back leaves the decision pending; it must never authorize deletion.
        page.CloseRequested = () => { };
        foreach (var button in NativeUi.Walk(page).OfType<MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenuButton>().Where(b => b.IsEnabled))
            button.FocusMode = Control.FocusModeEnum.All;
        NativeUi.FocusLoop(page); keep.Node.GrabFocus();
    }
    private static async Task Choose(Decision d, bool retry, bool delete)
    {
        if (!ReferenceEquals(_decision, d) || d.Operating || QuickReload.Busy || SoloLibrary.Busy) return;
        d.Operating = true;
        try
        {
            if (d.Scope != RunStorage.Scope || LanSession.Active && !Current(d)) throw new IOException("会话已变化，旧选择已失效。");
            if (d.Solo)
            {
                if (!Current(d)) throw new IOException("当前冒险已变化，旧选择已失效。");
                if (retry)
                {
                    if (d.Entry == null) throw new IOException("没有可继续的有效保存点。");
                    Hide(d);
                    await SoloLibrary.Load(d.Entry, true, d.Token);
                }
                else
                {
                    if (!delete && d.Entry != null) _ = d.Catalog.ReadPayload(d.Entry);
                    if (delete) RunStorage.Complete(d.Run, true, explicitEnd: true, reason: "deleted-after-defeat");
                    await Leave(d);
                }
                return;
            }
            if (d.Status != null) d.Status.Text = retry ? "等待全员确认并读取保存点……" : "正在处理存档并返回大厅……";
            if (d.Host)
            {
                if (SaveManager.Instance.CurrentRunSaveTask is { } pending)
                {
                    try { await pending; }
                    catch (Exception ex) { GD.PushWarning("[STS2 LAN Co-op] 结束时原版保存未完成，继续使用已验证归档：" + ex.Message); }
                }
                if (!ReferenceEquals(_decision, d) || d.Scope != RunStorage.Scope || LanSession.Active && !Current(d))
                    throw new IOException("等待保存期间会话已变化。");
                if (!delete && d.Entry is { } entry) _ = SaveLibrary.Catalog.ReadPayload(entry);
                if (retry)
                {
                    if (d.Entry == null || !Current(d) || !d.Service.IsConnected) throw new IOException("保存点或连接不可用，请保留存档后重新组队。");
                    await WaitFor(d, d.Ready, "end-wait", true);
                    await QuickReload.RetryEnding(d.Token);
                    if (_decision == d)
                    {
                        ReloadUi.Hide(); NativeUi.Dismiss("QuickSlNotice");
                        if (!d.Service.IsConnected) await Leave(d);
                        else throw new IOException(QuickReload.LastFailure ?? "读档重试未完成，保存点仍保留。");
                    }
                    return;
                }
                if (delete) RunStorage.Complete(d.Run, false, explicitEnd: true, reason: "deleted-after-defeat");
                if (Current(d) && d.Service.IsConnected)
                {
                    await WaitFor(d, d.Ready, "end-wait", false);
                    await WaitFor(d, d.ExitReady, "end-exit", false);
                    Broadcast(d, "end-leave");
                    await WaitFor(d, d.Left, "end-leave", false);
                }
            }
            await Leave(d);
        }
        catch (Exception ex)
        {
            GD.PushError("[STS2 LAN Co-op] 失败处理未完成，保留恢复记录：" + ex);
            if (_decision == d)
            {
                if (d.Solo && !RunManager.Instance.IsInProgress)
                {
                    Reset();
                    NativeUi.Notice(((SceneTree)Engine.GetMainLoop()).Root, "读档重试未完成", ex.Message + "\n原保存点仍保留，请从单人存档列表继续。");
                    return;
                }
                if (d.Page == null || !GodotObject.IsInstanceValid(d.Page)) Show(d);
                d.Status!.Text = ex.Message + "\n未完成的清理可重试；有效归档未被重试失败覆盖。";
            }
        }
        finally { d.Operating = false; }
    }
    private static async Task WaitFor(Decision d, HashSet<ulong> replies, string operation, bool requireAll)
    {
        var deadline = Time.GetTicksMsec() + (requireAll ? 10000UL : 2000UL);
        ulong nextSend = 0;
        while (!d.Slots.All(replies.Contains))
        {
            if (!Current(d) || !d.Service.IsConnected) throw new IOException("连接已中断，请重新组队续玩。");
            var connected = ((NetHostGameService)d.Service).NetHost!.ConnectedPeerIds.Append(1UL).ToHashSet();
            if (requireAll && !connected.SetEquals(d.Slots)) throw new IOException("人数未到齐，不能缺人读档重试。");
            if (!requireAll && d.Slots.Where(connected.Contains).All(replies.Contains)) return;
            if (Time.GetTicksMsec() >= deadline)
            {
                if (requireAll) throw new IOException("等待全员失败确认超时，保存点仍保留。");
                GD.PushWarning("[STS2 LAN Co-op] 部分玩家未确认返回大厅，主机继续退出；有效归档不受影响。");
                return;
            }
            if (Time.GetTicksMsec() >= nextSend) { nextSend = Time.GetTicksMsec() + 250; Broadcast(d, operation); }
            await Frame();
        }
    }
    private static async Task Leave(Decision d)
    {
        if (d.Leaving) return;
        d.Leaving = true; Hide(d);
        try
        {
            // Let the reliable acknowledgement leave before native cleanup disconnects the peer.
            await Frame(); await Frame();
            if (!ReferenceEquals(_decision, d) || d.Scope != RunStorage.Scope ||
                LanSession.Active && d.Session != AdmissionTransport.Session || d.Solo && !Current(d)) return;
            await NGame.Instance!.ReturnToMainMenu();
            var deadline = Time.GetTicksMsec() + 15000;
            while (NGame.Instance.MainMenu?.IsNodeReady() != true || NGame.Instance.Transition.InTransition)
            {
                if (Time.GetTicksMsec() > deadline) throw new IOException("返回大厅页面超时，请从主菜单进入局域网 / 直连。");
                await Frame();
            }
            if (!ReferenceEquals(_decision, d) || d.Scope != RunStorage.Scope || LanSession.Active) return;
            if (!d.Solo)
            {
                LanSession.Settings.PreferLanMenu = true;
                NGame.Instance.MainMenu!.OpenMultiplayerSubmenu();
            }
            if (_decision == d) Reset();
        }
        catch { d.Leaving = false; throw; }
    }
    public static bool Receive(INetGameService service, ulong sender, ReloadFrame frame)
    {
        if (!frame.Operation.StartsWith("end-", StringComparison.Ordinal)) return false;
        var d = _decision;
        if (d == null || !Current(d) || d.Service != service || frame.Epoch != d.Epoch || frame.RunId != d.Key ||
            frame.Request.Length != 32 || !frame.Request.All(char.IsAsciiHexDigit)) return true;
        if (d.Host)
        {
            if (frame.Request != d.Token || !d.Slots.Contains(sender)) return true;
            if (frame.Operation == "end-ready") d.Ready.Add(sender);
            else if (frame.Operation == "end-exit-ready") d.ExitReady.Add(sender);
            else if (frame.Operation == "end-left") d.Left.Add(sender);
        }
        else if (sender == 1)
        {
            if (frame.Operation == "end-wait" && (d.Token.Length == 0 || d.Token == frame.Request))
            {
                d.Token = frame.Request; Send(d, 1, "end-ready");
            }
            else if (d.Token == frame.Request && frame.Operation == "end-exit")
            {
                d.ExitPrepared = true; Send(d, 1, "end-exit-ready");
            }
            else if (d.Token == frame.Request && frame.Operation == "end-leave" && d.ExitPrepared)
            {
                Send(d, 1, "end-left"); _ = LeaveSafely(d);
            }
        }
        return true;
    }
    private static async Task LeaveSafely(Decision d)
    {
        try { await Leave(d); }
        catch (Exception ex) { GD.PushError("[STS2 LAN Co-op] 返回大厅未完成：" + ex); d.Error = ex.Message; }
    }
    private static void Broadcast(Decision d, string operation)
    {
        foreach (var id in ((NetHostGameService)d.Service).NetHost!.ConnectedPeerIds.Where(d.Slots.Contains)) Send(d, id, operation);
    }
    private static void Send(Decision d, ulong target, string operation)
    {
        var bytes = ReloadWire.Encode(new() { Operation = operation, Session = d.Session, Epoch = d.Epoch, Request = d.Token, RunId = d.Key });
        if (d.Service is NetHostGameService host) host.NetHost!.SendMessageToClient(target, bytes, bytes.Length, NetTransferMode.Reliable);
        else if (d.Service is NetClientGameService client) client.NetClient!.SendMessageToHost(bytes, bytes.Length, NetTransferMode.Reliable);
    }
    private static async Task Frame()
    {
        var tree = (SceneTree)Engine.GetMainLoop(); await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen), "OnMainMenuButtonPressed")]
internal static class FailureBlocksNativeReturn
{
    private static bool Prefix() => !RunEndFlow.Pending;
}
