using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2LanCoop.Core;

public sealed record SyncMessage
{
    public int RequestId { get; init; }
    public string Operation { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Hash { get; init; } = "";
    public long Offset { get; init; }
    public long Size { get; init; }
    [JsonIgnore] public byte[] Data { get; init; } = [];
    public string Error { get; init; } = "";
}

// Protocol 2 keeps small JSON metadata and transfers file bytes directly, without Base64.
public static class SyncTransferWire
{
    public const int ChunkSize = 48 * 1024;
    public const int WindowSize = 16;
    private const int MaxMetadata = 4096;
    private static readonly byte[] Magic = [0xF3, 0x53, 0x4D, 2];
    public static bool IsControl(byte[] bytes) => bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Magic.AsSpan(0, 3));
    private static void Validate(SyncMessage message)
    {
        if (message.RequestId <= 0 || message.Offset < 0 || message.Size < 0 ||
            message.Operation == null || message.Operation.Length > 16 || message.Revision == null || message.Revision.Length > 128 ||
            message.Hash == null || message.Hash.Length > 64 || message.Error == null || message.Error.Length > 512 ||
            message.Data == null || message.Data.Length > ChunkSize)
            throw new InvalidDataException("同步报文头或数据超限。");
    }
    public static byte[] Encode(SyncMessage message)
    {
        Validate(message);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(message);
        if (metadata.Length > MaxMetadata) throw new InvalidDataException("同步报文头过大。");
        var bytes = new byte[8 + metadata.Length + message.Data.Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4, 4), metadata.Length);
        metadata.CopyTo(bytes, 8); message.Data.CopyTo(bytes, 8 + metadata.Length);
        return bytes;
    }
    public static SyncMessage Decode(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes.Length > 8 + MaxMetadata + ChunkSize || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("模组传输协议不同或报文过大，请双方统一更新到0.7.0-alpha.6或更新的同版Mod。");
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4, 4));
        if (length is <= 0 or > MaxMetadata || length > bytes.Length - 8 || bytes.Length - 8 - length > ChunkSize)
            throw new InvalidDataException("同步报文长度无效。");
        var message = JsonSerializer.Deserialize<SyncMessage>(bytes.AsSpan(8, length)) ?? throw new InvalidDataException("同步报文头为空。");
        message = message with { Data = bytes[(8 + length)..] };
        Validate(message); return message;
    }
}

// Both outstanding requests and received-but-not-written chunks count toward the memory bound.
public sealed class SyncDownloadWindow
{
    private readonly long _length;
    private readonly Dictionary<long, int> _pending = [];
    private readonly SortedDictionary<long, byte[]> _received = [];
    private long _next;
    public long Committed { get; private set; }
    public bool Complete => Committed == _length;
    public SyncDownloadWindow(long length, long start)
    {
        if (length is <= 0 or > ModSyncFiles.MaxPackageSize || start < 0 || start > length)
            throw new InvalidDataException("下载长度或续传位置无效。");
        _length = length; _next = Committed = start;
    }
    public IEnumerable<long> Fill()
    {
        while (_pending.Count + _received.Count < SyncTransferWire.WindowSize && _next < _length)
        {
            var offset = _next;
            var count = (int)Math.Min(SyncTransferWire.ChunkSize, _length - offset);
            _pending.Add(offset, count); _next += count; yield return offset;
        }
    }
    public void Receive(long offset, byte[] data)
    {
        if (!_pending.TryGetValue(offset, out var count) || data.Length != count)
            throw new InvalidDataException("下载分片偏移、长度或请求状态无效。");
        _pending.Remove(offset); _received.Add(offset, data);
    }
    public bool TakeNext(out byte[] data)
    {
        if (!_received.Remove(Committed, out var next)) { data = []; return false; }
        data = next; Committed += next.Length; return true;
    }
}
