namespace Sts2LanCoop.Core;

public sealed record WorkshopUnit(string Item, string ModId, List<SyncFile> Files);
public sealed record WorkshopOrganizationPlan
{
    public int Schema { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string GameRoot { get; init; } = "";
    public string Storage { get; init; } = "";
    public string WorkshopRoot { get; init; } = "";
    public int GamePid { get; init; }
    public long GameStartedUtcTicks { get; init; }
    public List<WorkshopUnit> Units { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

public static class WorkshopOrganization
{
    public static WorkshopOrganizationPlan Inspect(string root, CancellationToken cancel = default)
    {
        root = Path.GetFullPath(root);
        if (Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) != "2868840")
            throw new InvalidDataException("不是本游戏的工坊内容目录。");
        var plan = new WorkshopOrganizationPlan { WorkshopRoot = root };
        ModSyncFiles.NoLinks(root);
        foreach (var item in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();
            var itemId = Path.GetFileName(item);
            if (!ulong.TryParse(itemId, out _)) continue;
            try
            {
                var manifests = ModSyncFiles.ReadManifests(item);
                if (manifests.Count != 1)
                {
                    plan.Notes.Add(itemId + "：跳过（没有唯一模组清单，可能含多模组或版本分支）。");
                    continue;
                }
                var modId = ModSyncFiles.UnitFolder(manifests[0].Id);
                var destination = ModSyncFiles.Child(item, modId);
                if (Directory.Exists(destination))
                {
                    plan.Notes.Add(itemId + "/" + modId + "：已存在，保持原状。");
                    continue;
                }
                // A root manifest proves that this is a loose item. Already
                // nested layouts and branch structures are left in place.
                var rootManifest = Directory.EnumerateFiles(item, "*.json", SearchOption.TopDirectoryOnly)
                    .Any(file => RootManifestId(file) == modId);
                if (!rootManifest)
                {
                    plan.Notes.Add(itemId + "：已有子目录结构，保持原状。");
                    continue;
                }
                var files = new List<SyncFile>();
                long total = 0;
                foreach (var file in Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    cancel.ThrowIfCancellationRequested();
                    var name = ModSyncFiles.Relative(Path.GetRelativePath(item, file).Replace('\\', '/'));
                    ModSyncFiles.Relative(modId + "/" + name);
                    var size = new FileInfo(file).Length;
                    total += size;
                    if (files.Count >= 30000 || total > ModSyncFiles.MaxPackageSize)
                        throw new IOException("条目超过整理大小/文件数上限。");
                    files.Add(new(name, size, ModSyncRules.FileHash(file)));
                }
                plan.Units.Add(new(itemId, modId, files));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                plan.Notes.Add(itemId + "：跳过（" + ex.Message + "）。");
            }
        }
        return plan;
    }

    private static string? RootManifestId(string file)
    {
        try
        {
            using var doc = ModManifestJson.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String &&
                (root.TryGetProperty("has_dll", out _) || root.TryGetProperty("has_pck", out _)) ? id.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
