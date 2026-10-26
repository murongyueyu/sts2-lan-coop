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
        using var client = new ModSyncTransport.Client(parent.GetTree(), endpoint);
        var host = await client.Profile(cancel);
        var local = await ModSyncInventory.Read(); cancel.ThrowIfCancellationRequested();
        var unreadable = local.Where(m => m.Loaded && m.Unavailable.Length > 0).ToArray();
        if (unreadable.Length > 0) throw new IOException("手机模组文件无法核验，请核对启动器目录与权限：\n" +
            string.Join("\n", unreadable.Select(m => m.Name + "：" + m.Unavailable)));
        var diff = ModSyncRules.Compare(host, local, GameBuild.Version);
        if (diff.Errors.Count > 0) throw new InvalidOperationException(string.Join("\n", diff.Errors));
        if (diff.Matches) { progress("文件及启用状态与主机一致；进房时继续执行原版内容校验。"); return true; }
        var changes = string.Join("\n", diff.Changes.Select(c => $"{c.Action} {c.Name} [{c.Id}]：{c.Reason}"));
        var targets = diff.Changes.Where(c => c.Action is "add" or "replace").Select(c => c.Target!).ToArray();
        progress("模组尚未一致：请使用“检查 / 下载主机模组”查看差异并获取文件；下载后仍须退出并在启动器中手动导入/调整启用状态。");
        if (!offerDownload) return false;
        if (targets.Length == 0)
        {
            await ModSyncWorkflow.Confirm(parent, "请在启动器调整启用状态", changes +
                "\n\n文件已存在，无需下载。当前没有启动前应用接口，不改动手机启用配置；请退出游戏后在启动器中调整，再重启检查。", cancel);
            return false;
        }
        if (!await ModSyncWorkflow.Confirm(parent, "下载供手动导入的主机模组", changes +
            "\n\n这是主机使用的PC包，尚未证明Android兼容。下载只进入缓存，不安装、不启用、不替换手机文件。请仅在确认对应模组支持Android后，通过启动器手动导入；其他包留给作者适配。是否下载？", cancel)) return false;
        var packages = new List<object>();
        foreach (var target in targets)
        {
            cancel.ThrowIfCancellationRequested();
            var archive = await client.Download(host, target, progress, cancel);
            packages.Add(new { target.Id, target.Version, target.Bundle, Archive = Path.GetFileName(archive) });
        }
        var fresh = await client.Profile(cancel);
        if (fresh.Session != host.Session || fresh.Revision != host.Revision)
            throw new IOException("主机房间或模组已变化，缓存未安装，请重新检查。");
        Directory.CreateDirectory(ModSyncInventory.Storage);
        var record = Path.Combine(ModSyncInventory.Storage, "downloaded-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(record, JsonSerializer.SerializeToUtf8Bytes(new
        {
            State = "downloaded-not-installed", Game = host.Game, HostRevision = host.Revision,
            AndroidCompatibility = "not-verified", Packages = packages
        }));
        progress("下载校验完成，未安装。请退出游戏，在启动器文件浏览器导出缓存ZIP，确认Android兼容后再导入/启用并重启。缓存目录：" + ModSyncInventory.Cache);
        if (await ModSyncWorkflow.Confirm(parent, "已下载，尚未安装", "缓存目录：\n" + ModSyncInventory.Cache +
            "\n\n启动器v0.1.11尚无本Mod已接入的启动前同步事务接口。本Mod不会在已加载时覆盖DLL/PCK或修改启用项。请使用启动器文件浏览器导出上述ZIP，退出游戏后手动导入兼容包并重启。", cancel, "复制缓存路径"))
            DisplayServer.ClipboardSet(ModSyncInventory.Cache);
        return false;
    }
    private static bool RunManagerInUse() => LanSession.Active || MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress;
}
