using System.Diagnostics;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class WorkshopOrganizationWorkflow
{
    private static bool _resultShown;
    public static void ShowLastResult(Action<string> progress)
    {
        if (!LanCapability.Windows || _resultShown) return;
        var root = Path.Combine(ModSyncInventory.Storage, "organization");
        var latest = Directory.Exists(root) ? Directory.EnumerateFiles(root, "journal.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        if (latest == null) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(latest));
            var message = doc.RootElement.GetProperty("State").GetString() switch
            {
                "applied" => "工坊目录整理完成；已保留备份。",
                "restored" => "工坊目录结构已恢复。",
                "failed" => "工坊目录整理/恢复未完成：" + doc.RootElement.GetProperty("Error").GetString(),
                _ => "工坊目录整理曾中断，请检查记录并使用恢复按钮。"
            };
            progress(message + "\n记录：" + latest);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        { progress("工坊整理记录无法读取：" + ex.Message + "\n记录：" + latest); }
        _resultShown = true;
    }
    public static async Task Organize(Node parent, Action<string> progress, CancellationToken cancel, bool restore = false)
    {
        if (!LanCapability.Windows || LanSession.Active || RunManager.Instance.IsInProgress)
            throw new InvalidOperationException("请退出房间/对局，在Windows多人菜单整理工坊目录。");
        var root = Path.Combine(ModSyncInventory.Storage, "organization");
        WorkshopOrganizationPlan plan;
        if (restore)
        {
            var latest = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(d =>
            {
                var journal = Path.Combine(d, "journal.json");
                if (!File.Exists(journal)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(journal));
                return doc.RootElement.GetProperty("State").GetString() is "applied" or "applying" or "failed";
            }).OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault() : null;
            if (latest == null) throw new IOException("没有可恢复的工坊目录整理记录。");
            plan = JsonSerializer.Deserialize<WorkshopOrganizationPlan>(File.ReadAllBytes(Path.Combine(latest, "plan.json")))!;
        }
        else
        {
            var workshopRoots = ModManager.Mods.Where(m => m.modSource == ModSource.SteamWorkshop)
                .Select(m => FindWorkshopRoot(m.path)).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (workshopRoots.Length != 1) throw new IOException("未找到唯一的Steam工坊目录；请先让游戏识别工坊模组。");
            progress("正在检查工坊目录和文件摘要，尚未移动文件……");
            plan = await Task.Run(() => WorkshopOrganization.Inspect(workshopRoots[0]!, cancel), cancel);
            cancel.ThrowIfCancellationRequested();
            if (plan.Units.Count == 0)
            {
                progress("没有需要整理的独立工坊条目。\n" + string.Join("\n", plan.Notes));
                return;
            }
        }
        var summary = string.Join("\n", plan.Units.Select(u => u.Item + "/ → " + u.Item + "/" + u.ModId + "/"));
        var transaction = Path.Combine(root, plan.Id);
        var text = restore
            ? "将退出游戏并恢复以下工坊条目的原目录结构。文件已被Steam更新或手动修改时停止，保留备份。\n" + summary
            : "将退出游戏，备份并把每个条目的完整内容移入模组ID子目录，然后重启。工坊数字目录保留，游戏mods目录与启用状态保持原样。\n" +
              summary + "\n\n请先暂停Steam下载/更新。后续工坊更新可能恢复原目录结构；不要在更新过程中整理。\n" + string.Join("\n", plan.Notes);
        if (!await ModSyncWorkflow.Confirm(parent, restore ? "恢复工坊目录整理并重启？" : "整理工坊原目录并重启？", text + "\n\n备份/日志：" + transaction, cancel)) return;
        using var process = Process.GetCurrentProcess();
        plan = plan with
        {
            GameRoot = ModSyncInventory.GameRoot, Storage = ModSyncInventory.Storage,
            GamePid = process.Id, GameStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks
        };
        var core = ModManager.GetLoadedMods().Single(m => m.manifest?.id == ModSyncRules.CoreId);
        var helper = Path.Combine(core.path, "Organize-Workshop.ps1");
        if (!File.Exists(helper)) throw new FileNotFoundException("LAN基础包缺少工坊整理助手，请完整更新基础包。", helper);
        Directory.CreateDirectory(transaction);
        File.WriteAllBytes(Path.Combine(transaction, "plan.json"), JsonSerializer.SerializeToUtf8Bytes(plan));
        // Run the transaction copy: the LAN core may itself be moved after exit.
        var localHelper = Path.Combine(transaction, "Organize-Workshop.ps1");
        File.Copy(helper, localHelper, true);
        var start = new ProcessStartInfo(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = ModSyncInventory.GameRoot };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", localHelper, "-Plan", Path.Combine(transaction, "plan.json") })
            start.ArgumentList.Add(argument);
        if (restore) start.ArgumentList.Add("-Restore");
        _ = Process.Start(start) ?? throw new IOException("工坊整理助手未能启动，游戏未退出。");
        parent.GetTree().Quit();
    }

    private static string? FindWorkshopRoot(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
            if (directory.Name == "2868840" && directory.Parent?.Name == "content" && directory.Parent.Parent?.Name == "workshop")
                return directory.FullName;
        return null;
    }
}
