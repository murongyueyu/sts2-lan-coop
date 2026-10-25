using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2LanCoop.Core;

public sealed class LanSettings
{
    public const int MaximumPlayers = 4;
    [JsonRequired]
    public int SchemaVersion { get; set; } = 1;
    [JsonRequired]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong PlayerId { get; set; } = CreateId();
    public string PlayerName { get; set; } = "玩家";
    public ushort HostPort { get; set; } = Endpoint.DefaultPort;
    public string ExternalEndpoint { get; set; } = "";
    public string QuickReloadHotkey { get; set; } = "Ctrl+F8";
    public List<string> RecentConnections { get; set; } = [];

    public static ulong CreateId()
    {
        ulong value;
        do { value = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7FFF_FFFF_FFFF_FFFF; }
        while (value <= 3000);
        return value;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("配置版本不受支持，请保留原文件并检查 Mod 版本。");
        if (PlayerId <= 3000) throw new InvalidDataException("玩家 ID 无效，请保留配置以便检查，避免存档身份变化。");
        if (HostPort == 0) throw new InvalidDataException("监听端口必须在 1–65535 之间。");
        PlayerName = PlayerName?.Trim() ?? "";
        if (PlayerName.Length is 0 or > 24 || PlayerName.Any(char.IsControl))
            throw new InvalidDataException("昵称应为 1–24 个字符，不能包含控制字符。");
        RecentConnections ??= [];
        RecentConnections = RecentConnections
            .Select(s => Endpoint.TryParse(s, out var ep, out _) ? ep!.ToString() : null)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        ExternalEndpoint ??= "";
        _ = ReloadShortcut.Parse(QuickReloadHotkey);
    }

    // Call only after a real lobby join succeeds, never on an attempted connection.
    public void RememberSuccessfulConnection(Endpoint endpoint)
    {
        RecentConnections.RemoveAll(s => string.Equals(s, endpoint.ToString(), StringComparison.OrdinalIgnoreCase));
        RecentConnections.Insert(0, endpoint.ToString());
        if (RecentConnections.Count > 5) RecentConnections.RemoveRange(5, RecentConnections.Count - 5);
    }
}

public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public LanSettings Load()
    {
        if (!File.Exists(filePath))
        {
            var initial = new LanSettings();
            Save(initial); // Stable identity must be persisted before starting a session.
            return initial;
        }
        var settings = JsonSerializer.Deserialize<LanSettings>(File.ReadAllText(filePath), Options)
            ?? throw new InvalidDataException("配置文件为空，未覆盖原文件。");
        settings.Validate();
        return settings;
    }

    public void Save(LanSettings settings)
    {
        settings.Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".lan-settings-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
