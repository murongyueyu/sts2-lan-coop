using System.Text;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2LanCoop.Mod;

internal static class RunSaveExtensions
{
    private static Type? Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
    public static void Attach(SerializableRun save, byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        string? text = null;
        if (json.RootElement.TryGetProperty("_ritsulib", out var root))
        {
            if (!root.TryGetProperty("run_saved_data", out var blocks) || blocks.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ritsu存档扩展结构无效，已停止载入。");
            var containsData = blocks.EnumerateObject().Any(block => block.Value.ValueKind == JsonValueKind.Object && block.Value.EnumerateObject().Any());
            if (containsData)
            {
                var registry = Find("STS2RitsuLib.RunData.RunSavedDataRegistry");
                var attach = registry == null ? null : AccessTools.Method(registry, "AttachDocumentFromJson", [typeof(SerializableRun), typeof(string)]);
                if (attach == null) throw new InvalidDataException("存档含Ritsu局内数据，但当前框架无法恢复。已保留原存档，请核对前置版本。");
                attach.Invoke(null, [save, text ??= Encoding.UTF8.GetString(bytes)]);
                var has = AccessTools.Method(registry, "HasDocument", [typeof(SerializableRun)]);
                if (has == null || !(bool)has.Invoke(null, [save])!) throw new InvalidDataException("Ritsu扩展数据未能附着到存档对象，已停止载入。");
            }
        }
        if (json.RootElement.TryGetProperty("_jml", out _) && Find("JmcModLib.Persistence.Run.RunPersistenceManager") is { } persistence)
        {
            var read = AccessTools.Method(persistence, "TryReadDocument", [typeof(string), typeof(long?)]);
            var attach = AccessTools.GetDeclaredMethods(persistence).SingleOrDefault(m => m.Name == "AttachDocument" && m.GetParameters().Length == 2);
            if (read == null || attach == null) throw new InvalidDataException("当前JmcModLib接口无法恢复存档附加数据。");
            var document = read.Invoke(null, [text ??= Encoding.UTF8.GetString(bytes), (long?)save.StartTime]);
            if (document == null) throw new InvalidDataException("JmcModLib附加数据与此存档不匹配，已停止载入。");
            attach.Invoke(null, [save, document]);
        }
    }
}
