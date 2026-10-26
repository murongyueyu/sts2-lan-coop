using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2LanCoop.Core;
namespace Sts2LanCoop.Mod;

internal static class ModSyncWorkflow
{
    private static bool _pendingShown;
    public static async Task<bool> Check(Node parent,Endpoint endpoint,Action<string> progress,CancellationToken cancel)
    {
        if(LanCapability.ModSyncBlockReason is {} blocked)throw new InvalidOperationException(blocked);
        if(RunManager.Instance.IsInProgress||LanSession.Active)
            throw new InvalidOperationException("请先退出当前对局/房间，在多人菜单同步。");
        progress("正在读取主机模组与本机文件……");
        using var client=new ModSyncTransport.Client(parent.GetTree(),endpoint);
        var profile=await client.Profile(cancel);
        var local=await ModSyncInventory.Read();cancel.ThrowIfCancellationRequested();
        var comparison=ModSyncRules.Compare(profile,local,GameBuild.Version);
        if(comparison.Errors.Count>0)throw new InvalidOperationException(string.Join("\n",comparison.Errors));
        if(comparison.Matches){progress("模组与主机一致，兼容的个人美化已保留。");return true;}
        var changes=string.Join("\n",comparison.Changes.Select(c=>$"{ActionName(c.Action)} {c.Name} [{c.Id}] {c.Target?.Version??c.Local?.Version}：{c.Reason}"));
        if(comparison.Changes.All(c=>c.Action is "enable" or "disable"))
        {
            var statePlan=await Prepare(profile,comparison,local,[],endpoint);
            var title=comparison.Changes.All(c=>c.Action=="enable")?"是否启用以下已安装模组并重启？":"是否调整模组启用状态并重启？";
            if(await Confirm(parent,title,changes+"\n\n文件版本与主机一致，无需下载。确认后退出游戏、备份原配置、调整启用状态并重启；取消则保持原状。",cancel))
            {Launch(statePlan);parent.GetTree().Quit();}
            else progress("已取消启用调整，原模组配置未改动。");
            return false;
        }
        var steam=ModSyncSteam.Available;
        var source=await Choose(parent,changes,steam,cancel);
        if(source<0){progress("已取消同步，原模组配置未改动。");return false;}
        var packages=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var change in comparison.Changes.Where(c=>c.Action is "add" or "replace"))
        {
            cancel.ThrowIfCancellationRequested();
            packages[change.Id]=source==1
                ?await ModSyncSteam.Download(parent.GetTree(),change.Target!,progress,cancel)
                :await client.Download(profile,change.Target!,progress,cancel);
        }
        // Re-query before committing a consented snapshot, including host room identity.
        var fresh=await client.Profile(cancel);
        if(fresh.Session!=profile.Session||fresh.Revision!=profile.Revision)throw new IOException("主机房间或模组已变化，请重新检查。");
        var plan=await Prepare(profile,comparison,local,packages,endpoint);
        progress("文件已全部校验，尚未应用。确认后将退出、备份、应用并重启。");
        if(await Confirm(parent,"退出并应用模组",changes+"\n\n将备份原文件和启用状态；不删除多余模组、不退订工坊。仅接受可信主机的模组代码。\n应用后重启，在多人面板复核并重连。",cancel))
        {
            Launch(plan);parent.GetTree().Quit();
        }
        else progress("下载已缓存，未应用。再次检查可复用缓存。事务："+plan.Id);
        return false;
    }
    private static string ActionName(string action)=>action switch{"add"=>"新增","replace"=>"替换","enable"=>"启用","disable"=>"禁用",_=>action};
    internal static async Task<SyncApplyPlan> Prepare(SyncProfile profile,SyncComparison comparison,List<SyncMod> local,
        Dictionary<string,string> packages,Endpoint endpoint)
    {
        if(comparison.Errors.Count>0)throw new InvalidOperationException("同步计划有错误。");
        var installs=new List<SyncInstall>();var settings=new List<SyncSetting>();
        foreach(var change in comparison.Changes)
        {
            if(change.Id==ModSyncRules.CoreId)throw new InvalidOperationException("不能更换LAN核心。");
            if(change.Action=="disable") {settings.Add(new(change.Id,change.Local!.Source,false));continue;}
            if(change.Action=="enable")
            {
                settings.Add(new(change.Id,change.Local!.Source,true));
                if(change.Local.Source==2)settings.Add(new(change.Id,1,false));
                continue;
            }
            var target=change.Target!;var archive=packages[change.Id];
            await Task.Run(()=>ModSyncFiles.VerifyMod(archive,target));
            var backups=new List<string>();
            var backupFiles=new Dictionary<string,List<SyncFile>>(StringComparer.OrdinalIgnoreCase);
            foreach(var copy in local.Where(m=>m.Id.Equals(change.Id,StringComparison.OrdinalIgnoreCase)&&m.Source==1))
            {
                var mods=Path.Combine(ModSyncInventory.GameRoot,"mods");
                var relative=Path.GetRelativePath(mods,copy.Directory).Replace('\\','/');ModSyncFiles.Relative(relative);
                if(ModSyncFiles.ReadManifests(copy.Directory).Count!=1||local.Any(m=>m.Id!=copy.Id&&m.Directory.StartsWith(copy.Directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("待替换目录包含其他模组，须先手动整理："+copy.Directory);
                backups.Add(ModSyncFiles.Child(mods,relative));
                backupFiles[ModSyncFiles.Child(mods,relative)]=copy.Files;
            }
            var folder="lan-synced-"+ModSyncRules.Digest(System.Text.Encoding.UTF8.GetBytes(change.Id))[..16];
            var destination=ModSyncFiles.Child(Path.Combine(ModSyncInventory.GameRoot,"mods"),folder);
            if(Directory.Exists(destination)&&!backups.Contains(destination,StringComparer.OrdinalIgnoreCase))throw new IOException("安装目标已存在，请先检查："+destination);
            installs.Add(new(change.Id,archive,ModSyncRules.FileHash(archive),folder,backups,target.Files){BackupFiles=backupFiles});
            settings.Add(new(change.Id,1,true));settings.Add(new(change.Id,2,false));
        }
        SaveManager.Instance.SaveSettings();
        var manager=AccessTools.Field(typeof(SaveManager),"_settingsSaveManager").GetValue(SaveManager.Instance)!;
        var store=(ISaveStore)AccessTools.Field(manager.GetType(),"_saveStore").GetValue(manager)!;
        var settingsPath=ProjectSettings.GlobalizePath(store.GetFullPath("settings.save"));
        var process=Process.GetCurrentProcess();
        var plan=new SyncApplyPlan
        {
            GameRoot=ModSyncInventory.GameRoot,Storage=ModSyncInventory.Storage,Settings=settingsPath,
            SettingsHash=ModSyncRules.FileHash(settingsPath),Endpoint=endpoint.ToString(),ProfileRevision=profile.Revision,
            GamePid=process.Id,GameStartedUtcTicks=process.StartTime.ToUniversalTime().Ticks,Installs=installs,SettingsChanges=settings,
            SourceTokens=new(){["1"]=SourceToken(1),["2"]=SourceToken(2)}
        };
        var dir=Path.Combine(plan.Storage,"transactions",plan.Id);Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir,"profile.json"),JsonSerializer.SerializeToUtf8Bytes(profile));
        File.WriteAllBytes(Path.Combine(dir,"plan.json"),JsonSerializer.SerializeToUtf8Bytes(plan,new JsonSerializerOptions{WriteIndented=true}));
        return plan;
    }
    private static string SourceToken(int source)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new SettingsSaveMod{Id="serialization-probe",Source=(ModSource)source},JsonSerializationUtility.GetTypeInfo<SettingsSaveMod>());
        using var doc=JsonDocument.Parse(bytes);return doc.RootElement.GetProperty("source").GetRawText();
    }
    public static void Launch(SyncApplyPlan plan,bool restore=false)
    {
        if(LanSession.Active||RunManager.Instance.IsInProgress)throw new InvalidOperationException("当前仍有对局/房间，请先退出。");
        var mod=ModManager.GetLoadedMods().Single(m=>m.manifest?.id==ModSyncRules.CoreId);
        var script=Path.Combine(mod.path,"Apply-ModSync.ps1");
        if(!File.Exists(script))throw new FileNotFoundException("基础包缺少本地应用助手，请重新完整安装LAN基础包。",script);
        var start=new ProcessStartInfo(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"))
        {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=ModSyncInventory.GameRoot};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-Plan",Path.Combine(plan.Storage,"transactions",plan.Id,"plan.json")})start.ArgumentList.Add(arg);
        if(restore)start.ArgumentList.Add("-Restore");
        if(restore)
        {
            using var process=Process.GetCurrentProcess();
            plan=plan with{GamePid=process.Id,GameStartedUtcTicks=process.StartTime.ToUniversalTime().Ticks};
            File.WriteAllBytes(Path.Combine(plan.Storage,"transactions",plan.Id,"plan.json"),JsonSerializer.SerializeToUtf8Bytes(plan));
        }
        _=Process.Start(start)??throw new IOException("应用助手未能启动，游戏未退出。");
    }
    public static void ShowPending(Node parent,Action<string> address,Action<string> status)
    {
        if(_pendingShown||LanCapability.ModSyncBlockReason!=null)return;
        var file=Path.Combine(ModSyncInventory.Storage,"pending.json");
        if(!File.Exists(file))
        {
            var transactions=Path.Combine(ModSyncInventory.Storage,"transactions");
            var latest=Directory.Exists(transactions)?Directory.EnumerateFiles(transactions,"journal.json",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault():null;
            if(latest!=null)
            {
                using var journal=JsonDocument.Parse(File.ReadAllBytes(latest));
                if(journal.RootElement.GetProperty("State").GetString()=="failed")
                {status("上次同步未完成："+journal.RootElement.GetProperty("Error").GetString()+"。事务日志："+latest);_pendingShown=true;}
            }
            return;
        }
        _pendingShown=true;_ = VerifyPending();
        async Task VerifyPending()
        {
            try
            {
                var plan=JsonSerializer.Deserialize<SyncApplyPlan>(File.ReadAllBytes(file))!;
                var profile=JsonSerializer.Deserialize<SyncProfile>(File.ReadAllBytes(Path.Combine(plan.Storage,"transactions",plan.Id,"profile.json")))!;
                var comparison=ModSyncRules.Compare(profile,await ModSyncInventory.Read(),GameBuild.Version);
                address(plan.Endpoint);
                if(!comparison.Matches){status("重启后模组尚未对齐，请检查应用日志；未自动入房。");return;}
                status("重启后实际加载已复核。地址已填入；点击连接重新检查房间，继续存档时重新选择角色。");
                File.Move(file,Path.Combine(plan.Storage,"transactions",plan.Id,"verified.json"),true);
            }
            catch(Exception ex){status("同步复核失败："+ex.Message);}
        }
    }
    public static async Task RestoreLatest(Node parent,CancellationToken cancel)
    {
        if(LanCapability.ModSyncBlockReason is {} blocked)throw new InvalidOperationException(blocked);
        var root=Path.Combine(ModSyncInventory.Storage,"transactions");
        var entry=Directory.Exists(root)?Directory.EnumerateDirectories(root).Where(d=>
        {
            var file=Path.Combine(d,"journal.json");if(!File.Exists(file))return false;
            using var journal=JsonDocument.Parse(File.ReadAllBytes(file));
            var state=journal.RootElement.GetProperty("State").GetString();
            return state is "applied" or "applying" || state=="failed" &&
                (journal.RootElement.GetProperty("Moves").GetArrayLength()>0||journal.RootElement.GetProperty("Installed").GetArrayLength()>0||journal.RootElement.GetProperty("SettingsApplied").GetBoolean());
        }).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault():null;
        if(entry==null)throw new IOException("没有可恢复的同步事务。");
        var plan=JsonSerializer.Deserialize<SyncApplyPlan>(File.ReadAllBytes(Path.Combine(entry,"plan.json")))!;
        if(await Confirm(parent,"恢复联机前模组","将退出游戏并恢复最近一次同步的文件和设置。若同步后又手动改过相关文件，助手会拒绝覆盖；结果见事务日志。",cancel))
        {Launch(plan,true);parent.GetTree().Quit();}
    }
    private static async Task<int> Choose(Node parent,string changes,bool steam,CancellationToken cancel)
    {
        var dialog=Details("按主机版本同步模组",changes+"\n\n确认后才开始获取文件；之后另行确认退出应用。主机包仅从当前连接获取。");
        var sources=new OptionButton();sources.AddItem("从主机获取准确版本（无需Steam）",0);
        if(steam)sources.AddItem("从Steam创意工坊获取（仍需与主机文件一致）",1);
        dialog.GetNode<VBoxContainer>("Details").AddChild(sources);dialog.GetOkButton().Text="确认调整并获取";dialog.GetCancelButton().Text="取消";
        return await Dialog(parent,dialog,()=>sources.GetSelectedId(),cancel);
    }
    internal static async Task<bool> Confirm(Node parent,string title,string text,CancellationToken cancel,string okText="确认")
    {
        var dialog=Details(title,text);
        dialog.GetOkButton().Text=okText;dialog.GetCancelButton().Text="稍后";
        return await Dialog(parent,dialog,()=>1,cancel)==1;
    }
    private static ConfirmationDialog Details(string title,string text)
    {
        var dialog=new ConfirmationDialog{Title=title,MinSize=new Vector2I(700,300)};
        var body=new VBoxContainer{Name="Details"};dialog.AddChild(body);
        body.AddChild(new RichTextLabel{Text=text,BbcodeEnabled=false,SelectionEnabled=true,ScrollActive=true,SizeFlagsVertical=Control.SizeFlags.ExpandFill,CustomMinimumSize=new Vector2(680,300)});
        return dialog;
    }
    private static async Task<int> Dialog(Node parent,ConfirmationDialog dialog,Func<int> value,CancellationToken cancel)
    {
        var completion=new TaskCompletionSource<int>();parent.AddChild(dialog);
        dialog.Confirmed+=()=>completion.TrySetResult(value());dialog.Canceled+=()=>completion.TrySetResult(-1);
        dialog.TreeExiting+=()=>completion.TrySetResult(-1);dialog.PopupCentered(new Vector2I(900,500));
        try
        {
            while(!completion.Task.IsCompleted)
            {cancel.ThrowIfCancellationRequested();await parent.ToSignal(parent.GetTree(),SceneTree.SignalName.ProcessFrame);}
            return await completion.Task;
        }
        finally {if(GodotObject.IsInstanceValid(dialog))dialog.QueueFree();}
    }
}
