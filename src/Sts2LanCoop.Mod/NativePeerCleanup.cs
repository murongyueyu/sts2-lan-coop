using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;

namespace Sts2LanCoop.Mod;

// v0.107.1's ENetHost.Update ignores ENet disconnect events and can retain dead peers.
// Notify the existing service/lobby through its native disconnect API; no packet format changes.
[HarmonyPatch(typeof(ENetHost), nameof(ENetHost.Update))]
internal static class NativePeerCleanup
{
    private static readonly FieldInfo Connections = AccessTools.Field(typeof(ENetHost), "_connectedPeers")
        ?? throw new MissingFieldException("ENetHost._connectedPeers");
    private static readonly Type ConnectionType = typeof(ENetHost).GetNestedType("ClientConnection", BindingFlags.NonPublic)
        ?? throw new MissingMemberException("ENetHost.ClientConnection");
    private static readonly FieldInfo Peer = AccessTools.Field(ConnectionType, "peer")
        ?? throw new MissingFieldException("ClientConnection.peer");
    private static readonly FieldInfo Id = AccessTools.Field(ConnectionType, "netId")
        ?? throw new MissingFieldException("ClientConnection.netId");
    private static readonly MethodInfo RemoveDeadConnection = AccessTools.Method(typeof(ENetHost), "HandleClientDisconnection")
        ?? throw new MissingMethodException("ENetHost.HandleClientDisconnection");

    [HarmonyPrefix]
    private static void BeforeUpdate(ENetHost __instance) => Sweep(__instance);

    [HarmonyPostfix]
    private static void AfterUpdate(ENetHost __instance) => Sweep(__instance);

    private static void Sweep(ENetHost host)
    {
        if (!LanSession.Active || LanSession.HostedService?.NetHost != host || !host.IsConnected) return;
        var dead = new List<object>();
        foreach (var connection in (IEnumerable)Connections.GetValue(host)!)
        {
            var peer = (ENetPacketPeer)Peer.GetValue(connection)!;
            if (!GodotObject.IsInstanceValid(peer) || peer.GetState() is
                ENetPacketPeer.PeerState.Disconnected or ENetPacketPeer.PeerState.Zombie)
                dead.Add(connection);
        }
        // Disconnect invokes callbacks which mutate native lists; never do so inside enumeration.
        foreach (var connection in dead)
        {
            var id = (ulong)Id.GetValue(connection)!;
            GD.Print($"[STS2 LAN Co-op] Removing disconnected ENet peer {id}; releasing native lobby slot.");
            // Transport is already terminal. Only remove/notify; another native PeerDisconnectNow may target a freed ENet peer.
            RemoveDeadConnection.Invoke(host, [connection, NetError.Timeout, true]);
        }
    }
}
