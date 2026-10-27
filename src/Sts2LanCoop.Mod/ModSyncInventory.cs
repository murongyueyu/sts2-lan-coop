using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using Sts2LanCoop.Core;
namespace Sts2LanCoop.Mod;

internal static class ModSyncInventory
{
    public static string Storage => LanCapability.Android
        ? Godot.ProjectSettings.GlobalizePath("user://sts2_lan_coop/modsync") : Path.Combine(GameRoot,".sts2-lan-modsync");
    public static string GameRoot => LanCapability.Windows ? Path.GetDirectoryName(System.Environment.ProcessPath)!
        : throw new InvalidOperationException("Android同步使用独立缓存，不使用PC游戏目录或PowerShell应用器。");
    public static string Cache => Path.Combine(Storage,"cache");
    public static string MobileImports => Path.Combine(Storage,"import");
    private static readonly Dictionary<string,string> Startup = new(StringComparer.OrdinalIgnoreCase);
    private static Task<List<SyncMod>>? _startup;
    public static string LastEnvironment {get;private set;}="";
    public static List<SyncMod> LastProfile {get;private set;}=[];
    public static void CaptureStartup() { _startup ??= Read(); }
    public static async Task<List<SyncMod>> Read(SyncProfile? mobileHost = null)
    {
        var mobileRequired=mobileHost!=null ? ModSyncRules.Required(mobileHost) : null;
        var mods = ModManager.Mods.Where(m=>m.manifest?.id!=null).Select(m=>new
        {
            Assemblies=GameBuild.Assemblies(m).ToArray(), Data=new SyncMod
            {
                Id=m.manifest!.id!, Name=m.manifest.name??m.manifest.id!,Version=m.manifest.version??"",
                Gameplay=m.manifest.affectsGameplay, Loaded=m.state==ModLoadState.Loaded, Source=(int)m.modSource,
                Directory=Path.GetFullPath(m.path), Dependencies=(m.manifest.dependencies??[])
                    .Select(d=>new SyncDependency(d.id,d.minVersion?.ToString()??"")).ToList()
            }
        }).ToArray();
        var inventory=await Task.Run(()=>mods.Select(m=>
        {
            var data=m.Data;
            try
            {
                var runtimeFiles=LanCapability.Android ? MobileModFiles.RuntimeFiles(data.Directory,data.Id) : null;
                var target=LanCapability.Android && mobileRequired?.Contains(data.Id)==true ? mobileHost!.Mods.Single(h=>h.Id.Equals(data.Id,StringComparison.OrdinalIgnoreCase)) : null;
                var inspection=target!=null ? MobileModFiles.Inspect(target,data.Directory,Cache) : default;
                var files=LanCapability.Android ? inspection.Files??runtimeFiles! : ModSyncFiles.Scan(data.Directory);
                if(data.Id==ModSyncRules.CoreId) files=files.Where(f=>f.Name==data.Id+".dll"||f.Name==data.Id+".json").ToList();
                foreach(var assembly in m.Assemblies.Where(a=>!a.IsDynamic))
                {
                    using var stream=File.OpenRead(assembly.Location);using var pe=new PEReader(stream);var reader=pe.GetMetadataReader();
                    if(reader.GetGuid(reader.GetModuleDefinition().Mvid)!=assembly.ManifestModule.ModuleVersionId)
                        throw new IOException("已加载 DLL 与磁盘版本不同，请重启游戏。");
                }
                var fingerprint=ModSyncRules.Fingerprint(files);
                var startupFingerprint=runtimeFiles!=null ? ModSyncRules.Fingerprint(runtimeFiles) : fingerprint;
                lock(Startup)
                {
                    var key=data.Directory+"|"+data.Id;
                    if(data.Loaded && Startup.TryGetValue(key,out var before) && before!=startupFingerprint)throw new IOException("游戏启动后文件发生变化，请重启。");
                    if(data.Loaded)Startup.TryAdd(key,startupFingerprint);
                }
                var parts=data.Directory.Replace('\\','/').Split('/');ulong workshop=0;
                for(var i=0;i<parts.Length-1;i++)if(parts[i]=="2868840")ulong.TryParse(parts[i+1],out workshop);
                return data with {Files=files,Fingerprint=fingerprint,Workshop=workshop,FileNotes=inspection.Notes??[],RuntimeFingerprint=startupFingerprint};
            }
            catch(Exception ex){return data with {Unavailable=ex.Message};}
        }).ToList());
        var snapshotRequired=ModSyncRules.Required(new SyncProfile{Mods=inventory.Where(m=>m.Loaded).ToList()});
        if(inventory.Where(m=>m.Loaded&&snapshotRequired.Contains(m.Id)).All(m=>m.Unavailable.Length==0))
        {
            // Save/reload identity must not depend on whether a host profile was
            // supplied, or whether a cached manifest was normalized for comparison.
            var snapshot=LanCapability.Android ? inventory.Select(m=>m with{Fingerprint=m.RuntimeFingerprint}).ToList() : inventory;
            LastEnvironment=EnvironmentHash(snapshot);
            LastProfile=snapshot.Where(m=>m.Loaded).Select(m=>m with{Files=[],Directory="",Manifest=null,FileNotes=[]}).ToList();
        }
        return inventory;
    }
    public static string EnvironmentHash(IEnumerable<SyncMod> mods,bool includeCore=false)
    {
        var profile=new SyncProfile{Mods=mods.Where(m=>m.Loaded).ToList()};var required=ModSyncRules.Required(profile);
        return ModSyncRules.Digest(JsonSerializer.SerializeToUtf8Bytes(profile.Mods.Where(m=>required.Contains(m.Id)&&(includeCore||m.Id!=ModSyncRules.CoreId))
            .OrderBy(m=>m.Id,StringComparer.Ordinal).Select(m=>new{m.Id,m.Version,m.Fingerprint})));
    }
    public static async Task<SyncProfile> HostProfile()
    {
        if(_startup!=null)await _startup;
        var mods=(await Read()).Where(m=>m.Loaded).ToList();
        var profile=new SyncProfile{Game=GameBuild.Version,Session=AdmissionTransport.Session,Mods=mods};var required=ModSyncRules.Required(profile);
        foreach(var mod in mods.Where(m=>required.Contains(m.Id)&&m.Unavailable.Length>0))throw new IOException(mod.Name+"："+mod.Unavailable);
        var published=new List<SyncMod>();
        foreach(var inventoryMod in mods)
        {
            var mod=inventoryMod;
            if(required.Contains(mod.Id))
                mod=mod with{Manifest=await Task.Run(()=>MobileModFiles.ReadHostManifest(mod))};
            if(!required.Contains(mod.Id)||mod.Id==ModSyncRules.CoreId||!LanSession.Settings.ShareMods)
            {published.Add(mod with{Unavailable=mod.Id==ModSyncRules.CoreId?"核心需手动统一版本":"主机未同意共享文件"});continue;}
            try
            {
                var units=ModSyncFiles.ReadManifests(mod.Directory);
                if(units.Count!=1||units[0].Id!=mod.Id||mod.Directory.Equals(Path.Combine(GameRoot,"mods"),StringComparison.OrdinalIgnoreCase))
                    throw new IOException("模组须放在独立目录，共享目录含多个模组时需手动整理。");
                var package=await Task.Run(()=>ModSyncFiles.MakePackage(mod.Directory,mod.Files,Cache));
                published.Add(mod with{Bundle=Path.GetFileNameWithoutExtension(package),BundleSize=new FileInfo(package).Length});
            }
            catch(Exception ex){published.Add(mod with{Unavailable=ex.Message});}
        }
        return profile with {Mods=published,Revision=EnvironmentHash(mods,true)};
    }
}
