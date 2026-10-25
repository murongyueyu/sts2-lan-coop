using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2LanCoop.Core;

public sealed record SyncDependency(string Id, string Minimum);
public sealed record SyncFile(string Name, long Size, string Hash);
public sealed record SyncMod
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public bool Gameplay { get; init; }
    public bool Loaded { get; init; }
    public int Source { get; init; }
    public ulong Workshop { get; init; }
    public string Fingerprint { get; init; } = "";
    public string Bundle { get; init; } = "";
    public long BundleSize { get; init; }
    public string Unavailable { get; init; } = "";
    public List<SyncDependency> Dependencies { get; init; } = [];
    public List<SyncFile> Files { get; init; } = [];
    [JsonIgnore] public string Directory { get; init; } = "";
}
public sealed record SyncProfile
{
    public int Schema { get; init; } = 1;
    public string Game { get; init; } = "v0.107.1";
    public string Platform { get; init; } = "Windows-x64";
    public string Session { get; init; } = "";
    public string Revision { get; init; } = "";
    public List<SyncMod> Mods { get; init; } = [];
}
public sealed record SyncChange(string Id, string Name, string Action, string Reason, SyncMod? Target, SyncMod? Local);
public sealed class SyncComparison
{
    public List<SyncChange> Changes { get; } = [];
    public List<string> Errors { get; } = [];
    public bool Matches => Changes.Count == 0 && Errors.Count == 0;
}
public static class ModSyncRules
{
    public const string CoreId = "sts2_lan_coop";
    public static readonly string[] ConflictingIds = ["QuickSL", "sts2_lan_connect", "LanSpire", "DirectConnectIP", "AutoModSubscriber"];
    public static HashSet<string> Required(SyncProfile host)
    {
        if (host.Schema != 1 || host.Game != "v0.107.1" || host.Platform != "Windows-x64" || host.Mods.Count > 1024 ||
            host.Mods.Any(m => string.IsNullOrWhiteSpace(m.Id) || m.Id.Length>160) || host.Mods.Select(m=>m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=host.Mods.Count)
            throw new InvalidDataException("主机模组清单格式、游戏或平台不受支持。");
        var required = host.Mods.Where(m=>m.Gameplay || m.Id==CoreId).Select(m=>m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!required.Contains(CoreId)) throw new InvalidDataException("主机清单缺少LAN核心。");
        var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string id)
        {
            if(done.Contains(id))return;
            if(!stack.Add(id)) throw new InvalidDataException("主机模组依赖存在环："+id);
            var mod=host.Mods.SingleOrDefault(m=>m.Id.Equals(id,StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("主机缺少依赖："+id);
            foreach(var dep in mod.Dependencies)
            {
                required.Add(dep.Id); Visit(dep.Id);
                var dependency=host.Mods.Single(m=>m.Id.Equals(dep.Id,StringComparison.OrdinalIgnoreCase));
                if(!AtLeast(dependency.Version,dep.Minimum))throw new InvalidDataException("主机依赖版本不满足要求："+dep.Id);
            }
            stack.Remove(id);done.Add(id);
        }
        foreach(var id in required.ToArray())Visit(id);
        return required;
    }
    public static SyncComparison Compare(SyncProfile host, IReadOnlyList<SyncMod> local)
    {
        var result=new SyncComparison();
        var required=Required(host);
        foreach(var duplicate in local.Where(m=>m.Loaded).GroupBy(m=>m.Id,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1))
            result.Errors.Add("本机重复加载相同模组ID，请先整理："+duplicate.Key);
        foreach(var id in required)
        {
            var target=host.Mods.Single(m=>m.Id.Equals(id,StringComparison.OrdinalIgnoreCase));
            var copies=local.Where(m=>m.Id.Equals(id,StringComparison.OrdinalIgnoreCase)).ToArray();
            var current=copies.FirstOrDefault(m=>m.Loaded) ?? copies.FirstOrDefault();
            if(current?.Loaded==true && current.Version==target.Version && current.Fingerprint==target.Fingerprint && !string.IsNullOrEmpty(target.Fingerprint)) continue;
            var matching=copies.FirstOrDefault(m=>m.Version==target.Version && m.Fingerprint==target.Fingerprint && !string.IsNullOrEmpty(target.Fingerprint));
            var action=matching!=null ? "enable" : current==null ? "add" : "replace";
            var reason=current==null ? "本机未安装" : matching!=null ? "正确版本尚未加载" : current.Version!=target.Version ? $"{current.Version} → {target.Version}（按主机版本，可能包含降级）" : "版本相同但关键文件不同";
            if(id==CoreId) result.Errors.Add("LAN核心版本/文件不同，请先更新同版基础包，不能在同步中替换核心。");
            else result.Changes.Add(new(id,target.Name,action,reason,target,matching??current));
        }
        var disabled=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var mod in local.Where(m=>m.Loaded && !required.Contains(m.Id)))
            if(mod.Gameplay || ConflictingIds.Contains(mod.Id,StringComparer.OrdinalIgnoreCase)) disabled.Add(mod.Id);
        // A retained cosmetic may depend on a library that is being replaced with an incompatible version.
        bool again;
        do
        {
            again=false;
            foreach(var mod in local.Where(m=>m.Loaded && !required.Contains(m.Id) && !disabled.Contains(m.Id)))
                if(mod.Dependencies.Any(d=>disabled.Contains(d.Id) ||
                    required.Contains(d.Id) && !AtLeast(host.Mods.Single(m=>m.Id.Equals(d.Id,StringComparison.OrdinalIgnoreCase)).Version,d.Minimum)))
                    again|=disabled.Add(mod.Id);
        }while(again);
        foreach(var mod in local.Where(m=>m.Loaded && disabled.Contains(m.Id)))
            result.Changes.Add(new(mod.Id,mod.Name,"disable",mod.Gameplay ? "主机不需要的玩法模组" : "网络功能或依赖冲突",null,mod));
        return result;
    }
    private static bool AtLeast(string version,string minimum)
    {
        if(string.IsNullOrWhiteSpace(minimum))return true;
        if(version==minimum)return true;
        var a=version.Split('+')[0].Split('-',2);var b=minimum.Split('+')[0].Split('-',2);
        if(!Version.TryParse(a[0],out var actual)||!Version.TryParse(b[0],out var needed))return false;
        var left=new Version(actual.Major,actual.Minor,Math.Max(0,actual.Build),Math.Max(0,actual.Revision));
        var right=new Version(needed.Major,needed.Minor,Math.Max(0,needed.Build),Math.Max(0,needed.Revision));
        var compare=left.CompareTo(right);if(compare!=0)return compare>0;
        if(a.Length==1)return true;if(b.Length==1)return false;
        var ap=a[1].Split('.');var bp=b[1].Split('.');
        for(var i=0;i<Math.Min(ap.Length,bp.Length);i++)
        {
            var an=long.TryParse(ap[i],out var av);var bn=long.TryParse(bp[i],out var bv);
            compare=an&&bn?av.CompareTo(bv):an!=bn?(an?-1:1):StringComparer.Ordinal.Compare(ap[i],bp[i]);
            if(compare!=0)return compare>0;
        }
        return ap.Length>=bp.Length;
    }
    public static string Digest(byte[] data)=>Convert.ToHexString(SHA256.HashData(data));
    public static string FileHash(string file) { using var stream=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(stream)); }
    public static string Fingerprint(IEnumerable<SyncFile> files)=>Digest(Encoding.UTF8.GetBytes(string.Join("\n",files.OrderBy(f=>f.Name,StringComparer.Ordinal).Select(f=>$"{f.Name}\t{f.Size}\t{f.Hash}"))));
}

public sealed record SyncSetting(string Id,int Source,bool Enabled);
public sealed record SyncInstall(string Id,string Archive,string ArchiveHash,string Target,List<string> BackupDirectories,List<SyncFile> Files)
{
    public Dictionary<string,List<SyncFile>> BackupFiles {get;init;}=[];
}
public sealed record SyncApplyPlan
{
    public int Schema { get; init; }=1;
    public string Id { get; init; }=Guid.NewGuid().ToString("N");
    public string GameRoot { get; init; }="";
    public string Settings { get; init; }="";
    public string SettingsHash { get; init; }="";
    public string Storage { get; init; }="";
    public string Endpoint { get; init; }="";
    public string ProfileRevision { get; init; }="";
    public int GamePid { get; init; }
    public long GameStartedUtcTicks { get; init; }
    public List<SyncInstall> Installs { get; init; }=[];
    public List<SyncSetting> SettingsChanges { get; init; }=[];
    public Dictionary<string,string> SourceTokens { get; init; }=[];
}
