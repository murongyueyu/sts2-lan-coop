using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Validation;
using MegaCrit.Sts2.Core.Timeline;
using Sts2LanCoop.Core;
namespace Sts2LanCoop.Mod;

internal static class SharedUnlocks
{
    private static readonly System.Reflection.FieldInfo StoreField=AccessTools.Field(typeof(ProgressSaveManager),"_saveStore");
    private static bool _busy;
    private static int _loading;
    public static string Status {get;private set;}="共用解锁未开启。";
    public static string BackupRoot=>ProjectSettings.GlobalizePath("user://sts2_lan_coop/progress_backups");
    private static ProgressSaveManager Manager=>(ProgressSaveManager)AccessTools.Field(typeof(SaveManager),"_progressSaveManager").GetValue(SaveManager.Instance)!;
    private static string Current=>ProgressSaveManager.GetProgressPathForProfile(SaveManager.Instance.CurrentProfileId);
    private static string Vanilla=>Path.Combine("profile"+SaveManager.Instance.CurrentProfileId,"saves","progress.save");
    private static ISaveStore Store(ProgressSaveManager manager)=>(ISaveStore)StoreField.GetValue(manager)!;
    private static string Full(ISaveStore store,string relative)=>ProjectSettings.GlobalizePath(store.GetFullPath(relative));
    private static UnlockAllowlist Allowed()
    {
        var assembly=typeof(ModelDb).Assembly;
        HashSet<string> Ids(IEnumerable<AbstractModel> models)=>models.Where(m=>m.GetType().Assembly==assembly).Select(m=>m.Id.ToString()).ToHashSet(StringComparer.Ordinal);
        return new(EpochModel.AllEpochIds.Where(id=>EpochModel.Get(id).GetType().Assembly==assembly).ToHashSet(StringComparer.Ordinal),Ids(ModelDb.AllCharacters),new()
        {
            ["discovered_cards"]=Ids(ModelDb.AllCards),["discovered_relics"]=Ids(ModelDb.AllRelics),
            ["discovered_potions"]=Ids(ModelDb.AllPotions),["discovered_events"]=Ids(ModelDb.AllEvents),["discovered_acts"]=Ids(ModelDb.Acts)
        });
    }
    private static string Backup(ISaveStore store,string relative,byte[] bytes)
    {
        var account=ModSyncRules.Digest(Encoding.UTF8.GetBytes(Full(store,"")))[..16];
        var root=Path.Combine(BackupRoot,account,"profile"+SaveManager.Instance.CurrentProfileId);Directory.CreateDirectory(root);
        var file=Path.Combine(root,ModSyncRules.Digest(bytes)+".save");
        if(!File.Exists(file))File.WriteAllBytes(file,bytes);return file;
    }
    public static void BeforeLoad(ProgressSaveManager manager)
    {
        _loading++;
        try
        {
            if(!LanSession.Settings.ShareUnlockProgress||!UserDataPathProvider.IsRunningModded||!ReferenceEquals(manager,Manager))return;
            var store=Store(manager);var file=Full(store,Current);
            if(File.Exists(file))Backup(store,Current,File.ReadAllBytes(file));
        }
        catch(Exception ex){Failure(ex);}
    }
    public static void AfterLoad(ProgressSaveManager manager,Exception? error)
    {
        _loading=Math.Max(0,_loading-1);
        if(error==null&&_loading==0)Automatic(manager,true);
    }
    public static void AfterSave(ProgressSaveManager manager)
    {if(_loading==0)Automatic(manager,false);}
    private static void Automatic(ProgressSaveManager manager,bool import)
    {
        if(_busy)return;
        try
        {
            if(!LanSession.Settings.ShareUnlockProgress||!UserDataPathProvider.IsRunningModded||!ReferenceEquals(manager,Manager))return;
            Sync(manager,import);
        }
        catch(Exception ex){Failure(ex);}
    }
    private static void Failure(Exception ex)
    {Status="解锁同步未完成："+ex.Message;GD.PushError("[STS2 LAN Co-op] "+Status);}
    public static void SyncNow()
    {
        if(LanSession.Active||RunManager.Instance.IsInProgress||SaveManager.Instance.CurrentRunSaveTask is {IsCompleted:false})
            throw new InvalidOperationException("请先保存退出对局并离开房间，再合并解锁进度。");
        Sync(Manager,true);
    }
    public static void Configure(Node parent,Action<string> report)
    {
        var enabled=LanSession.Settings.ShareUnlockProgress;
        var dialog=new ConfirmationDialog
        {
            Title="原版 / 模组共用解锁进度",
            DialogText=$"当前：{(enabled?"已开启":"未开启")}，仅处理当前账号的同编号存档栏。\n\n"+
                "合并原版角色、时间线、单人/多人进阶等级及图鉴发现记录，已有解锁取并集。\n"+
                "单人/多人进行中对局、战绩统计、成就、偏好和模组专属内容继续分开。\n"+
                "关闭其他游戏实例后操作。首次与每次有变更的写入均保留原文件备份。\n"+
                "开启后在加载模组进度时合并，保存模组进度时导出原版解锁；下次启动原版生效。\n\n备份目录："+BackupRoot,
            MinSize=new Vector2I(700,350)
        };
        parent.AddChild(dialog);dialog.GetOkButton().Text=enabled?"立即合并一次":"备份并开启";dialog.GetCancelButton().Text="取消";
        if(enabled)dialog.AddButton("关闭自动共用",false,"disable");
        dialog.Confirmed+=()=>
        {
            try{SyncNow();LanSession.Settings.ShareUnlockProgress=true;LanSession.Save();report(Status);}
            catch(Exception ex){Failure(ex);report(Status);}
            finally{dialog.QueueFree();}
        };
        dialog.CustomAction+=action=>
        {
            if(action!="disable")return;
            try{LanSession.Settings.ShareUnlockProgress=false;LanSession.Save();Status="自动共用已关闭，已合并的解锁不会撤销。";report(Status);}
            catch(Exception ex){report(ex.Message);}finally{dialog.QueueFree();}
        };
        dialog.Canceled+=dialog.QueueFree;dialog.PopupCentered(new Vector2I(960,480));
    }
    private static void Sync(ProgressSaveManager manager,bool import)
    {
        if(_busy)return;
        if(!UserDataPathProvider.IsRunningModded)throw new InvalidOperationException("当前不是有模组的存档环境。");
        if(SaveManager.Instance.GetLatestSchemaVersion<SerializableProgress>()!=UnlockProgress.Schema)throw new InvalidOperationException("当前游戏进度格式未适配。");
        using(var currentProcess=Process.GetCurrentProcess())
        {
            var peers=Process.GetProcessesByName(currentProcess.ProcessName);
            try{if(peers.Any(p=>p.Id!=currentProcess.Id))throw new IOException("检测到其他游戏实例，请关闭后再同步解锁进度。");}
            finally{foreach(var peer in peers)peer.Dispose();}
        }
        _busy=true;
        try
        {
            var store=Store(manager);var modPath=Full(store,Current);var vanillaPath=Full(store,Vanilla);
            if(modPath.Equals(vanillaPath,StringComparison.OrdinalIgnoreCase))throw new IOException("原版和模组路径相同，拒绝同步。");
            if(!File.Exists(modPath))throw new IOException("当前模组进度尚未保存，请重启到主菜单后重试。");
            var modBytes=File.ReadAllBytes(modPath);var mod=UnlockProgress.Read(modBytes);
            if(!import)
            {
                var saved=manager.Progress.ToSerializable();saved.SchemaVersion=UnlockProgress.Schema;
                if(!JsonNode.DeepEquals(mod,JsonNode.Parse(JsonSerializationUtility.ToJson(saved))))
                    throw new IOException("本次原版进度保存尚未成功读回，未导出解锁。");
            }
            // A new vanilla profile starts with native defaults, never the modded save's statistics or IDs.
            byte[]? vanillaBytes=File.Exists(vanillaPath)?File.ReadAllBytes(vanillaPath):null;
            if(vanillaBytes==null&&store.FileExists(Vanilla))throw new IOException("原版进度尚未在本地就绪，请先完成云存档同步。");
            var vanilla=vanillaBytes==null?UnlockProgress.Read(JsonSerializer.SerializeToUtf8Bytes(DefaultProgress(),JsonSerializationUtility.GetTypeInfo<SerializableProgress>())):UnlockProgress.Read(vanillaBytes);
            var result=UnlockProgress.Merge(vanilla,mod,Allowed());
            ProgressState? updated=null;
            if(import)
            {
                var data=JsonSerializer.Deserialize(result.Modded.ToJsonString(),JsonSerializationUtility.GetTypeInfo<SerializableProgress>())??throw new IOException("合并结果无法读取。");
                var original=JsonSerializer.Deserialize(mod.ToJsonString(),JsonSerializationUtility.GetTypeInfo<SerializableProgress>())??throw new IOException("原进度无法读取。");
                var beforeContext=new DeserializationContext();_ = ProgressState.FromSerializable(original,beforeContext);
                var context=new DeserializationContext();updated=ProgressState.FromSerializable(data,context);
                if(context.Errors.Count>beforeContext.Errors.Count)throw new IOException("合并后出现新的原版校验问题；原文件未同步。");
            }
            var changes=new List<(string Relative,string Full,byte[]? Before,JsonObject After)>();
            if(!JsonNode.DeepEquals(vanilla,result.Vanilla)||vanillaBytes==null)changes.Add((Vanilla,vanillaPath,vanillaBytes,result.Vanilla));
            if(import&&!JsonNode.DeepEquals(mod,result.Modded))changes.Add((Current,modPath,modBytes,result.Modded));
            if(changes.Count>0)
            {
                var journal=new List<object>();
                foreach(var change in changes)
                    journal.Add(new{change.Relative,Before=change.Before==null?"absent":ModSyncRules.Digest(change.Before),Backup=change.Before==null?"":Backup(store,change.Relative,change.Before),After=ModSyncRules.Digest(Encoding.UTF8.GetBytes(change.After.ToJsonString()))});
                Directory.CreateDirectory(BackupRoot);
                File.WriteAllText(Path.Combine(BackupRoot,DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff")+"-"+Guid.NewGuid().ToString("N")+".json"),JsonSerializer.Serialize(journal));
                foreach(var change in changes)
                {
                    if(change.Before==null?File.Exists(change.Full):!File.Exists(change.Full)||ModSyncRules.FileHash(change.Full)!=ModSyncRules.Digest(change.Before))
                        throw new IOException("进度在合并期间被其他进程修改，已停止；请关闭其他游戏实例后重试。");
                    store.CreateDirectory(Path.GetDirectoryName(change.Relative)!);
                    ModSyncFiles.NoLinks(Path.GetDirectoryName(change.Full)!);
                    store.WriteFile(change.Relative,change.After.ToJsonString());
                    if(!JsonNode.DeepEquals(UnlockProgress.Read(File.ReadAllBytes(change.Full)),change.After))throw new IOException("进度写入读回不一致；备份已保留。");
                }
            }
            if(updated!=null)manager.Progress=updated;
            Status="原版与模组版解锁进度已合并；对局、统计和成就保持各自独立。";
        }
        finally{_busy=false;}
        SerializableProgress DefaultProgress(){var data=ProgressState.CreateDefault().ToSerializable();data.SchemaVersion=UnlockProgress.Schema;return data;}
    }
}

[HarmonyPatch(typeof(ProgressSaveManager),nameof(ProgressSaveManager.LoadProgress))]
internal static class SharedUnlockLoad
{
    private static void Prefix(ProgressSaveManager __instance)=>SharedUnlocks.BeforeLoad(__instance);
    private static Exception? Finalizer(ProgressSaveManager __instance,Exception? __exception)
    {SharedUnlocks.AfterLoad(__instance,__exception);return __exception;}
}
[HarmonyPatch(typeof(ProgressSaveManager),nameof(ProgressSaveManager.SaveProgress))]
internal static class SharedUnlockSave
{private static void Postfix(ProgressSaveManager __instance)=>SharedUnlocks.AfterSave(__instance);}
