using System.Security.Cryptography;
using System.Text.Json;

namespace Sts2LanCoop.Core;

// A journaled, exact-byte lease of the native multiplayer working slot. Never a Steam-to-LAN converter.
public sealed class WorkingSaveLease(string journal, string working)
{
    private sealed record Lease(byte[]? Original, byte[]? Backup, string[] Allowed);
    public bool Exists => File.Exists(journal);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    private Lease Load() => JsonSerializer.Deserialize<Lease>(File.ReadAllBytes(journal)) ?? throw new IOException("多人工作槽保护记录无效。");
    private void Write(Lease lease)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        var temp = journal + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(lease));
        File.Move(temp, journal, true);
    }
    public void Acquire(Func<byte[], bool> preserve)
    {
        if (Exists) throw new InvalidOperationException("上次多人工作槽尚未归还，请先恢复。");
        var main = Read(working); var backup = Read(working + ".backup");
        // Predicate must validate every existing payload, including the fallback.
        var keepMain = main != null && preserve(main); var keepBackup = backup != null && preserve(backup);
        Write(new(keepMain ? main : null, keepBackup ? backup : null,
            new[] { main, backup }.Where(b => b != null).Select(b => Hash(b!)).ToArray()));
    }
    public void Observe(byte[] written)
    {
        if (!Exists) return;
        var lease = Load(); Write(lease with { Allowed = lease.Allowed.Append(Hash(written)).Distinct().ToArray() });
    }
    public bool HasConflict()
    {
        if (!Exists) return false;
        var lease = Load();
        return new[] { working, working + ".backup" }.Any(path => Read(path) is { } data && !lease.Allowed.Contains(Hash(data)));
    }
    // Only call after an explicit user confirmation. Unknown bytes are preserved, never silently adopted.
    public string PreserveConflictAndRestore(string backupRoot)
    {
        var journalBytes = File.ReadAllBytes(journal);
        var lease = JsonSerializer.Deserialize<Lease>(journalBytes) ?? throw new IOException("多人工作槽保护记录无效。");
        var main = Read(working); var fallback = Read(working + ".backup");
        var destination = Path.GetFullPath(Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
        if (new[] { journal, working, working + ".backup" }.Any(p => Path.GetFullPath(p).StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("恢复备份目录不能包含工作文件。");
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "lease.json"), journalBytes);
        if (main != null) File.WriteAllBytes(Path.Combine(destination, "current_run_mp.save"), main);
        if (fallback != null) File.WriteAllBytes(Path.Combine(destination, "current_run_mp.save.backup"), fallback);
        // Verify durable copies and refuse changes made during preservation; recovery can safely be retried.
        bool Same(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.SequenceEqual(b);
        if (!File.ReadAllBytes(Path.Combine(destination, "lease.json")).SequenceEqual(journalBytes) ||
            main != null && !File.ReadAllBytes(Path.Combine(destination, "current_run_mp.save")).SequenceEqual(main) ||
            fallback != null && !File.ReadAllBytes(Path.Combine(destination, "current_run_mp.save.backup")).SequenceEqual(fallback) ||
            !File.ReadAllBytes(journal).SequenceEqual(journalBytes) || !Same(Read(working), main) || !Same(Read(working + ".backup"), fallback))
            throw new IOException("工作文件在备份期间发生变化，已保留副本，请停止其他进程后重试。");
        Write(lease with { Allowed = lease.Allowed.Concat(new[] { main, fallback }.Where(b => b != null).Select(b => Hash(b!))).Distinct().ToArray() });
        Restore();
        return destination;
    }
    public void Restore()
    {
        if (!Exists) return;
        var lease = Load();
        foreach (var path in new[] { working, working + ".backup" })
            if (Read(path) is { } data && !lease.Allowed.Contains(Hash(data)))
                throw new IOException("多人工作档或备份出现未记录的写入，已保留双方。请在继续对局中处理工作档冲突。");
        // Record every intermediate restored value before writing, so crash recovery remains idempotent.
        Write(lease with { Allowed = lease.Allowed.Concat(new[] { lease.Original, lease.Backup }.Where(b => b != null).Select(b => Hash(b!))).Distinct().ToArray() });
        RestoreFile(working, lease.Original); RestoreFile(working + ".backup", lease.Backup);
        File.Delete(journal);
    }
    private static void RestoreFile(string path, byte[]? bytes)
    {
        if (bytes == null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".lan-restore";
        File.WriteAllBytes(temp, bytes); File.Move(temp, path, true);
    }
}
