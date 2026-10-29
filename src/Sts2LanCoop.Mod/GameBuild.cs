using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
namespace Sts2LanCoop.Mod;

internal static class GameBuild
{
    private static readonly PropertyInfo? CounterProperty = AccessTools.Property(typeof(MegaCrit.Sts2.Core.Random.Rng), "Counter");
    private static readonly FieldInfo? CounterField = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Random.Rng), "_counter");
    public static int RngCounter(MegaCrit.Sts2.Core.Random.Rng rng) => (int)(CounterProperty?.GetValue(rng) ?? CounterField?.GetValue(rng)
        ?? throw new MissingMemberException("Rng counter"));
    public static IEnumerable<MegaCrit.Sts2.Core.Models.CharacterModel> StartCharacters(object lobby) =>
        ((IEnumerable)AccessTools.Property(lobby.GetType(), "Players").GetValue(lobby)!).Cast<object>().Select(p =>
            (MegaCrit.Sts2.Core.Models.CharacterModel)(AccessTools.Field(p.GetType(), "character")?.GetValue(p) ?? AccessTools.Property(p.GetType(), "character").GetValue(p))!);
    public static string Version=>"v"+(MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance.ReleaseInfo?.Version??"unknown").TrimStart('v');
    public static bool Supported=>Version is "v0.107.1" or "v0.111.0";
    private static object Construct(Type type)
    {
        var empty=type.GetConstructor(Type.EmptyTypes);if(empty!=null)return empty.Invoke(null);
        var ctor=type.GetConstructors().Single(c=>c.GetParameters() is [{ParameterType.Name:"PeerVersionInfo"}]);
        var info=ctor.GetParameters()[0].ParameterType.GetMethod("LocalDefault",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,null);
        return ctor.Invoke([info]);
    }
    public static NetHostGameService Host()=>(NetHostGameService)Construct(typeof(NetHostGameService));
    public static JoinFlow Join()
    {
        var empty=typeof(JoinFlow).GetConstructor(Type.EmptyTypes);
        return empty!=null?(JoinFlow)empty.Invoke(null):(JoinFlow)Activator.CreateInstance(typeof(JoinFlow),Construct(typeof(NetClientGameService)))!;
    }
    public static INetClientGameService Service(JoinFlow flow)=>(INetClientGameService)typeof(JoinFlow).GetProperty("NetService")!.GetValue(flow)!;
    public static IEnumerable<Assembly> Assemblies(MegaCrit.Sts2.Core.Modding.Mod mod)
    {
        if(AccessTools.Field(mod.GetType(),"assemblies")?.GetValue(mod) is IEnumerable<Assembly> list)return list;
        return AccessTools.Field(mod.GetType(),"assembly")?.GetValue(mod) is Assembly assembly?[assembly]:[];
    }
    public static HashSet<ulong> Connected(object lobby)=>((IEnumerable<ulong>)
        (AccessTools.Property(lobby.GetType(),"ConnectedPlayerIds")??AccessTools.Property(lobby.GetType(),"PlayerIds")??throw new MissingMemberException("Lobby player IDs")).GetValue(lobby)!).ToHashSet();
    public static List<ulong> StartIds(object lobby)=>((IEnumerable)AccessTools.Property(lobby.GetType(),"Players").GetValue(lobby)!).Cast<object>()
        .Select(p=>(ulong)(AccessTools.Field(p.GetType(),"id")?.GetValue(p)??AccessTools.Property(p.GetType(),"id").GetValue(p))!).ToList();
    public static void AddReloadPlayers(LoadRunLobby lobby,IEnumerable<ulong> ids)
    {
        if(AccessTools.Property(lobby.GetType(),"ConnectedPlayerIds")?.GetValue(lobby) is HashSet<ulong> old)
        {foreach(var id in ids)old.Add(id);return;}
        var players=(IList)AccessTools.Property(lobby.GetType(),"Players").GetValue(lobby)!;
        var type=players.GetType().GetGenericArguments()[0];
        foreach(var id in ids)
        {
            var player=Activator.CreateInstance(type)!;
            type.GetField("id")!.SetValue(player,id);type.GetField("isModded")!.SetValue(player,true);type.GetField("isReady")!.SetValue(player,true);players.Add(player);
        }
    }
    public static string ProgressPath(int profile)=>(string)Optional(typeof(ProgressSaveManager),null,"GetProgressPathForProfile",profile)!;
    public static string ScopedPath(string relative)=>(string)Optional(typeof(SaveManager),SaveManager.Instance,"GetProfileScopedPath",relative)!;
    private static object? Optional(Type type,object? target,string name,object first)
    {
        var method=type.GetMethods().Single(m=>m.Name==name);
        return method.Invoke(target,method.GetParameters().Select((p,i)=>i==0?first:p.DefaultValue).ToArray());
    }
    public static ILoadRunLobbyListener ReloadListener(Action<string> disconnect)
    {
        var result=DispatchProxy.Create(typeof(ILoadRunLobbyListener),typeof(AdaptiveLoadListener));
        ((AdaptiveLoadListener)result).Disconnected=disconnect;return (ILoadRunLobbyListener)result;
    }
}

public class AdaptiveLoadListener:DispatchProxy
{
    internal Action<string>? Disconnected;
    protected override object? Invoke(MethodInfo? targetMethod,object?[]? args)=>targetMethod?.Name switch
    {
        "PlayerConnected" or "PlayerReadyChanged"=>null,
        "ShouldAllowRunToBegin"=>Task.FromResult(false),
        "RemotePlayerDisconnected"=>Notify("重载时有玩家退出。"),
        "LocalPlayerDisconnected"=>Notify("主机连接已断开。"),
        "BeginRun"=>throw new InvalidOperationException("快速SL由加载屏障启动。"),
        _=>throw new MissingMethodException("未知读档大厅回调："+targetMethod?.Name)
    };
    private object? Notify(string reason){Disconnected?.Invoke(reason);return null;}
}
