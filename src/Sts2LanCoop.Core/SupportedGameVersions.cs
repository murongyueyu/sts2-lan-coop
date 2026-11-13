namespace Sts2LanCoop.Core;

internal static class SupportedGameVersions
{
    public const string Display = "v0.107.1 / v0.111.0 / v0.113.0";

    public static bool Contains(string version) => version is "v0.107.1" or "v0.111.0" or "v0.113.0";
}
