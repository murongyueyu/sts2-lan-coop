using System.Reflection;
using Godot;
using HarmonyLib;
using Sts2LanCoop.Core;

namespace Sts2LanCoop.Mod;

// The installed library owns its framing, validation and feature negotiation.
// LAN only preserves that framing across its own epoch and native-ID layer.
internal static class RitsuNetworkInterop
{
    private delegate bool MatchSidecar(ReadOnlySpan<byte> bytes);
    private static MatchSidecar? _matches;
    private static bool _initialized;

    public static void Ensure()
    {
        if (_initialized) return;
        Type? Find(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
        var wire = Find("STS2RitsuLib.Networking.Sidecar.RitsuLibSidecarWire");
        if (wire == null) { _initialized = true; return; }
        var harmony = new Harmony("sts2_lan_coop.ritsu-network");
        try
        {
            var match = wire.GetMethod("MatchesMagic", BindingFlags.Public | BindingFlags.Static,
                null, [typeof(ReadOnlySpan<byte>)], null)
                ?? throw new MissingMethodException("RitsuLib sidecar MatchesMagic");
            var evidence = Find("STS2RitsuLib.Networking.Sidecar.RitsuLibSidecarNativeTrailerEvidence")
                ?? throw new TypeLoadException("RitsuLib native trailer evidence");
            var append = evidence.GetMethod("TryAppendLocalTrailer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, [typeof(byte[]).MakeByRefType(), typeof(int).MakeByRefType()], null)
                ?? throw new MissingMethodException("RitsuLib native trailer append");
            if (match.ReturnType != typeof(bool) || append.ReturnType != typeof(bool))
                throw new NotSupportedException("RitsuLib transport interface changed.");
            _matches = match.CreateDelegate<MatchSidecar>();
            harmony.Patch(append, prefix: new HarmonyMethod(typeof(RitsuNetworkInterop), nameof(PreserveControl)) { priority = Priority.First });
            _initialized = true;
            GD.Print("[STS2 LAN Co-op] Ritsu网络适配已启用：保留专用控制包，扩展报文不转换原版编号，接收先解除LAN代次封装。");
        }
        catch (Exception ex)
        {
            _matches = null;
            harmony.UnpatchAll(harmony.Id);
            throw new InvalidOperationException("RitsuLib网络接口无法适配，已停止建房/加入以避免丢失扩展消息：" + ex.Message, ex);
        }
    }

    public static bool IsSidecar(byte[] bytes) => _matches?.Invoke(bytes) == true;

    private static bool PreserveControl(byte[] packetBytes, int length, ref bool __result)
    {
        if (!LanSession.Active || length < 4 || length > packetBytes.Length ||
            !(AdmissionWire.IsControl(packetBytes) || ReloadWire.Matches(packetBytes))) return true;
        __result = false;
        return false;
    }
}
