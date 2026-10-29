using System.IO.Compression;
using System.Text.Json;

namespace Sts2LanCoop.Core;

// A launcher may put several mods in one directory. Verify each host-declared
// file in that mod's loaded directory, rather than hashing all of its neighbours.
public static class MobileModFiles
{
    private const string AliasMarker = "android_generated_manifest_alias";
    private const int MaxManifestBytes = 2 * 1024 * 1024;

    public static List<SyncFile> RuntimeFiles(string directory, string id)
    {
        ModSyncFiles.Relative(id);
        if (id.Contains('/')) throw new InvalidDataException("模组ID不能包含路径。");
        var files = new List<SyncFile>();
        foreach (var name in new[] { id + ".dll", id + ".pck", id + ".json", "mod_manifest.json" })
        {
            var path = CheckedPath(directory, name);
            if (!File.Exists(path)) continue;
            if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                using var doc = ModManifestJson.Parse(ReadManifest(path));
                if (!IsManifest(doc.RootElement, id)) continue;
            }
            files.Add(ReadFile(path, name));
        }
        return files;
    }

    public static (List<SyncFile> Files, List<string> Notes) Inspect(SyncMod expected, string directory, string cache)
    {
        if (expected.Files.Count is 0 or > 30000 || expected.Files.Any(f => f.Size < 0) ||
            expected.Files.Sum(f => f.Size) > ModSyncFiles.MaxPackageSize ||
            expected.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Files.Count ||
            ModSyncRules.Fingerprint(expected.Files) != expected.Fingerprint)
            throw new InvalidDataException("主机模组文件清单或指纹无效：" + expected.Id);
        ModSyncFiles.Relative(expected.Id);
        if (expected.Id.Contains('/')) throw new InvalidDataException("模组ID不能包含路径。");
        var files = new List<SyncFile>();
        var notes = new List<string>();
        foreach (var expectedFile in expected.Files)
        {
            var path = CheckedPath(directory, expectedFile.Name);
            var actual = File.Exists(path) ? ReadFile(path, expectedFile.Name) : null;
            if (actual == expectedFile) { files.Add(actual); continue; }

            // The launcher renames a manifest, adds one marker and serializes it
            // again. Its other fields must still equal the exact host manifest.
            var aliasName = expected.Id + ".json";
            if (!expectedFile.Name.Contains('/') && expectedFile.Name.EndsWith(".json", StringComparison.Ordinal) &&
                (actual == null || expectedFile.Name == aliasName))
            {
                var aliasPath = CheckedPath(directory, aliasName);
                if (File.Exists(aliasPath) && AliasMatches(expected, expectedFile, aliasPath, cache))
                {
                    files.Add(expectedFile);
                    notes.Add($"{expectedFile.Name} → {aliasName}：已核实启动器清单别名，仅增加 {AliasMarker} 及JSON排版变化。");
                    continue;
                }
                if (File.Exists(aliasPath))
                    notes.Add($"{expectedFile.Name}：启动器别名尚未通过等价核验；具体缺失或内容差异见报告，版本号相同不能代替文件校验。");
            }
            if (actual != null) files.Add(actual);
        }
        return (files, notes);
    }

    public static List<string> Differences(SyncMod expected, SyncMod actual)
    {
        var found = actual.Files.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var file in expected.Files)
        {
            if (!found.TryGetValue(file.Name, out var local)) result.Add($"缺少文件：{file.Name}");
            else if (file.Size != local.Size) result.Add($"大小不同：{file.Name}（主机 {file.Size} B，手机 {local.Size} B）");
            else if (file.Hash != local.Hash) result.Add($"内容SHA256不同：{file.Name}（主机 {Short(file.Hash)}，手机 {Short(local.Hash)}）");
        }
        return result;
        static string Short(string hash) => hash[..Math.Min(12, hash.Length)];
    }

    // Keep the verified original cache intact; only the manual-import copy gets
    // a containing directory, preventing the launcher from merging mod units.
    public static string PrepareImport(string archive, SyncMod mod, string importDirectory, CancellationToken cancel = default)
    {
        ModSyncFiles.Relative(mod.Id);
        if (mod.Id.Contains('/') || mod.Bundle.Length != 64 || !mod.Bundle.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("手动导入包标识无效。");
        ModSyncFiles.VerifyMod(archive, mod);
        cancel.ThrowIfCancellationRequested();
        Directory.CreateDirectory(importDirectory);
        var destination = Path.Combine(importDirectory, mod.Id + "-" + mod.Bundle + ".zip");
        var wrappedFiles = mod.Files.Select(f => f with { Name = mod.Id + "/" + f.Name }).ToList();
        if (File.Exists(destination))
        {
            ModSyncFiles.VerifyPackage(destination, wrappedFiles);
            return destination;
        }
        var temp = Path.Combine(importDirectory, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var original = ZipFile.OpenRead(archive))
            using (var wrapped = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (var file in mod.Files)
                {
                    cancel.ThrowIfCancellationRequested();
                    using var input = original.GetEntry(file.Name)!.Open();
                    using var output = wrapped.CreateEntry(mod.Id + "/" + file.Name, CompressionLevel.Fastest).Open();
                    var buffer = new byte[65536];
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                    }
                }
            cancel.ThrowIfCancellationRequested();
            ModSyncFiles.VerifyPackage(temp, wrappedFiles);
            cancel.ThrowIfCancellationRequested();
            File.Move(temp, destination);
            return destination;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool AliasMatches(SyncMod mod, SyncFile expected, string aliasPath, string cache)
    {
        using var actual = ModManifestJson.Parse(ReadManifest(aliasPath));
        if (!IsManifest(actual.RootElement, mod.Id) ||
            !actual.RootElement.TryGetProperty(AliasMarker, out var marker) || marker.ValueKind != JsonValueKind.True ||
            expected.Size > MaxManifestBytes) return false;
        byte[] data;
        if (mod.Manifest is { } manifest && manifest.Name == expected.Name)
        {
            data = manifest.Data;
        }
        else
        {
            if (mod.Bundle.Length != 64 || !mod.Bundle.All(char.IsAsciiHexDigit)) return false;
            var archive = CheckedPath(cache, mod.Bundle + ".zip");
            if (!File.Exists(archive)) return false;
            using var zip = ZipFile.OpenRead(archive);
            var entries = zip.Entries.Where(e => e.FullName == expected.Name).ToArray();
            if (entries.Length != 1 || entries[0].Length != expected.Size) return false;
            using var input = entries[0].Open();
            data = new byte[checked((int)expected.Size)];
            input.ReadExactly(data);
            if (input.ReadByte() != -1) return false;
        }
        // Prove these are the host's bytes, even if the cached ZIP was modified.
        if (data.LongLength != expected.Size || ModSyncRules.Digest(data) != expected.Hash) return false;
        using var original = ModManifestJson.Parse(data);
        if (!IsManifest(original.RootElement, mod.Id) || original.RootElement.TryGetProperty(AliasMarker, out _)) return false;
        return Canonical(original.RootElement, false).SequenceEqual(Canonical(actual.RootElement, true));
    }

    public static SyncManifest ReadHostManifest(SyncMod mod)
    {
        SyncManifest? manifest = null;
        foreach (var file in mod.Files.Where(f => !f.Name.Contains('/') && f.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var data = ReadManifest(CheckedPath(mod.Directory, file.Name));
            if (data.LongLength != file.Size || ModSyncRules.Digest(data) != file.Hash)
                throw new IOException("主机清单在核验后变化，请重启游戏：" + mod.Id);
            JsonDocument doc;
            try { doc = ModManifestJson.Parse(data); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (!IsManifest(doc.RootElement, mod.Id)) continue;
                if (manifest != null) throw new InvalidDataException("模组包含重复清单：" + mod.Id);
                manifest = new(file.Name, data);
            }
        }
        return manifest ?? throw new InvalidDataException("未找到可核验的主机模组清单：" + mod.Id);
    }

    private static bool IsManifest(JsonElement root, string id) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == id &&
        (root.TryGetProperty("has_dll", out _) || root.TryGetProperty("has_pck", out _));

    private static byte[] Canonical(JsonElement root, bool omitMarker)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) Write(root, writer, omitMarker);
        return bytes.ToArray();

        static void Write(JsonElement value, Utf8JsonWriter writer, bool omit)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var properties = value.EnumerateObject().ToArray();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new InvalidDataException("模组清单包含重复JSON字段，不能归一化。");
                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (omit && property.Name == AliasMarker) continue;
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer, false);
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(item, writer, false);
                writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }

    private static byte[] ReadManifest(string path)
    {
        if (new FileInfo(path).Length > MaxManifestBytes) throw new InvalidDataException("模组清单超过2MiB。");
        return File.ReadAllBytes(path);
    }

    private static SyncFile ReadFile(string path, string name) => new(name, new FileInfo(path).Length, ModSyncRules.FileHash(path));

    private static string CheckedPath(string directory, string name)
    {
        var result = ModSyncFiles.Child(directory, name);
        var current = Path.GetFullPath(directory);
        Check(current);
        foreach (var part in name.Split('/')) { current = Path.Combine(current, part); Check(current); }
        return result;
        static void Check(string path)
        {
            if (File.Exists(path) || Directory.Exists(path))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("拒绝链接目录或文件。");
        }
    }
}
