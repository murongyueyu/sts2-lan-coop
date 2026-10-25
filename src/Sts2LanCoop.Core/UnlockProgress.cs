using System.Text.Json.Nodes;

namespace Sts2LanCoop.Core;

// Only monotonic, explicitly allowed base-game unlock fields cross the modded boundary.
public sealed record UnlockAllowlist(HashSet<string> Epochs,HashSet<string> Characters,Dictionary<string,HashSet<string>> Discoveries);
public sealed record UnlockMerge(JsonObject Vanilla,JsonObject Modded);
public static class UnlockProgress
{
    public const int Schema=21;
    private static readonly string[] States=["none","no_slot","not_obtained","obtained_no_slot","obtained","revealed"];
    public static JsonObject Read(byte[] bytes)
    {
        if(bytes.Length is 0 or >16*1024*1024)throw new InvalidDataException("进度文件为空或过大，未同步。");
        var value=JsonNode.Parse(bytes) as JsonObject??throw new InvalidDataException("进度文件不是JSON对象。");
        if(value["schema_version"]?.GetValue<int>()!=Schema)throw new InvalidDataException("共用解锁仅支持v0.107.1的schema 21；请先由对应游戏迁移存档。");
        return value;
    }
    public static UnlockMerge Merge(JsonObject vanilla,JsonObject modded,UnlockAllowlist allow)
    {
        // Validate both directions before touching a file. Preserve each side's unrelated/unknown fields.
        var v=(JsonObject)vanilla.DeepClone();var m=(JsonObject)modded.DeepClone();
        Apply(v,modded,allow);Apply(m,vanilla,allow);return new(v,m);
    }
    private static JsonArray Array(JsonObject root,string name)
    {
        if(root[name]==null)return [];
        if(root[name] is not JsonArray array||array.Count>30000)throw new InvalidDataException("进度列表无效："+name);
        return array;
    }
    private static int Number(JsonObject root,string key,int max)
    {
        var value=root[key]?.GetValue<int>()??0;
        if(value<0||value>max)throw new InvalidDataException("进度数值超出当前游戏范围："+key);
        return value;
    }
    private static Dictionary<string,JsonObject> Records(JsonObject root,string name)
    {
        var result=new Dictionary<string,JsonObject>(StringComparer.Ordinal);
        foreach(var item in Array(root,name))
        {
            if(item is not JsonObject value||value["id"]?.GetValue<string>() is not {Length:>0 and <=160} id||!result.TryAdd(id,value))
                throw new InvalidDataException("进度记录无效或ID重复："+name);
        }
        return result;
    }
    private static int Rank(JsonObject epoch)
    {
        var node=epoch["state"]??throw new InvalidDataException("时间线状态缺失。");
        var value=node.ToJsonString();
        if(int.TryParse(value,out var number)&&number>=0&&number<States.Length)return number;
        var rank=System.Array.IndexOf(States,node.GetValue<string>());
        if(rank<0)throw new InvalidDataException("未知时间线状态。");return rank;
    }
    private static void Apply(JsonObject dest,JsonObject source,UnlockAllowlist allow)
    {
        if(Number(dest,"schema_version",Schema)!=Schema||Number(source,"schema_version",Schema)!=Schema)throw new InvalidDataException("进度格式版本不兼容。");
        var epochs=Records(dest,"epochs");var incoming=Records(source,"epochs");
        dest["epochs"]??=new JsonArray();
        foreach(var (id,epoch) in incoming.Where(p=>allow.Epochs.Contains(p.Key)))
        {
            var rank=Rank(epoch);
            var time=epoch["obtain_date"]?.GetValue<long>()??0;
            if(time<0)throw new InvalidDataException("时间线获取时间无效。");
            if(!epochs.TryGetValue(id,out var existing)) {((JsonArray)dest["epochs"]!).Add(epoch.DeepClone());continue;}
            if(rank>Rank(existing))existing["state"]=epoch["state"]!.DeepClone();
            var old=existing["obtain_date"]?.GetValue<long>()??0;
            if(old<0)throw new InvalidDataException("时间线获取时间无效。");
            if(Rank(existing)>=3&&time>0&&(old==0||time<old))existing["obtain_date"]=time;
        }
        var characters=Records(dest,"character_stats");dest["character_stats"]??=new JsonArray();
        foreach(var (id,stats) in Records(source,"character_stats").Where(p=>allow.Characters.Contains(p.Key)))
        {
            var max=Number(stats,"max_ascension",10);
            if(characters.TryGetValue(id,out var existing)) existing["max_ascension"]=Math.Max(max,Number(existing,"max_ascension",10));
            else ((JsonArray)dest["character_stats"]!).Add(new JsonObject{["id"]=id,["max_ascension"]=max});
        }
        dest["max_multiplayer_ascension"]=Math.Max(Number(dest,"max_multiplayer_ascension",10),Number(source,"max_multiplayer_ascension",10));
        foreach(var (field,ids) in allow.Discoveries)
        {
            var current=Array(dest,field);var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in current)seen.Add(item?.GetValue<string>()??throw new InvalidDataException("图鉴记录为空。"));
            if(dest[field]==null)dest[field]=current;
            foreach(var item in Array(source,field))
            {
                var id=item?.GetValue<string>()??throw new InvalidDataException("图鉴记录为空。");
                if(ids.Contains(id)&&seen.Add(id))current.Add(id);
            }
        }
    }
}
