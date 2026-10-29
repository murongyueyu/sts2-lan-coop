using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2LanCoop.Core;

public sealed record ReloadFrame
{
    public int Version { get; init; } = 3;
    public string Ending { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Session { get; init; } = "";
    public string Request { get; init; } = "";
    public string RunId { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Digest { get; init; } = "";
    public string Detail { get; init; } = "";
    public ulong Epoch { get; init; }
    public int Length { get; init; }
    public int Index { get; init; }
    public byte[] Data { get; init; } = [];
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong[] Slots { get; init; } = [];
}

public static class ReloadWire
{
    private static readonly byte[] Magic = [0xF4, 0x53, 0x4C, 0x31];
    public const int ChunkSize = 16384;
    public const int MaximumSaveBytes = 16 * 1024 * 1024;
    public static bool Matches(byte[] bytes) => bytes.AsSpan().StartsWith(Magic);
    public static byte[] Encode(ReloadFrame frame)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        if (bytes.Length > 32768) throw new InvalidDataException("快速SL报文过大。");
        return [..Magic, ..bytes];
    }
    public static ReloadFrame Decode(byte[] bytes)
    {
        if (!Matches(bytes) || bytes.Length > 32772) throw new InvalidDataException("无效快速SL报文。");
        var frame = JsonSerializer.Deserialize<ReloadFrame>(bytes.AsSpan(4)) ?? throw new InvalidDataException("SL报文为空。");
        if (frame.Version != 3 || frame.Ending.Length > 32 || frame.Data.Length > ChunkSize || frame.Slots.Length > 4 || frame.Detail.Length > 500)
            throw new InvalidDataException("快速SL协议不兼容或数据超限。");
        return frame;
    }
}

public sealed class ReloadDownload
{
    private readonly byte[] _bytes;
    private readonly bool[] _received;
    private readonly string _digest;
    public ReloadDownload(int length, string digest)
    {
        if (length is <= 0 or > ReloadWire.MaximumSaveBytes || digest.Length != 64 || !digest.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("SL存档长度或摘要无效。");
        _bytes = new byte[length]; _digest = digest;
        _received = new bool[(length + ReloadWire.ChunkSize - 1) / ReloadWire.ChunkSize];
    }
    public void Add(int index, byte[] data)
    {
        if (index < 0 || index >= _received.Length || data.Length != Math.Min(ReloadWire.ChunkSize, _bytes.Length - index * ReloadWire.ChunkSize))
            throw new InvalidDataException("SL存档分块不完整。");
        var target = _bytes.AsSpan(index * ReloadWire.ChunkSize, data.Length);
        if (_received[index] && !target.SequenceEqual(data)) throw new InvalidDataException("SL存档重复分块不一致。");
        data.CopyTo(target); _received[index] = true;
    }
    public byte[] Complete()
    {
        if (_received.Any(value => !value) || !string.Equals(Convert.ToHexString(SHA256.HashData(_bytes)), _digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SL存档未收齐或校验失败。");
        return (byte[])_bytes.Clone();
    }
}

public static class GameEpochWire
{
    private static readonly byte[] Magic = [0xF5, 0x4C, 0x41, 0x4E];
    public static byte[] Wrap(ulong epoch, byte[] data)
    {
        var bytes = new byte[12 + data.Length]; Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(4, 8), epoch); data.CopyTo(bytes, 12); return bytes;
    }
    public static bool TryRead(ulong epoch, byte[] bytes, out byte[] payload)
    {
        payload = [];
        if (bytes.Length < 12 || !bytes.AsSpan().StartsWith(Magic) || BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(4,8)) != epoch) return false;
        payload = bytes[12..]; return true;
    }
}

public sealed record ReloadShortcut(int FunctionKey, bool Ctrl, bool Alt, bool Shift)
{
    public static ReloadShortcut? Parse(string value)
    {
        if (value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = value.Trim().ToUpperInvariant().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Distinct().Count() != parts.Length ||
            !parts[^1].StartsWith('F') || !int.TryParse(parts[^1][1..], out var key) || key is < 1 or > 12 ||
            parts[..^1].Any(p => p is not ("CTRL" or "ALT" or "SHIFT")))
            throw new InvalidDataException("SL快捷键请填写Ctrl+F8、其他F1–F12组合，或off关闭。");
        return new(key, parts.Contains("CTRL"), parts.Contains("ALT"), parts.Contains("SHIFT"));
    }
}
