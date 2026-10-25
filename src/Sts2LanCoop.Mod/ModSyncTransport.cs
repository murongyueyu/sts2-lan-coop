using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using Sts2LanCoop.Core;
namespace Sts2LanCoop.Mod;

internal sealed record SyncMessage
{
    public string Operation {get;init;}="";
    public string Revision {get;init;}="";
    public string Hash {get;init;}="";
    public long Offset {get;init;}
    public long Size {get;init;}
    public byte[] Data {get;init;}=[];
    public string Error {get;init;}="";
}
internal static class ModSyncTransport
{
    private static readonly byte[] Magic=[0xF3,0x53,0x4D,1];
    private static Task<SyncProfile>? _profile;
    private static byte[]? _profileBytes;
    public static SyncProfile? Published=>_profile is {IsCompletedSuccessfully:true}?_profile.Result:null;
    public static void Reset(){_profile=null;_profileBytes=null;}
    public static void Warm(){_profile??=ModSyncInventory.HostProfile();}
    public static bool IsControl(byte[] bytes)=>bytes.Length>=4&&bytes.AsSpan(0,4).SequenceEqual(Magic);
    private static byte[] Encode(SyncMessage msg)=>[..Magic,..JsonSerializer.SerializeToUtf8Bytes(msg)];
    private static SyncMessage Decode(byte[] bytes)
    {
        if(!IsControl(bytes)||bytes.Length>96*1024)throw new InvalidDataException("同步报文不受支持或过大。");
        return JsonSerializer.Deserialize<SyncMessage>(bytes.AsSpan(4))??throw new InvalidDataException("同步报文为空。");
    }
    public static void Serve(ENetPacketPeer peer,byte[] bytes)
    {
        SyncMessage response;
        try
        {
            var request=Decode(bytes);_profile??=ModSyncInventory.HostProfile();
            if(!_profile.IsCompleted)response=new(){Operation="pending"};
            else
            {
                var profile=_profile.GetAwaiter().GetResult();
                if(request.Operation=="info")
                {
                    _profileBytes??=JsonSerializer.SerializeToUtf8Bytes(profile);
                    if(_profileBytes.Length>8*1024*1024)throw new IOException("主机资料过大。");
                    response=new(){Revision=profile.Revision,Size=_profileBytes.Length,Hash=ModSyncRules.Digest(_profileBytes)};
                }
                else
                {
                    if(request.Revision!=profile.Revision)throw new IOException("主机清单已变化，请重新检查。");
                    if(request.Offset<0)throw new InvalidDataException("下载偏移无效。");
                    if(request.Operation=="profile")
                    {
                        var body=_profileBytes??=JsonSerializer.SerializeToUtf8Bytes(profile);
                        if(request.Offset>=body.Length)throw new InvalidDataException("资料偏移越界。");
                        response=new(){Offset=request.Offset,Data=body.AsSpan((int)request.Offset,Math.Min(48*1024,body.Length-(int)request.Offset)).ToArray()};
                    }
                    else if(request.Operation=="chunk")
                    {
                        if(!LanSession.Settings.ShareMods)throw new IOException("主机已关闭文件共享。");
                        var mod=profile.Mods.FirstOrDefault(m=>m.Bundle.Length==64&&m.Bundle==request.Hash)??throw new IOException("文件未共享。");
                        if(request.Offset>=mod.BundleSize)throw new InvalidDataException("文件偏移越界。");
                        using var file=File.OpenRead(Path.Combine(ModSyncInventory.Cache,mod.Bundle+".zip"));file.Position=request.Offset;
                        var data=new byte[(int)Math.Min(48*1024,mod.BundleSize-request.Offset)];file.ReadExactly(data);
                        response=new(){Offset=request.Offset,Data=data};
                    }
                    else throw new InvalidDataException("未知同步请求。");
                }
            }
        }
        catch(Exception ex){response=new(){Error=ex.GetBaseException().Message};}
        peer.Send(0,Encode(response),1);
    }
    internal sealed class Client:IDisposable
    {
        private readonly ENetConnection _connection=new();
        private readonly SceneTree _tree;
        private ENetPacketPeer? _peer;
        private bool _connected;
        public Client(SceneTree tree,Endpoint endpoint)
        {
            _tree=tree;
            if(_connection.CreateHost()!=Error.Ok)throw new IOException("不能创建同步连接。");
            _peer=_connection.ConnectToHost(endpoint.Host,endpoint.Port)??throw new IOException("不能连接主机。");
        }
        public async Task<SyncMessage> Request(SyncMessage request,CancellationToken cancel)
        {
            var deadline=Time.GetTicksMsec()+20000;var sent=false;
            while(Time.GetTicksMsec()<deadline)
            {
                cancel.ThrowIfCancellationRequested();
                if(_connected&&!sent){_peer!.Send(0,Encode(request),1);sent=true;}
                for(var i=0;i<128&&_connection.TryService(out var data);i++)
                {
                    var ev=data!.Value;
                    if(ev.type==ENetConnection.EventType.Connect)_connected=true;
                    if(ev.type==ENetConnection.EventType.Disconnect)throw new IOException("主机断开了同步连接。");
                    if(ev.type==ENetConnection.EventType.Receive)
                    {
                        var response=Decode(ev.packetData);
                        if(response.Error.Length>0)throw new IOException(response.Error);
                        return response;
                    }
                }
                await _tree.ToSignal(_tree,SceneTree.SignalName.ProcessFrame);
            }
            throw new TimeoutException("同步超时，请检查UDP地址和隧道。下载缓存保留，可重试。");
        }
        public async Task<SyncProfile> Profile(CancellationToken cancel)
        {
            var until=Time.GetTicksMsec()+120000;SyncMessage info;
            do
            {
                info=await Request(new(){Operation="info"},cancel);
                if(info.Operation!="pending")break;
                if(Time.GetTicksMsec()>until)throw new TimeoutException("主机整理模组超时。");
                await _tree.ToSignal(_tree.CreateTimer(.25),SceneTreeTimer.SignalName.Timeout);
            }while(true);
            if(info.Size is <=0 or >8*1024*1024)throw new IOException("清单大小无效。");
            using var data=new MemoryStream();
            while(data.Length<info.Size)
            {
                var chunk=await Request(new(){Operation="profile",Revision=info.Revision,Offset=data.Length},cancel);
                if(chunk.Offset!=data.Length||chunk.Data.Length==0||data.Length+chunk.Data.Length>info.Size)throw new IOException("清单分片无效。");
                data.Write(chunk.Data);
            }
            if(ModSyncRules.Digest(data.ToArray())!=info.Hash)throw new IOException("清单校验失败。");
            var profile=JsonSerializer.Deserialize<SyncProfile>(data.ToArray())??throw new IOException("清单为空。");
            ModSyncRules.Required(profile);
            if(profile.Revision!=info.Revision)throw new IOException("主机清单已变化。");
            return profile;
        }
        public async Task<string> Download(SyncProfile profile,SyncMod mod,Action<string> progress,CancellationToken cancel)
        {
            if(mod.Bundle.Length!=64||!mod.Bundle.All(char.IsAsciiHexDigit)||mod.BundleSize is <=0 or >ModSyncFiles.MaxPackageSize)
                throw new IOException(mod.Name+"："+mod.Unavailable+"；可改选工坊或请主机开启共享。");
            Directory.CreateDirectory(ModSyncInventory.Cache);var target=Path.Combine(ModSyncInventory.Cache,mod.Bundle+".zip");
            if(File.Exists(target))
            {
                if(await Task.Run(()=>ModSyncRules.FileHash(target))!=mod.Bundle)throw new IOException("缓存校验失败，请清除损坏文件："+target);
                await Task.Run(()=>ModSyncFiles.VerifyMod(target,mod));return target;
            }
            var partial=target+".partial";
            using(var file=new FileStream(partial,FileMode.OpenOrCreate,System.IO.FileAccess.Write,FileShare.None))
            {
                if(file.Length>mod.BundleSize)throw new IOException("缓存大小异常。");file.Position=file.Length;
                while(file.Length<mod.BundleSize)
                {
                    var response=await Request(new(){Operation="chunk",Revision=profile.Revision,Hash=mod.Bundle,Offset=file.Length},cancel);
                    if(response.Offset!=file.Length||response.Data.Length==0||file.Length+response.Data.Length>mod.BundleSize)throw new IOException("下载分片无效。");
                    file.Write(response.Data);progress($"{mod.Name}：{file.Length*100/mod.BundleSize}%（{file.Length/1024}/{mod.BundleSize/1024} KiB）");
                }
                file.Flush(true);
            }
            if(await Task.Run(()=>ModSyncRules.FileHash(partial))!=mod.Bundle){File.Delete(partial);throw new IOException("下载校验失败，损坏分片已清除，可重试。");}
            await Task.Run(()=>ModSyncFiles.VerifyMod(partial,mod));File.Move(partial,target);return target;
        }
        public void Dispose(){_peer?.PeerDisconnectNow();_peer=null;_connection.Destroy();_connection.Dispose();}
    }
}
