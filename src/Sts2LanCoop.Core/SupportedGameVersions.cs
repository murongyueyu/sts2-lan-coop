namespace Sts2LanCoop.Core;

internal static class SupportedGameVersions
{
    public const string Display = "v0.107.1 / v0.111.0 / v0.111.1 / v0.113.0 / v0.113.1 / v0.114.0";

    // v0.113.1 is the public-beta hotfix label for the same assembly family as v0.113.0
    // (sts2.dll still reports 0.113.0; only release_info.json carries v0.113.1).
    public static bool Contains(string version) => version is "v0.107.1" or "v0.111.0" or "v0.111.1" or "v0.113.0" or "v0.113.1" or "v0.114.0";
}
