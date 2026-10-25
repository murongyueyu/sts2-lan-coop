using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2LanCoop.Core;

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
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Revision { get; init; } = "";
    public string PayloadHash { get; init; } = "";
    public string GameVersion { get; init; } = "v0.107.1";
    public int PlayerCount { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong Owner { get; init; }
    public int Act { get; init; }
    public int Floor { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public List<SlotSummary> Slots { get; init; } = [];
}

public sealed class RunCatalog(string root)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string ValidateName(string? name)
    {
        var value = name?.Trim().Normalize() ?? "";
        if (value.Length is 0 or > 40 || value.Any(char.IsControl)) throw new InvalidDataException("存档名称应为1–40个可见字符。");
        return value;
    }
    private static void ValidateId(string id)
    { if (id.Length != 32 || !id.All(char.IsAsciiHexDigit)) throw new InvalidDataException("存档标识无效。"); }
    private string RunDir(string id) { ValidateId(id); return Path.Combine(root, id); }
    public IReadOnlyList<RunEntry> List() => !Directory.Exists(root) ? [] : Directory.EnumerateDirectories(root)
        .Where(d => Path.GetFileName(d).Length == 32 && Path.GetFileName(d).All(char.IsAsciiHexDigit))
        .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
        .Select(d => Read(Path.GetFileName(d))).OrderByDescending(e => e.SavedAt).ToArray();

    public RunEntry Read(string id)
    {
        var entry = JsonSerializer.Deserialize<RunEntry>(File.ReadAllBytes(Path.Combine(RunDir(id), "manifest.json")), Json)
            ?? throw new InvalidDataException("存档索引为空。");
        ValidateId(entry.Revision);
        if (entry.Id != id || entry.Schema != 1 || entry.PlayerCount is < 2 or > 4 ||
            entry.Slots.Count != entry.PlayerCount || entry.Slots.Select(s => s.NativeId).Distinct().Count() != entry.PlayerCount ||
            entry.Slots.Count(s => s.NativeId == 1) != 1)
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
        if (payload.Length is 0 or > 16 * 1024 * 1024) throw new InvalidDataException("存档载荷大小异常。");
        if (proposed.PlayerCount is < 2 or > 4 || proposed.Slots.Count != proposed.PlayerCount ||
            proposed.Slots.Select(s => s.NativeId).Distinct().Count() != proposed.PlayerCount || proposed.Slots.Count(s => s.NativeId == 1) != 1)
            throw new InvalidDataException("保存人数或主机角色不正确。");
        var directory = RunDir(proposed.Id);
        if (File.Exists(Path.Combine(directory, "manifest.json")))
        {
            var previous = Read(proposed.Id);
            if (previous.Owner != proposed.Owner || previous.PlayerCount != proposed.PlayerCount ||
                !previous.Slots.Select(s => s.NativeId).Order().SequenceEqual(proposed.Slots.Select(s => s.NativeId).Order()))
                throw new InvalidDataException("已有存档的主机、人数和角色槽位不能改变。");
        }
        var revision = Guid.NewGuid().ToString("N");
        var next = proposed with { Name = ValidateName(proposed.Name), Revision = revision,
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
