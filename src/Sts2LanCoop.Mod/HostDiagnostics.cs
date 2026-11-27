using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;

namespace Sts2LanCoop.Mod;

// 手机主机收包诊断：独立于大厅的逐帧回调，区分“没有调用收包”与“套接字没有收到数据”。只写日志，不改变网络行为。
internal static class HostDiagnostics
{
    private static ENetHost? _host;
    private static ulong _next;
    private static int _reports;

    public static void Tick()
    {
        if (!LanCapability.Android) return;
        try
        {
            if (LanSession.HostedService?.NetHost is not ENetHost host) { _host = null; return; }
            var now = Time.GetTicksMsec();
            var started = !ReferenceEquals(host, _host);
            if (started)
            {
                _host = host; _reports = 0; _next = now + 5000;
                AdmissionTransport.Updates = AdmissionTransport.ConnectEvents = 0;
                AdmissionTransport.DisconnectEvents = AdmissionTransport.ReceiveEvents = 0;
            }
            else if (now < _next) return;
            // 开房后前2分钟每5秒一次，之后每30秒一次，避免日志无限增长。
            _next = now + (++_reports < 24 ? 5000UL : 30000UL);
            var connection = AccessTools.Field(typeof(ENetHost), "_connection")?.GetValue(host) as ENetConnection;
            var socket = connection == null ? "连接对象缺失" :
                $"本地端口{connection.GetLocalPort()}，套接字收{connection.PopStatistic(ENetConnection.HostStatistic.ReceivedPackets)}包/" +
                $"{connection.PopStatistic(ENetConnection.HostStatistic.ReceivedData)}字节，发{connection.PopStatistic(ENetConnection.HostStatistic.SentPackets)}包";
            GD.Print($"[STS2 LAN Diag] {(started ? "房间已建立" : "主机状态")}：设定端口{LanSession.Settings.HostPort}，{socket}；" +
                $"已调用收包{AdmissionTransport.Updates}次，事件 连接{AdmissionTransport.ConnectEvents}/数据{AdmissionTransport.ReceiveEvents}/断开{AdmissionTransport.DisconnectEvents}；" +
                $"在线{host.ConnectedPeerIds.Count()}，IsConnected={host.IsConnected}，前台={LanMobileLifecycle.Foreground}");
        }
        catch (Exception ex)
        {
            if (_reports++ < 3) GD.PushWarning("[STS2 LAN Diag] 诊断读取失败：" + ex);
        }
    }
}
