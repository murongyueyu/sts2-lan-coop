using System.Text.Json;
using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using Sts2LanCoop.Core;
namespace Sts2LanCoop.Mod;

internal static class ModSyncTransport
{
    private static Task<SyncProfile>? _profile;
    private static byte[]? _profileBytes;
    public static SyncProfile? Published=>_profile is {IsCompletedSuccessfully:true}?_profile.Result:null;
    public static void Reset(){_profile=null;_profileBytes=null;}
    public static void Warm(){_profile??=ModSyncInventory.HostProfile();}
    public static bool IsControl(byte[] bytes)=>SyncTransferWire.IsControl(bytes);
    private static byte[] Encode(SyncMessage msg)=>SyncTransferWire.Encode(msg);
    private static SyncMessage Decode(byte[] bytes)=>SyncTransferWire.Decode(bytes);
    public static async Task<SyncProfile> ReadProfile(SceneTree tree,Endpoint endpoint,CancellationToken cancel)
    {
        using var client=new Client(tree,endpoint);
        return await client.Profile(cancel);
    }
    public static void Serve(ENetPacketPeer peer,byte[] bytes)
    {
        SyncMessage response;
        var request=Decode(bytes);
        try
        {
            _profile??=ModSyncInventory.HostProfile();
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
                        response=new(){Offset=request.Offset,Data=body.AsSpan((int)request.Offset,Math.Min(SyncTransferWire.ChunkSize,body.Length-(int)request.Offset)).ToArray()};
                    }
                    else if(request.Operation=="chunk")
                    {
                        if(!LanSession.Settings.ShareMods)throw new IOException("主机已关闭文件共享。");
                        var mod=profile.Mods.FirstOrDefault(m=>m.Bundle.Length==64&&m.Bundle==request.Hash)??throw new IOException("文件未共享。");
                        if(request.Offset>=mod.BundleSize)throw new InvalidDataException("文件偏移越界。");
                        using var file=File.OpenRead(Path.Combine(ModSyncInventory.Cache,mod.Bundle+".zip"));file.Position=request.Offset;
                        var data=new byte[(int)Math.Min(SyncTransferWire.ChunkSize,mod.BundleSize-request.Offset)];file.ReadExactly(data);
                        response=new(){Offset=request.Offset,Data=data};
                    }
                    else throw new InvalidDataException("未知同步请求。");
                }
            }
        }
        catch(Exception ex){var error=ex.GetBaseException().Message;response=new(){Error=error[..Math.Min(error.Length,512)]};}
        response=response with{RequestId=request.RequestId,Operation=response.Operation.Length>0?response.Operation:request.Operation,
            Revision=response.Revision.Length>0?response.Revision:request.Revision,Hash=response.Hash.Length>0?response.Hash:request.Hash};
        peer.Send(0,Encode(response),1);
    }
    internal sealed class Client:IDisposable
    {
        private readonly ENetConnection _connection=new();
        private readonly SceneTree _tree;
        private ENetPacketPeer? _peer;
        private bool _connected;
        private bool _busy;
        private bool _disposed;
        private int _nextId;
        private int _discardThrough;
        public Client(SceneTree tree,Endpoint endpoint)
        {
            _tree=tree;
            if(_connection.CreateHost()!=Error.Ok)throw new IOException("不能创建同步连接。");
            _peer=_connection.ConnectToHost(endpoint.Host,endpoint.Port)??throw new IOException("不能连接主机。");
        }
        public async Task<SyncMessage> Request(SyncMessage request,CancellationToken cancel)
        {
            Enter();
            try
            {
                cancel.ThrowIfCancellationRequested();
                request=request with{RequestId=checked(++_nextId)};
                var deadline=Time.GetTicksMsec()+20000;var sent=false;
                while(Time.GetTicksMsec()<deadline)
                {
                    cancel.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed,this);
                    if(_connected&&!sent){_peer!.Send(0,Encode(request),1);sent=true;}
                    foreach(var response in Poll())
                    {
                        CheckResponse(response,request);
                        return response;
                    }
                    await _tree.ToSignal(_tree,SceneTree.SignalName.ProcessFrame);
                }
                throw new TimeoutException($"同步请求超时（{request.Operation}，{(_connected?"已连接":"握手未完成")}，{(sent?"已发请求":"未发请求")}）。下载缓存保留，可重试；请保留当前阶段和主机日志。");
            }
            finally { _discardThrough=_nextId; _busy=false; }
        }
        private void Enter()
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_busy)throw new InvalidOperationException("同一同步连接正在执行操作，请等待完成或取消。");
            _busy=true;
        }
        private IEnumerable<SyncMessage> Poll()
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            for(var i=0;i<128&&_connection.TryService(out var data);i++)
            {
                var ev=data!.Value;
                if(ev.type==ENetConnection.EventType.Connect)_connected=true;
                if(ev.type==ENetConnection.EventType.Disconnect)throw new IOException("房主断开了同步连接，请确认双方使用0.8.0-alpha.4或更新的同版Mod。");
                if(ev.type==ENetConnection.EventType.Receive)
                {
                    var response=Decode(ev.packetData);
                    if(response.RequestId<=_discardThrough)continue; // Late replies of an already canceled/completed operation.
                    yield return response;
                }
            }
        }
        private static void CheckResponse(SyncMessage response,SyncMessage request)
        {
            if(response.RequestId!=request.RequestId)throw new IOException("同步响应与请求编号不匹配。");
            if(response.Error.Length>0)throw new IOException(response.Error);
            if(response.Operation=="pending")return;
            if(response.Operation!=request.Operation || request.Revision.Length>0&&response.Revision!=request.Revision ||
                request.Hash.Length>0&&response.Hash!=request.Hash)
                throw new IOException("同步响应的操作、清单或文件摘要不匹配。");
        }
        private async Task DownloadWindow(FileStream file,SyncProfile profile,SyncMod mod,Action<string> progress,CancellationToken cancel)
        {
            Enter();
            var window=new SyncDownloadWindow(mod.BundleSize,file.Length);
            var pending=new Dictionary<int,SyncMessage>();
            var watch=Stopwatch.StartNew();var started=file.Length;
            var idleDeadline=Time.GetTicksMsec()+20000;
            var lastProgress=0L;
            try
            {
                while(!window.Complete)
                {
                    cancel.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed,this);
                    if(_connected)
                        foreach(var offset in window.Fill())
                        {
                            var request=new SyncMessage{RequestId=checked(++_nextId),Operation="chunk",Revision=profile.Revision,Hash=mod.Bundle,Offset=offset};
                            pending.Add(request.RequestId,request);
                            _peer!.Send(0,Encode(request),1);
                        }
                    foreach(var response in Poll())
                    {
                        cancel.ThrowIfCancellationRequested();
                        if(!pending.Remove(response.RequestId,out var expected))throw new IOException("收到未请求或重复的下载分片。");
                        CheckResponse(response,expected);
                        if(response.Operation=="pending")throw new IOException("主机资料在下载中尚未就绪，请重新检查。");
                        if(response.Offset!=expected.Offset)throw new IOException("下载分片偏移与请求不一致。");
                        window.Receive(response.Offset,response.Data);
                        idleDeadline=Time.GetTicksMsec()+20000;
                        while(window.TakeNext(out var bytes))
                        {
                            cancel.ThrowIfCancellationRequested();file.Write(bytes);
                            if(watch.ElapsedMilliseconds-lastProgress>=200 || file.Length==mod.BundleSize)
                            {
                                var speed=(file.Length-started)/Math.Max(.001,watch.Elapsed.TotalSeconds)/1024/1024;
                                progress($"{mod.Name}：{file.Length*100/mod.BundleSize}% · {speed:F2} MiB/s（{file.Length/1024}/{mod.BundleSize/1024} KiB）");
                                cancel.ThrowIfCancellationRequested();
                                lastProgress=watch.ElapsedMilliseconds;
                            }
                        }
                    }
                    if(!window.Complete && Time.GetTicksMsec()>idleDeadline)
                        throw new TimeoutException("同步分片等待超时，连续缓存保留，可重试续传。");
                    if(!window.Complete)await _tree.ToSignal(_tree,SceneTree.SignalName.ProcessFrame);
                }
            }
            finally { _discardThrough=_nextId; _busy=false; }
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
            ObjectDisposedException.ThrowIf(_disposed,this);
            cancel.ThrowIfCancellationRequested();
            if(_busy)throw new InvalidOperationException("同步连接正在执行其他操作。");
            if(mod.Bundle.Length!=64||!mod.Bundle.All(char.IsAsciiHexDigit)||mod.BundleSize is <=0 or >ModSyncFiles.MaxPackageSize)
                throw new IOException(mod.Name+"："+mod.Unavailable+"；可改选工坊或请主机开启共享。");
            Directory.CreateDirectory(ModSyncInventory.Cache);var target=Path.Combine(ModSyncInventory.Cache,mod.Bundle+".zip");
            if(File.Exists(target))
            {
                progress("正在核验完整缓存："+mod.Name);
                if(await Task.Run(()=>ModSyncRules.FileHash(target))!=mod.Bundle)throw new IOException("缓存校验失败，请清除损坏文件："+target);
                await Task.Run(()=>ModSyncFiles.VerifyMod(target,mod));cancel.ThrowIfCancellationRequested();return target;
            }
            var partial=target+".partial";
            using(var file=new FileStream(partial,FileMode.OpenOrCreate,System.IO.FileAccess.Write,FileShare.None))
            {
                if(file.Length>mod.BundleSize)throw new IOException("缓存大小异常。");file.Position=file.Length;
                await DownloadWindow(file,profile,mod,progress,cancel);
                cancel.ThrowIfCancellationRequested();
                file.Flush(true);
            }
            progress("下载完成，正在校验文件："+mod.Name);
            if(await Task.Run(()=>ModSyncRules.FileHash(partial))!=mod.Bundle){File.Delete(partial);throw new IOException("下载校验失败，损坏分片已清除，可重试。");}
            await Task.Run(()=>ModSyncFiles.VerifyMod(partial,mod));cancel.ThrowIfCancellationRequested();File.Move(partial,target);return target;
        }
        public void Dispose(){if(_disposed)return;_disposed=true;_peer?.PeerDisconnectNow();_peer=null;_connection.Destroy();_connection.Dispose();}
    }
}
