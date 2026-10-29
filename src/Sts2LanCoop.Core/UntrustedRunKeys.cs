namespace Sts2LanCoop.Core;

// Only prevents automatic import of an unknown working copy. Valid catalog revisions remain usable.
public sealed class UntrustedRunKeys(string root)
{
    private string Marker(string key)
    {
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigit)) throw new InvalidDataException("无效的对局来源标识。");
        return Path.Combine(root, key.ToUpperInvariant() + ".marker");
    }
    public bool Contains(string key) => File.Exists(Marker(key));
    public void Mark(string key)
    {
        var path = Marker(key);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(root);
        var temp = path + ".tmp";
        File.WriteAllText(temp, "State divergence: preserve catalog checkpoint; do not auto-import working copies.");
        File.Move(temp, path, true);
    }
}
