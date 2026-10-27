using System.Text.Json;
using Godot;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

// Stage C: compare and acquire exact host packages. Never installs or changes enabled mods.
internal static class MobileModSync
{
    public static async Task<bool> Check(Node parent, Endpoint endpoint, Action<string> progress,
        CancellationToken cancel, bool offerDownload)
    {
        if (RunManagerInUse()) throw new InvalidOperationException("请先退出当前对局/房间，再检查主机模组。");
        progress("正在检查主机模组与手机文件……");
        var host = await ModSyncTransport.ReadProfile(parent.GetTree(), endpoint, cancel);
        var required = ModSyncRules.Required(host);
        var local = await ModSyncInventory.Read(host); cancel.ThrowIfCancellationRequested();
        var unreadable = local.Where(m => m.Loaded && required.Contains(m.Id) && m.Unavailable.Length > 0).ToArray();
        if (unreadable.Length > 0) throw new IOException("手机模组文件无法核验，请核对启动器目录与权限：\n" +
            string.Join("\n", unreadable.Select(m => m.Name + "：" + m.Unavailable)));
        var diff = ModSyncRules.Compare(host, local, GameBuild.Version);
        if (diff.Errors.Count > 0) throw new InvalidOperationException(string.Join("\n", diff.Errors));
        var report = WriteReport(host, local, diff);
        // Old imports can now pass verification while still sharing a directory.
        // The download button may offer foldered copies; Join never requires this.
        var sharedIds = local.Where(m => m.Loaded).GroupBy(m => m.Directory, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).SelectMany(g => g.Select(m => m.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folderTargets = diff.Matches && offerDownload ? host.Mods
            .Where(m => required.Contains(m.Id) && m.Id != ModSyncRules.CoreId && sharedIds.Contains(m.Id)).ToArray() : [];
        if (diff.Matches && folderTargets.Length == 0)
        { progress("文件及启用状态与主机一致；进房时继续执行原版内容校验。"); return true; }
        var changes = diff.Matches ? "文件及启用状态已与主机一致。以下模组仍共用目录，可生成独立文件夹导入包：\n" +
            string.Join("\n", folderTargets.Select(m => m.Name + " [" + m.Id + "]")) : string.Join("\n", diff.Changes.Select(c =>
        {
            var details = c.Target != null && c.Local != null ? MobileModFiles.Differences(c.Target, c.Local) : [];
            return $"{ActionName(c.Action)} {c.Name} [{c.Id}]：{c.Reason}" +
                (details.Count > 0 ? "\n  " + string.Join("\n  ", details.Take(8)) + (details.Count > 8 ? "\n  其余见差异报告。" : "") : "");
        }));
        var targets = diff.Matches ? folderTargets : diff.Changes.Where(c => c.Action is "add" or "replace").Select(c => c.Target!).ToArray();
        progress((diff.Matches ? "文件检查通过，可自愿整理导入目录：\n" : "连接前模组检查未通过：\n") + changes + "\n完整差异报告：" + report +
            "\n下载不会直接安装；已导入后仍有差异时，请按具体文件提示处理。");
        if (!offerDownload) return false;
        if (targets.Length == 0)
        {
            await ModSyncWorkflow.Confirm(parent, "请在启动器调整启用状态", changes +
                "\n\n文件已存在，无需下载。当前没有启动前应用接口，不改动手机启用配置；请退出游戏后在启动器中调整，再重启检查。", cancel);
            return false;
        }
        if (!await ModSyncWorkflow.Confirm(parent, diff.Matches ? "生成独立目录的模组导入包？" : "下载供手动导入的主机模组", changes +
            "\n\n已有完整缓存会复用，缺缓存时才下载。生成的导入包保留模组ID文件夹，不自动替换手机文件。文件一致不等于已证明Android兼容；请确认对应模组兼容后，退出并通过启动器模组管理手动导入。是否继续？", cancel)) return false;
        var archives = new List<(SyncMod Mod, string Archive)>();
        foreach (var target in targets)
        {
            cancel.ThrowIfCancellationRequested();
            // Poll an ENet connection only during this package's network work.
            // Do not carry it through user dialogs or local ZIP generation.
            using var downloadClient = new ModSyncTransport.Client(parent.GetTree(), endpoint);
            var archive = await downloadClient.Download(host, target, progress, cancel);
            archives.Add((target, archive));
        }
        progress("文件已获取，正在重新连接并复核主机房间……");
        GD.Print("[STS2 LAN Co-op] Android sync: fresh host verification before local packaging.");
        var fresh = await ModSyncTransport.ReadProfile(parent.GetTree(), endpoint, cancel);
        if (fresh.Session != host.Session || fresh.Revision != host.Revision)
            throw new IOException("主机房间或模组已变化，缓存未安装，请重新检查。");
        var packages = new List<object>();
        foreach (var (target, archive) in archives)
        {
            cancel.ThrowIfCancellationRequested();
            progress("正在本机生成导入包（无需同步连接）：" + target.Name);
            GD.Print("[STS2 LAN Co-op] Android sync: local packaging " + target.Id);
            var import = await Task.Run(() => MobileModFiles.PrepareImport(archive, target, ModSyncInventory.MobileImports, cancel), cancel);
            packages.Add(new { target.Id, target.Version, target.Bundle, Archive = Path.GetFileName(archive), Import = Path.GetFileName(import) });
        }
        cancel.ThrowIfCancellationRequested();
        Directory.CreateDirectory(ModSyncInventory.Storage);
        var record = Path.Combine(ModSyncInventory.Storage, "downloaded-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(record, JsonSerializer.SerializeToUtf8Bytes(new
        {
            State = "downloaded-not-installed", Game = host.Game, HostRevision = host.Revision,
            AndroidCompatibility = "not-verified", HostVerification = "before-local-packaging", Packages = packages
        }));
        progress("下载校验完成，未安装。请退出游戏，在启动器文件浏览器导出import目录的ZIP，确认Android兼容后再导入/启用并重启。导入包目录：" + ModSyncInventory.MobileImports);
        if (await ModSyncWorkflow.Confirm(parent, "已下载，尚未安装", "导入包目录：\n" + ModSyncInventory.MobileImports +
            "\n\n启动器 → 设置 → 系统 → 查看文件 → sts2_lan_coop/modsync/import。导出此处ZIP，再通过模组管理手动导入。每个ZIP保留独立模组目录，避免混在一起；cache中的原包只用于下载校验。请退出游戏后操作，再启用并重启。", cancel, "复制导入包路径"))
            DisplayServer.ClipboardSet(ModSyncInventory.MobileImports);
        return false;
    }
    private static string WriteReport(SyncProfile host, List<SyncMod> local, SyncComparison comparison)
    {
        Directory.CreateDirectory(ModSyncInventory.Storage);
        var path = Path.Combine(ModSyncInventory.Storage, "last-comparison.json");
        var required = ModSyncRules.Required(host);
        var entries = host.Mods.Where(target => required.Contains(target.Id)).Select(target => new
        {
            target.Id, HostVersion = target.Version,
            Copies = local.Where(m => m.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)).Select(m => new
            {
                m.Version, m.Loaded, m.Directory, m.FileNotes,
                Differences = MobileModFiles.Differences(target, m), HostFiles = target.Files, PhoneFiles = m.Files
            }).ToArray()
        }).ToArray();
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = "0.7.0-alpha.8", HostRevision = host.Revision, comparison.Matches,
            comparison.Errors, comparison.Changes, Mods = entries
        }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print("[STS2 LAN Co-op] Android mod comparison: " + (comparison.Matches ? "matched" : "different") + "; report=" + path);
        foreach (var entry in entries)
            foreach (var copy in entry.Copies)
                foreach (var note in copy.FileNotes.Concat(copy.Differences)) GD.Print("[STS2 LAN Co-op] " + entry.Id + ": " + note);
        return path;
    }
    private static string ActionName(string action) => action switch
    { "add" => "安装", "replace" => "替换", "enable" => "启用", "disable" => "禁用", _ => action };
    private static bool RunManagerInUse() => LanSession.Active || MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress;
}
