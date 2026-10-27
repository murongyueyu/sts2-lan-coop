using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2LanCoop.Core;

public enum RunKind { Lan, Solo }
public sealed record CatalogItem(string Id, RunEntry? Entry, string? Error);
public sealed record RetiredRun(string Id, string SourceKey, string Reason, string[] Hashes);

public sealed record SlotSummary
{
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong NativeId { get; init; }
    public string Character { get; init; } = "";
    public string CharacterTitle { get; init; } = "";
    public Dictionary<string, string> Titles { get; init; } = [];
    public int Hp { get; init; }
    public int MaxHp { get; init; }
    public int Gold { get; init; }
    public List<string> Relics { get; init; } = [];
    public List<string> Deck { get; init; } = [];
    public List<string> Potions { get; init; } = [];
}

public sealed record RunEntry
{
    public int Schema { get; init; } = 1;
    public RunKind Kind { get; init; }
    public string SourceKey { get; init; } = "";
    public string GameMode { get; init; } = "Standard";
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Revision { get; init; } = "";
    public string PayloadHash { get; init; } = "";
    public string GameVersion { get; init; } = "v0.107.1";
    public string ModEnvironment { get; init; } = "";
    public List<SyncMod> ModProfile { get; init; } = [];
    public int PlayerCount { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong Owner { get; init; }
    public int Act { get; init; }
    public int Floor { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public List<SlotSummary> Slots { get; init; } = [];
}

public sealed class RunCatalog(string root, RunKind kind = RunKind.Lan)
{
    public string RootPath => Path.GetFullPath(root);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string ValidateName(string? name)
    {
        var value = name?.Trim().Normalize() ?? "";
        if (value.Length is 0 or > 40 || value.Any(char.IsControl)) throw new InvalidDataException("存档名称应为1–40个可见字符。");
        return value;
    }
    private static void ValidateId(string id)
    { if (id.Length != 32 || !id.All(char.IsAsciiHexDigit)) throw new InvalidDataException("存档标识无效。"); }
    private string RunDir(string id) { ValidateId(id); var path = Path.Combine(RootPath, id); CheckPlain(path); return path; }
    private void CheckPlain(string path)
    {
        var full = Path.GetFullPath(path);
        if (full != RootPath && !full.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("存档路径越界。");
        for (var dir = new DirectoryInfo(full); dir != null; dir = dir.Parent)
        {
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("存档路径包含链接，停止操作。");
            if (dir.FullName == RootPath) break;
        }
    }
    private void CheckTree(string path)
    {
        CheckPlain(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("存档目录包含链接，停止清理。");
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }
    public IReadOnlyList<CatalogItem> Inspect()
    {
        if (!Directory.Exists(root)) return [];
        CheckPlain(root);
        return Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d).Length == 32 && Path.GetFileName(d).All(char.IsAsciiHexDigit))
            .Where(d => !IsRetired(Path.GetFileName(d), ""))
            .Select(d => { var id = Path.GetFileName(d); try { return new CatalogItem(id, Read(id), null); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
                { return new CatalogItem(id, null, ex.Message); } }).ToArray();
    }
    public IReadOnlyList<RunEntry> List() => !Directory.Exists(root) ? [] : Directory.EnumerateDirectories(root)
        .Where(d => Path.GetFileName(d).Length == 32 && Path.GetFileName(d).All(char.IsAsciiHexDigit))
        .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
        .Where(d => !IsRetired(Path.GetFileName(d), ""))
        .Select(d => Read(Path.GetFileName(d))).OrderByDescending(e => e.SavedAt).ToArray();

    public RunEntry Read(string id)
    {
        ValidateId(id);
        if (IsRetired(id, "")) throw new InvalidOperationException("此存档已删除或结束，不能继续；物理清理失败时可重试清理。");
        var entry = JsonSerializer.Deserialize<RunEntry>(File.ReadAllBytes(Path.Combine(RunDir(id), "manifest.json")), Json)
            ?? throw new InvalidDataException("存档索引为空。");
        ValidateId(entry.Revision);
        if (entry.Id != id || entry.Schema is not (1 or 2) || entry.Kind != kind || !ValidPlayers(entry) ||
            entry.Slots.Count != entry.PlayerCount || entry.Slots.Select(s => s.NativeId).Distinct().Count() != entry.PlayerCount ||
            (entry.Schema == 1 && kind != RunKind.Lan))
            throw new InvalidDataException("存档索引、人数或主机角色无效，未改动原文件。");
        return entry;
    }

    public byte[] ReadPayload(RunEntry entry)
    {
        var current = Read(entry.Id);
        if (current.Revision != entry.Revision) throw new InvalidOperationException("存档已更新，请刷新列表。");
        var payload = File.ReadAllBytes(Path.Combine(RunDir(entry.Id), "revisions", entry.Revision, "native-run.save"));
        if (Convert.ToHexString(SHA256.HashData(payload)) != entry.PayloadHash)
            throw new InvalidDataException("存档校验失败，未加载或覆盖。请保留文件以便恢复。");
        return payload;
    }

    public RunEntry Commit(RunEntry proposed, byte[] payload)
    {
        ValidateId(proposed.Id);
        if (proposed.Kind != kind || IsRetired(proposed.Id, proposed.SourceKey)) throw new InvalidOperationException("此对局已删除或结束，拒绝迟到保存。");
        if (payload.Length is 0 or > 16 * 1024 * 1024) throw new InvalidDataException("存档载荷大小异常。");
        if (!ValidPlayers(proposed) || proposed.Slots.Count != proposed.PlayerCount ||
            proposed.Slots.Select(s => s.NativeId).Distinct().Count() != proposed.PlayerCount)
            throw new InvalidDataException("保存人数或主机角色不正确。");
        var directory = RunDir(proposed.Id);
        if (File.Exists(Path.Combine(directory, "manifest.json")))
        {
            var previous = Read(proposed.Id);
            if (previous.Owner != proposed.Owner || previous.PlayerCount != proposed.PlayerCount ||
                !previous.Slots.Select(s => s.NativeId).Order().SequenceEqual(proposed.Slots.Select(s => s.NativeId).Order()))
                throw new InvalidDataException("已有存档的主机、人数和角色槽位不能改变。");
            if (previous.Schema == 1)
            {
                var backup = Path.Combine(directory, "manifest.schema1.backup");
                if (!File.Exists(backup)) File.Copy(Path.Combine(directory, "manifest.json"), backup);
            }
        }
        var revision = Guid.NewGuid().ToString("N");
        var next = proposed with { Schema = 2, Name = ValidateName(proposed.Name), Revision = revision,
            PayloadHash = Convert.ToHexString(SHA256.HashData(payload)), SavedAt = DateTimeOffset.UtcNow };
        var revisions = Path.Combine(directory, "revisions");
        var temporary = Path.Combine(revisions, ".staging-" + revision);
        Directory.CreateDirectory(temporary);
        File.WriteAllBytes(Path.Combine(temporary, "native-run.save"), payload);
        File.WriteAllBytes(Path.Combine(temporary, "summary.json"), JsonSerializer.SerializeToUtf8Bytes(next, Json));
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(temporary, "native-run.save")))) != next.PayloadHash)
            throw new IOException("保存读回校验失败。");
        Directory.Move(temporary, Path.Combine(revisions, revision));
        AtomicManifest(directory, next);
        return next;
    }

    private bool ValidPlayers(RunEntry entry) => kind == RunKind.Solo ? entry.PlayerCount == 1 :
        entry.PlayerCount is >= 2 and <= 4 && entry.Slots.Count(s => s.NativeId == 1) == 1;

    private string RetiredPath => Path.Combine(RootPath, "retired");
    public bool IsRetired(string id, string sourceKey, string? hash = null)
    {
        if (!Directory.Exists(RetiredPath)) return false;
        CheckPlain(RetiredPath);
        return Directory.EnumerateFiles(RetiredPath, "*.json").Select(file => JsonSerializer.Deserialize<RetiredRun>(File.ReadAllBytes(file), Json)
            ?? throw new InvalidDataException("退役记录损坏，停止自动导入。"))
            .Any(r => r.Id == id || sourceKey.Length > 0 && r.SourceKey == sourceKey || hash != null && r.Hashes.Contains(hash));
    }
    public void Retire(string id, string? revision, string reason, string sourceKey = "")
    {
        var directory = RunDir(id);
        if (!IsRetired(id, ""))
        {
            RunEntry? entry = null;
            if (revision != null) { entry = Read(id); if (entry.Revision != revision) throw new InvalidOperationException("存档已变化，请重新确认。"); }
            var hashes = new HashSet<string>();
            if (Directory.Exists(directory))
            {
                CheckTree(directory);
                foreach (var file in Directory.EnumerateFiles(directory, "native-run.save", SearchOption.AllDirectories))
                    hashes.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            }
            Directory.CreateDirectory(RetiredPath);
            var temporary = Path.Combine(RetiredPath, id + ".tmp");
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new RetiredRun(id, entry?.SourceKey is { Length: > 0 } key ? key : sourceKey, reason, hashes.ToArray()), Json));
            File.Move(temporary, Path.Combine(RetiredPath, id + ".json"), true);
        }
        RetryRetired();
    }
    public void RetryRetired()
    {
        if (!Directory.Exists(RetiredPath)) return;
        CheckPlain(RetiredPath);
        foreach (var file in Directory.EnumerateFiles(RetiredPath, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file); var directory = RunDir(id);
            if (Directory.Exists(directory)) { CheckTree(directory); Directory.Delete(directory, true); }
        }
    }
    public void Prune(RunEntry entry, params string[] pinned)
    {
        var migration = Path.Combine(RunDir(entry.Id), "manifest.schema1.backup");
        if (File.Exists(migration))
        {
            var old = JsonSerializer.Deserialize<RunEntry>(File.ReadAllBytes(migration), Json) ?? throw new InvalidDataException("迁移备份无效。");
            pinned = pinned.Append(old.Revision).ToArray();
        }
        var revisions = Path.Combine(RunDir(entry.Id), "revisions");
        if (!Directory.Exists(revisions)) return;
        var ordered = Directory.EnumerateDirectories(revisions).Where(d => Path.GetFileName(d).Length == 32)
            .OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
        foreach (var directory in ordered.Skip(3).Where(d => Path.GetFileName(d) != entry.Revision && !pinned.Contains(Path.GetFileName(d))))
        { CheckTree(directory); Directory.Delete(directory, true); }
    }

    public RunEntry Rename(string id, string name)
    {
        var next = Read(id) with { Name = ValidateName(name) };
        AtomicManifest(RunDir(id), next);
        return next;
    }
    private static void AtomicManifest(string directory, RunEntry entry)
    {
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(entry, Json));
            File.Move(temp, Path.Combine(directory, "manifest.json"), overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
