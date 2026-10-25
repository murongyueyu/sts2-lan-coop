using Godot;
using Steamworks;
using Sts2LanCoop.Core;
namespace Sts2LanCoop.Mod;

internal static class ModSyncSteam
{
    public static bool Available
    {
        get
        {
            try { return Probe(); }
            catch(InvalidOperationException){return false;}
            catch(DllNotFoundException){return false;}
            catch(TypeInitializationException){return false;}
            catch(FileNotFoundException){return false;}
            catch(FileLoadException){return false;}
            catch(TypeLoadException){return false;}
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool Probe()=>SteamAPI.IsSteamRunning() && SteamUtils.GetAppID().m_AppId==2868840 && SteamUser.BLoggedOn() && SteamApps.BIsSubscribed();
    public static async Task<string> Download(SceneTree tree,SyncMod mod,Action<string> progress,CancellationToken cancel)
    {
        if(!Available||mod.Workshop==0)throw new IOException(mod.Name+"没有可用工坊来源，请选择主机传输。");
        var id=new PublishedFileId_t(mod.Workshop);
        SteamUGC.SubscribeItem(id);
        if(!SteamUGC.DownloadItem(id,true))throw new IOException("工坊拒绝下载，请改选主机传输。");
        var until=Time.GetTicksMsec()+10*60*1000;
        while(true)
        {
            cancel.ThrowIfCancellationRequested();
            var state=(EItemState)SteamUGC.GetItemState(id);
            SteamUGC.GetItemDownloadInfo(id,out var got,out var size);
            progress($"工坊 {mod.Name}：{got/1024}/{size/1024} KiB（取消不会退订）");
            if(state.HasFlag(EItemState.k_EItemStateInstalled)&&!state.HasFlag(EItemState.k_EItemStateNeedsUpdate)&&!state.HasFlag(EItemState.k_EItemStateDownloading)&&!state.HasFlag(EItemState.k_EItemStateDownloadPending))break;
            if(Time.GetTicksMsec()>until)throw new TimeoutException("工坊下载超时，可改选主机来源。");
            await tree.ToSignal(tree.CreateTimer(.25),SceneTreeTimer.SignalName.Timeout);
        }
        if(!SteamUGC.GetItemInstallInfo(id,out _,out var folder,4096,out _))throw new IOException("工坊安装目录不可用。");
        return await Task.Run(()=>
        {
            // Workshop wrappers may contain several game-version builds. Locate one exact unit.
            var candidates=Directory.EnumerateFiles(folder,mod.Id+".json",SearchOption.AllDirectories).Select(Path.GetDirectoryName).OfType<string>().Distinct().ToArray();
            foreach(var directory in candidates)
            {
                var manifests=ModSyncFiles.ReadManifests(directory);
                if(manifests.Count!=1||manifests[0].Id!=mod.Id||manifests[0].Version!=mod.Version)continue;
                var files=ModSyncFiles.Scan(directory);
                if(ModSyncRules.Fingerprint(files)!=mod.Fingerprint)continue;
                return ModSyncFiles.MakePackage(directory,files,ModSyncInventory.Cache);
            }
            throw new IOException("工坊提供的版本或文件与主机不同；未安装。请改选主机来源获取准确版本（支持旧版）。");
        });
    }
}
