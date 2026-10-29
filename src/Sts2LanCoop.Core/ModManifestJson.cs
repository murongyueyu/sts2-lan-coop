using System.Text.Json;

namespace Sts2LanCoop.Core;

internal static class ModManifestJson
{
    // JsonDocument's byte-buffer overload does not consume a UTF-8 BOM. Keep the
    // original bytes for hashing and transport; omit only the prefix while parsing.
    public static JsonDocument Parse(byte[] bytes)
    {
        ReadOnlyMemory<byte> json = bytes;
        if (json.Span.StartsWith(new byte[] { 239, 187, 191 })) json = json[3..];
        return JsonDocument.Parse(json);
    }
}
