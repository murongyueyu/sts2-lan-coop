using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

internal static class RunStateTrust
{
    public static bool Trusted { get; private set; } = true;
    public static UntrustedRunKeys Keys => new(ProjectSettings.GlobalizePath($"user://sts2_lan_coop/untrusted/{RunStorage.Scope}"));
    public static void Reset() => Trusted = true;
    public static void Recovered() => Trusted = true;
    public static void Protect(SerializableRun run) => Keys.Mark(RunStorage.Key(run));
    public static void Mark()
    {
        if (!LanSession.Active || !RunManager.Instance.IsInProgress || !Trusted) return;
        Trusted = false;
        try
        {
            if (SaveLibrary.ActiveEntry is { } entry)
                Protect(SaveLibrary.Decode(SaveLibrary.Catalog.ReadPayload(entry)));
        }
        catch (IOException ex) { GD.PushError("[STS2 LAN Co-op] 失步标记未能落盘，当前会话仍停止归档覆盖；请保留有效存档。" + ex.Message); }
        catch (UnauthorizedAccessException ex) { GD.PushError("[STS2 LAN Co-op] 无权写入失步标记，当前会话仍停止归档覆盖；请保留有效存档。" + ex.Message); }
        LanStateTrace.Record("checkpoint-protected", new { HasCheckpoint = SaveLibrary.ActiveEntry != null });
        LanStateTrace.Flush();
        GD.PushWarning("[STS2 LAN Co-op] 已检测到局内失步，保留最近有效归档，停止覆盖续玩检查点。");
    }
}

[HarmonyPatch(typeof(ChecksumTracker), "CompareChecksums")]
internal static class ProtectDivergedHost
{
    private static void Prefix(object localChecksum, NetChecksumData remoteChecksum)
    {
        if (!LanSession.Active) return;
        var local = (NetChecksumData)AccessTools.Field(localChecksum.GetType(), "data").GetValue(localChecksum)!;
        if (local.id == remoteChecksum.id && local.checksum != remoteChecksum.checksum) RunStateTrust.Mark();
    }
}
[HarmonyPatch(typeof(ChecksumTracker), "OnReceivedStateDivergenceMessage")]
internal static class ProtectDivergedPeer
{
    private static void Prefix() => RunStateTrust.Mark();
}
