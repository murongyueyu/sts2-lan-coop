using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2LanCoop.Mod;

// Populate the library's existing lobby payload before Ritsu serializes it. No permanent settings are written.
internal static class NeuvilletteRunInterop
{
    private static Type? _settings;
    private static Type? _difficulty;
    private static object? _settingsSlot, _difficultySlot;
    private static Type? _beginPayload;
    private static bool _initialized;
    public static bool Required => _settings != null;
    private static MethodInfo Method(Type type, string name) => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
    private static object? Call(Type type, string name, params object[] args) => Method(type, name).Invoke(null, args);
    private static object Slot(Type type, string name) => AccessTools.Field(type, name)?.GetValue(null) ?? throw new MissingFieldException(type.FullName, name);
    private static bool Has(object slot, RunState state)
    {
        object?[] args = [state, null];
        return (bool)Method(slot.GetType(), "TryGet").Invoke(slot, args)!;
    }
    private static void Set(object value, string property, object data) =>
        (AccessTools.Property(value.GetType(), property) ?? throw new MissingMemberException(value.GetType().FullName, property)).SetValue(value, data);
    private static void Stage(object slot, StartRunLobby lobby, object value)
    {
        var staging = AccessTools.Property(slot.GetType(), "Lobby").GetValue(slot)!;
        Method(staging.GetType(), "Set").Invoke(staging, [lobby, value]);
    }
    public static void Ensure()
    {
        if (_initialized) return;
        _settings = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Neuvillette.NeuvilletteSettingsStore", false)).FirstOrDefault(t => t != null);
        if (_settings == null) { _initialized = true; return; }
        var harmony = new Harmony("sts2_lan_coop.neuvillette-run");
        try
        {
            _difficulty = _settings.Assembly.GetType("Neuvillette.Features.Difficulty.NeuvilletteDifficultyService", true)!;
            _settingsSlot = Slot(_settings, "RunSavedSettings"); _difficultySlot = Slot(_difficulty, "SavedState");
            _beginPayload = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("STS2RitsuLib.RunData.Patches.RunSavedDataLobbyBeginRunMessageState", false)).FirstOrDefault(t => t != null)
                ?? throw new TypeLoadException("Ritsu new-run payload state");
            harmony.Patch(Method(typeof(StartRunLobby), "BeginRunForAllPlayers"),
                prefix: new HarmonyMethod(typeof(NeuvilletteRunInterop), nameof(PrepareLobby)) { priority = Priority.First });
            harmony.Patch(Method(_settings, "SyncLocalSettingsToRunState"),
                prefix: new HarmonyMethod(typeof(NeuvilletteRunInterop), nameof(KeepSettings)));
            harmony.Patch(Method(_difficulty, "InitializeNewRun"),
                prefix: new HarmonyMethod(typeof(NeuvilletteRunInterop), nameof(KeepDifficulty)));
            harmony.Patch(Method(_settings, "IsAct4Enabled"),
                prefix: new HarmonyMethod(typeof(NeuvilletteRunInterop), nameof(EarlyAct4)));
            harmony.Patch(Method(typeof(RunManager), "GenerateRooms"),
                prefix: new HarmonyMethod(typeof(NeuvilletteRunInterop), nameof(RequireRunData)) { priority = Priority.First });
            harmony.Patch(Method(typeof(RunManager), "GenerateMap"),
                prefix: new HarmonyMethod(typeof(NeuvilletteRunInterop), nameof(RequireRunData)) { priority = Priority.First });
            _initialized = true;
        }
        catch (Exception ex)
        {
            harmony.UnpatchAll(harmony.Id);
            throw new InvalidOperationException("那维莱特局内数据接口无法适配，已停止LAN连接：" + ex.Message, ex);
        }
    }
    private static void PrepareLobby(StartRunLobby __instance)
    {
        if (!LanSession.Active || LanSession.HostedService == null || _settings == null || _difficulty == null) return;
        var value = Activator.CreateInstance(_settingsSlot!.GetType().GenericTypeArguments[0])!;
        var cache = AccessTools.Field(_settings, "_cache").GetValue(null)!;
        var local = AccessTools.Property(cache.GetType(), "Value").GetValue(cache)!;
        Set(value, "Act4Enabled", Call(_settings, "IsAct4Enabled")!);
        Set(value, "Act4Unlocked", Call(_settings, "IsAct4Unlocked")!);
        Set(value, "OnboardingDifficultyEnabled", Call(_settings, "IsOnboardingDifficultyEnabled")!);
        foreach (var property in new[] { "MultiplayerCourtEnabled", "SponsorRelicEnabled" }) Set(value, property, AccessTools.Property(local.GetType(), property).GetValue(local)!);
        Stage(_settingsSlot, __instance, value);
        // Use the mod's own calculation helpers; avoid reimplementing its tier or revive rules.
        var career = (ITuple)Call(_difficulty, "GetCareerStats")!;
        var completed = (int)career[0]!; var ordinal = completed + 1;
        var tier = (int)Call(_difficulty, "GetInitialTier", ordinal, completed, career[1]!, career[2]!)!;
        var revives = Call(_difficulty, "GetReviveAllowance", tier)!;
        var difficulty = Activator.CreateInstance(_difficultySlot!.GetType().GenericTypeArguments[0])!;
        Set(difficulty, "SchemaVersion", 4); Set(difficulty, "RunOrdinal", ordinal);
        Set(difficulty, "Enabled", GameBuild.StartCharacters(__instance).Any(p => p.Id.Entry == "NEUVILLETTE_CHARACTER_NEUVILLETTE") &&
            (bool)AccessTools.Property(value.GetType(), "OnboardingDifficultyEnabled").GetValue(value)! && ordinal <= 10);
        Set(difficulty, "BaselineTier", tier); Set(difficulty, "Tier", tier);
        Set(difficulty, "ReviveChargesGranted", revives); Set(difficulty, "RemainingRevives", revives);
        Set(difficulty, "LearnedMechanics", Call(_settings, "GetOnboardingLearnedMechanics")!);
        Stage(_difficultySlot, __instance, difficulty);
        GD.Print("[STS2 LAN Co-op] 已在原版开始消息之前冻结那维莱特settings_sync和新手难度快照。");
    }
    private static bool KeepSettings(RunState runState)
    {
        if (!LanSession.Active || _settingsSlot == null) return true;
        if (!Has(_settingsSlot, runState)) throw new InvalidOperationException("本局缺少那维莱特设置快照，不能用本机规则替代。请保留旧档并重新开局。");
        return false;
    }
    private static bool KeepDifficulty(RunState runState)
    {
        if (!LanSession.Active || _difficultySlot == null) return true;
        if (!Has(_difficultySlot, runState)) throw new InvalidOperationException("本局缺少那维莱特难度快照，不能用本机进度替代。请保留旧档并重新开局。");
        return false;
    }
    private static bool EarlyAct4(ref bool __result)
    {
        if (!LanSession.Active || _beginPayload == null || RunManager.Instance.DebugOnlyGetState() != null) return true;
        var payload = AccessTools.Property(_beginPayload, LanSession.HostedService == null ? "PendingNewRunPayload" : "PreparedNewRunPayload").GetValue(null) as string;
        if (string.IsNullOrEmpty(payload)) return true; // Lobby settings UI, before begin-run serialization.
        using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
        if (!json.RootElement.TryGetProperty("_ritsulib", out var root) || !root.TryGetProperty("run_saved_data", out var data) ||
            !data.TryGetProperty("Neuvillette", out var mod) || !mod.TryGetProperty("settings_sync", out var slot) ||
            !slot.TryGetProperty("data", out var settings))
            throw new InvalidOperationException("开始消息缺少那维莱特本局配置，已停止选择章节。");
        __result = settings.GetProperty("Act4Unlocked").GetBoolean() && settings.GetProperty("Act4Enabled").GetBoolean();
        return false;
    }
    private static void RequireRunData(RunManager __instance)
    {
        if (!LanSession.Active || _settingsSlot == null || _difficultySlot == null || __instance.DebugOnlyGetState() is not { } state) return;
        if (!Has(_settingsSlot, state) || !Has(_difficultySlot, state))
            throw new InvalidOperationException("那维莱特本局规则尚未同步，已停止生成房间。请统一完整Mod包后重新加入。");
    }
    public static void ValidateSave(byte[] bytes)
    {
        Ensure();
        if (!Required) return;
        using var json = JsonDocument.Parse(bytes);
        if (!json.RootElement.TryGetProperty("_ritsulib", out var root) || !root.TryGetProperty("run_saved_data", out var data) ||
            !data.TryGetProperty("Neuvillette", out var mod) || new[] { "settings_sync", "difficulty_state" }.Any(key =>
                !mod.TryGetProperty(key, out var slot) || !slot.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("此旧档缺少那维莱特本局设置或难度快照，已保留原件。无法根据当前个人进度安全补齐，请使用完整有效保存点或新建对局。");
    }
}
