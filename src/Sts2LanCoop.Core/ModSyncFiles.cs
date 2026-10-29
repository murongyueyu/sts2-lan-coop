using System.IO.Compression;
using System.Text.Json;

namespace Sts2LanCoop.Core;

public static class ModSyncFiles
{
    public static string UnitFolder(string id)
    {
        Relative(id);
        if (id.Contains('/')) throw new InvalidDataException("模组ID不能包含路径。");
        return id;
    }
    public const long MaxPackageSize=4L*1024*1024*1024;
    private static readonly string[] ForbiddenNames=["sts2.dll","godotsharp.dll","0harmony.dll","steam_api64.dll","steam_api.dll","settings.save","current_run_mp.save"];
    private static readonly string[] ForbiddenExtensions=[".exe",".com",".cmd",".bat",".ps1",".vbs",".lnk",".url",".reg",".log",".save",".pdb",".user",".key",".pem"];
    private static void PrivatePath(string path)
    {
        var parts=path.Split('/');
        if(parts.Any(p=>new[]{".git",".svn",".hg",".ssh",".aws",".codex","steam_settings","credentials.json","tokens.json"}.Contains(p,StringComparer.OrdinalIgnoreCase)||p.Equals(".env",StringComparison.OrdinalIgnoreCase)||p.StartsWith(".env.",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("模组目录包含仓库、凭据或账号配置，不能自动共享。");
    }
    public static string Relative(string path)
    {
        var parts=path.Split('/');
        if(path.Length is 0 or > 240 || path.Contains('\\') || parts.Any(p=>string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c=>c<32 || "<>:\"|?*".Contains(c))))
            throw new InvalidDataException("模组包路径无效或越界。");
        foreach(var part in parts)
        {
            var stem=part.Split('.')[0].ToUpperInvariant();
            if(stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length==4 && (stem.StartsWith("COM")||stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')
                throw new InvalidDataException("模组包包含Windows保留文件名。");
        }
        return path;
    }
    public static string Child(string root,string relative)
    {
        Relative(relative);
        var full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("路径越界。");
        return full;
    }
    public static void NoLinks(string root)
    {
        if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("拒绝链接目录或文件。");
        if(Directory.Exists(root))foreach(var child in Directory.EnumerateFileSystemEntries(root))NoLinks(child);
    }
    public static List<SyncFile> Scan(string directory)
    {
        NoLinks(directory);
        var files=new List<SyncFile>(); long total=0;
        foreach(var file in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative=Path.GetRelativePath(directory,file).Replace('\\','/'); Relative(relative);
            PrivatePath(relative);
            if(ForbiddenExtensions.Contains(Path.GetExtension(file),StringComparer.OrdinalIgnoreCase))continue;
            if(ForbiddenNames.Contains(Path.GetFileName(file),StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("模组目录混有游戏/账号文件，不能自动提供。");
            var length=new FileInfo(file).Length;total+=length;
            if(total>MaxPackageSize || files.Count>=30000)throw new InvalidDataException("模组包过大或文件太多。");
            files.Add(new(relative,length,ModSyncRules.FileHash(file)));
        }
        if(files.Select(f=>f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count)throw new InvalidDataException("模组包含同名冲突文件。");
        return files;
    }
    public static string MakePackage(string directory,IReadOnlyList<SyncFile> files,string cache)
    {
        Directory.CreateDirectory(cache);
        var temp=Path.Combine(cache,Guid.NewGuid().ToString("N")+".partial");
        try
        {
            using(var zip=ZipFile.Open(temp,ZipArchiveMode.Create))
                foreach(var f in files)
                {
                    var source=Child(directory,f.Name);
                    if(new FileInfo(source).Length!=f.Size || ModSyncRules.FileHash(source)!=f.Hash)throw new IOException("主机文件已变化，请重启并重新建房。");
                    zip.CreateEntryFromFile(source,f.Name,CompressionLevel.Fastest);
                }
            var hash=ModSyncRules.FileHash(temp);
            var target=Path.Combine(cache,hash+".zip");
            if(!File.Exists(target))File.Move(temp,target);
            return target;
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    public static void VerifyPackage(string archive,IReadOnlyList<SyncFile> files)
    {
        if(files.Count is 0 or >30000 || files.Sum(f=>f.Size)>MaxPackageSize || files.Any(f=>f.Size<0))throw new InvalidDataException("无效的模组文件清单。");
        var expected=files.ToDictionary(f=>Relative(f.Name),StringComparer.OrdinalIgnoreCase);
        using var zip=ZipFile.OpenRead(archive);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            Relative(entry.FullName);
            PrivatePath(entry.FullName);
            if(ForbiddenNames.Contains(Path.GetFileName(entry.FullName),StringComparer.OrdinalIgnoreCase)||ForbiddenExtensions.Contains(Path.GetExtension(entry.FullName),StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("模组包包含禁止分发的文件。");
            if(((entry.ExternalAttributes>>16)&0xF000)==0xA000 || !seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName,out var file) || entry.Length!=file.Size)
                throw new InvalidDataException("包包含未声明文件、链接、重复文件或大小错误。");
            using var stream=entry.Open();
            using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer=new byte[65536];long count=0;int read;
            while((read=stream.Read(buffer))>0)
            {count+=read;if(count>file.Size)throw new InvalidDataException("文件解压超过声明大小。");hash.AppendData(buffer,0,read);}
            if(count!=file.Size||Convert.ToHexString(hash.GetHashAndReset())!=file.Hash)throw new InvalidDataException("模组文件校验失败。");
        }
        if(seen.Count!=expected.Count)throw new InvalidDataException("模组包不完整。");
    }
    public static void VerifyMod(string archive,SyncMod mod)
    {
        VerifyPackage(archive,mod.Files);
        if(FingerprintMismatch())throw new InvalidDataException("文件清单与模组指纹不符。");
        bool FingerprintMismatch()=>ModSyncRules.Fingerprint(mod.Files)!=mod.Fingerprint;
        using var zip=ZipFile.OpenRead(archive);var manifests=0;
        foreach(var entry in zip.Entries.Where(e=>e.FullName.EndsWith(".json",StringComparison.OrdinalIgnoreCase)))
        {
            if(entry.Length>2*1024*1024)throw new InvalidDataException("JSON文件过大，无法安全检查。");
            JsonDocument doc;
            try {using var stream=entry.Open();doc=JsonDocument.Parse(stream);}
            catch(JsonException){continue;}
            using(doc)
            {
                var root=doc.RootElement;
                if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("id",out var id)||!root.TryGetProperty("has_dll",out _)&&!root.TryGetProperty("has_pck",out _))continue;
                manifests++;
                if(id.GetString()!=mod.Id||mod.Id==ModSyncRules.CoreId||entry.FullName.Contains('/')||root.GetProperty("version").GetString()!=mod.Version)
                    throw new InvalidDataException("包包含其他模组、核心或不匹配版本。");
                if(root.TryGetProperty("has_dll",out var dll)&&dll.GetBoolean()&&!mod.Files.Any(f=>f.Name==mod.Id+".dll") ||
                   root.TryGetProperty("has_pck",out var pck)&&pck.GetBoolean()&&!mod.Files.Any(f=>f.Name==mod.Id+".pck"))
                    throw new InvalidDataException("包缺少清单声明的DLL或PCK。");
            }
        }
        if(manifests!=1)throw new InvalidDataException("一个安装单元必须且只能包含一个模组清单。");
    }
    public static List<(string Id,string Version,bool Gameplay,List<SyncDependency> Dependencies)> ReadManifests(string root)
    {
        var result=new List<(string,string,bool,List<SyncDependency>)>();
        foreach(var file in Directory.EnumerateFiles(root,"*.json",SearchOption.AllDirectories))
        {
            JsonDocument document;
            try{document=ModManifestJson.Parse(File.ReadAllBytes(file));}catch(JsonException){continue;}
            using var doc=document;var json=doc.RootElement;
            if(json.ValueKind!=JsonValueKind.Object || !json.TryGetProperty("id",out var id) || id.ValueKind!=JsonValueKind.String)continue;
            if(!json.TryGetProperty("has_dll",out _) && !json.TryGetProperty("has_pck",out _))continue;
            var deps=new List<SyncDependency>();
            if(json.TryGetProperty("dependencies",out var array)) foreach(var dep in array.EnumerateArray())
                deps.Add(dep.ValueKind==JsonValueKind.String ? new(dep.GetString()!,"") : new(dep.GetProperty("id").GetString()!,dep.TryGetProperty("min_version",out var min)?min.GetString()??"":""));
            result.Add((id.GetString()!,json.TryGetProperty("version",out var v)?v.GetString()??"":"",!json.TryGetProperty("affects_gameplay",out var g)||g.GetBoolean(),deps));
        }
        return result;
    }
}
