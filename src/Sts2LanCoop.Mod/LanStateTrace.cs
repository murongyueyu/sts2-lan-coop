using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2LanCoop.Mod;

// Keep a bounded recent window; flush it before Ritsu builds its existing dual-peer bundle.
internal static class LanStateTrace
{
    private static string _session = "";
    private static int _records;
    private static readonly Queue<string> Recent = new();
    internal static string Digest(IEnumerable<string> values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))))[..16];
    internal static void Record(string stage, object data)
    {
        if (!LanSession.Active) return;
        if (_session != AdmissionTransport.Session) { _session = AdmissionTransport.Session; _records = 0; Recent.Clear(); }
        var record = JsonSerializer.Serialize(new { Stage = stage, Epoch = QuickReload.Epoch, Data = data });
        if (Recent.Count == 160) Recent.Dequeue();
        Recent.Enqueue(record);
        if (_records < 160) { GD.Print("[STS2 LAN State] " + record); _records++; }
    }
    internal static void Flush()
    {
        foreach (var record in Recent) GD.Print("[STS2 LAN State Recent] " + record);
    }
    internal static void Snapshot(RunManager manager, string stage)
    {
        if (!LanSession.Active || manager.DebugOnlyGetState() is not { } state) return;
        Record(stage, new { Acts = state.Acts.Select(a => a.Id.ToString()), Players = state.Players.Select((p, index) => new
        {
            Slot = index, Character = p.Character.Id.ToString(),
            Unlocks = Digest(p.UnlockState.ToSerializable().UnlockedEpochs.OrderBy(e => e, StringComparer.Ordinal)),
            Pool = Digest(p.Character.CardPool.GetUnlockedCards(p.UnlockState, p.RunState.CardMultiplayerConstraint).Select(c => c.Id.ToString()))
        }).ToArray() });
    }
}

[HarmonyPatch(typeof(RunManager), "GenerateRooms")]
internal static class TraceRoomInputs
{
    private static void Prefix(RunManager __instance) => LanStateTrace.Snapshot(__instance, "before-room-generation");
}

[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetDistinctForCombat))]
internal static class TraceCardGeneration
{
    internal sealed record Generation(Player Player, int Before, int Count, string Pool);
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(Player player, ref IEnumerable<CardModel> cards, Rng rng, out Generation? __state)
    {
        __state = null;
        if (!LanSession.Active) return;
        // The original samples the sequence once; reuse that same materialization instead of inspecting it twice.
        var snapshot = cards.ToArray(); cards = snapshot;
        __state = new(player, GameBuild.RngCounter(rng), snapshot.Length, LanStateTrace.Digest(snapshot.Select(c => c.Id.ToString())));
    }
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref IEnumerable<CardModel> __result, Rng rng, Generation? __state)
    {
        if (__state == null) return;
        var after = GameBuild.RngCounter(rng);
        __result = Observe(__result, __state, after);
    }
    private static IEnumerable<CardModel> Observe(IEnumerable<CardModel> source, Generation state, int after)
    {
        var ids = new List<string>();
        foreach (var card in source) { ids.Add(card.Id.ToString()); yield return card; }
        var players = state.Player.RunState.Players;
        LanStateTrace.Record("combat-card-generation", new { Slot = players.ToList().IndexOf(state.Player),
            state.Count, state.Pool, state.Before, After = after, Candidates = ids.Take(12).ToArray(), CandidateCount = ids.Count });
    }
}
