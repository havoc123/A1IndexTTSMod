using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using JNGame.Ainpc.Prompt;
using LocalModManager.Abstractions;

namespace A1IndexTTSMod;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
[BepInProcess("WorldApart.exe")]
public sealed class Plugin : BasePlugin, IManagedFeaturePlugin
{
    internal static Plugin? Instance { get; private set; }
    private Harmony? _harmony;
    private ConfigEntry<bool>? _managedEnabled;
    private ConfigEntry<bool>? _autoRead;
    private ConfigEntry<int>? _panelVolume;
    private ConfigEntry<int>? _panelScale;
    private ConfigEntry<int>? _panelTab;

    public string FeatureId => PluginInfo.Guid;
    public string DisplayName => "CosyVoice NPC 朗读";
    public string Description => "朗读游戏内 NPC AI 回复";
    public string FeatureVersion => PluginInfo.Version;
    public bool DesiredEnabled => _managedEnabled?.Value ?? false;
    public FeaturePluginState State => SpeechMvp.State;
    public string StatusMessage => SpeechMvp.StatusMessage;

    public void SetEnabled(bool enabled)
    {
        if (_managedEnabled == null) return;
        _managedEnabled.Value = enabled;
        try { Config.Save(); } catch (Exception e) { Log.LogWarning("Could not persist speech state: " + e.Message); }
        SpeechMvp.SetEnabled(enabled);
    }

    internal void SetAutoRead(bool enabled)
    {
        if (_autoRead == null) return;
        _autoRead.Value = enabled;
        try { Config.Save(); } catch (Exception e) { Log.LogWarning("Could not persist auto-read preference: " + e.Message); }
        SpeechMvp.SetAutoRead(enabled);
    }

    internal void SetPanelVolume(int volume, bool persist)
    {
        if (_panelVolume == null) return;
        _panelVolume.Value = Math.Clamp(volume, 0, 100);
        SpeechMvp.SetVolume(_panelVolume.Value);
        if (persist)
        {
            try { Config.Save(); } catch (Exception e) { Log.LogWarning("Could not persist volume preference: " + e.Message); }
        }
    }

    public override void Load()
    {
        Instance = this;
        try
        {
            var diagnostics = Config.Bind("Stage2A", "Enabled", false,
                "Opt in to local diagnostic dialogue capture. Leave disabled for normal play.");
            var capture = Config.Bind("Stage2A", "CaptureFullPrompt", false,
                "Explicitly save client-side messages/schema to .state/stage2a. Contains private game dialogue.");
            var maxChars = Config.Bind("Stage2A", "MaxRecordChars", 120000, "Maximum characters copied from one text field.");
            var dir = Config.Bind("Stage2A", "CaptureDirectory", ".state/stage2a", "Capture path relative to the mod project directory.");
            if (diagnostics.Value) Probe.Start(Log, capture, maxChars, dir);
            _managedEnabled = Config.Bind("Stage3Mvp", "Enabled", true, "Speak NPC replies and matched preset openings through the local CosyVoice service.");
            _panelVolume = Config.Bind("SpeechPanel", "VolumePercent", 100, "Mod-only playback volume (0-100).");
            _panelVolume.Value = Math.Clamp(_panelVolume.Value, 0, 100);
            _autoRead = Config.Bind("SpeechPanel", "AutoRead", true, "Automatically speak new NPC replies.");
            _panelScale = Config.Bind("SpeechPanel", "InterfaceScalePercent", 100, "User interface scale, in addition to automatic display scaling (75-175%).");
            _panelScale.Value = Math.Clamp(_panelScale.Value, 75, 175);
            _panelTab = Config.Bind("SpeechPanel", "LastTab", 0, "Last open speech panel tab (0-3).");
            _panelTab.Value = Math.Clamp(_panelTab.Value, 0, 3);
            NpcVoiceResolver.Initialize(Log);
            PresetVoiceStyles.Initialize(Log, Config.Bind("Stage3Mvp", "PresetVoiceStyles", true,
                "Use the embedded character-specific emotion library for exact preset greetings and topic openings when no valid reply voice_style is available."));
            var ttsUrl = Config.Bind("Stage3Mvp", "TtsUrl", "http://127.0.0.1:8892/v1/audio/speech", "Local TTS endpoint; audio.cpp /v1/audio/speech by default, or legacy /v1/tts.");
            var backendDefinition = new ConfigDefinition("Stage3Mvp", "Backend");
            var backendWasConfigured = Config.ContainsKey(backendDefinition);
            var backend = Config.Bind(backendDefinition, "CosyVoiceAudioCpp", new ConfigDescription("Maintained backend: CosyVoiceAudioCpp. IndexTtsAudioCpp and IndexTtsLegacyApi are archived compatibility paths."));
            if (BackendConfigMigration.InferLegacyBackend(backendWasConfigured, ttsUrl.Value) is { } migratedBackend)
            {
                backend.Value = migratedBackend;
                Config.Save();
                Log.LogInfo("Migrated existing /v1/tts configuration to explicit IndexTtsLegacyApi backend.");
            }
            var promptEnhancement = Config.Bind("Stage3Mvp", "PromptEnhancement", true, "Add voice_style to actual NPC reply prompts while NPC speech is enabled.");
            Probe.SetPromptEnhancement(promptEnhancement);
            SpeechMvp.Start(
                Log,
                _managedEnabled,
                ttsUrl,
                Config.Bind("Stage3Mvp", "ReferenceId", "demo", "Reference audio ID under indextts25/voices."),
                Config.Bind("Stage3Mvp", "AudioCppModelId", "cosyvoice3", "Model ID in the local audio.cpp server config; cosyvoice3 for the maintained CosyVoice route."),
                Config.Bind("Stage3Mvp", "TimeoutSeconds", 180, "Maximum wait for one synthesis request."),
                Config.Bind("Stage3Mvp", "AutoStartAudioCpp", true, "Start the project audio.cpp service and close it when this game exits."),
                Config.Bind("Stage3Mvp", "AudioCppPrecision", "q8_0", "Auto-start model precision: q8_0, f16, or orig (model file must already exist)."),
                Config.Bind("Stage3Mvp", "GpuBackend", "Auto", "GPU route for auto-started audio.cpp: Auto (NVIDIA CUDA / AMD Vulkan), Nvidia, or Vulkan."),
                Config.Bind("Stage3Mvp", "GpuDevice", 0, "Vulkan device index (default 0; change only when a multi-GPU system lists AMD at another index)."),
                backend);
            SpeechMvp.SetAutoRead(_autoRead.Value);
            SpeechMvp.SetVolume(_panelVolume.Value);
            SpeechPanelUi.Configure(Log, _panelVolume, _autoRead, _managedEnabled, _panelScale, _panelTab);
            StreamingAsr.ConfigureRoot(Paths.GameRootPath);
            StreamingAsr.SetLog(message => Log.LogInfo(message));
            SpeechPanelUi.ConfigureAsr(Config.Bind("SpeechInput", "Enabled", true, "Enable in-process streaming Chinese ASR."),
                Config.Bind("SpeechInput", "DeviceId", "", "WASAPI capture device ID; blank uses the current Windows default microphone."),
                Config.Bind("SpeechInput", "ModelProfile", "lightweight14m", "Installed model: lightweight14m or accurate160m. Only one model is loaded."));
            Probe.SetSpeechLog(Log);
            _harmony = new Harmony(PluginInfo.Guid);
            PatchCompleteBudgeted();
            PatchDialogueLifecycle("Game.NpcDialoguePanel", "OnShow", nameof(Probe.OnDialogueShowPrefix));
            PatchDialogueLifecycle("Game.NpcDialoguePanel", "OnHide", nameof(Probe.OnDialogueHidePrefix));
            PatchDialogueSetData();
            PatchUiInputGuards();
            PatchExact("Game", "Game.Model.NpcModel", "IsRepeatedChatReply", new[] { "System.String", "System.String" },
                "OnPreGameReplyNormalization", "OnPreGameReplyNormalizationPostfix");
            PatchExact("Game", "Game.Model.NpcModel", "AddNpcChatMessage", new[] { "System.String", "System.String" },
                "OnNpcDisplayPrefix", "OnNpcDisplay", "OnNpcDisplayFinalizer");
            PatchSingleParameter("Game", "Game.NpcPersuadePanel", "OnReceivePersuadeResponse", "Game.Model.ChatMessage", "OnPersuadeResponsePrefix", "OnPersuadeResponse");
            PatchSingleParameter("Game", "Game.NpcPersuadePanel", "OnReceivePersuadeResponseNpc", "Game.Model.ChatMessage", "OnPersuadeResponsePrefix", "OnPersuadeResponse");
            if (diagnostics.Value)
            {
                Patch("Game", "Game.Model.NpcModel", "SendChatMessage", "OnPlayerInput", prefix:true);
                Patch("Game", "Game.Model.AinpcRuntime", "LogRequest", "OnRequest");
            }
            Log.LogInfo("Stage2A diagnostic capture is " + (diagnostics.Value ? (capture.Value ? "ON (full prompt)" : "ON") : "OFF"));
        }
        catch (Exception e) { Log.LogError("Stage2A initialization failed safely: " + e.GetType().Name + ": " + e.Message); }
    }

    public override bool Unload()
    {
        StreamingAsr.Shutdown();
        return base.Unload();
    }

    private void Patch(string assembly, string type, string method, string callback, bool prefix=false)
    {
        var targetType = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(type);
        var target = targetType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == method);
        var postfix = typeof(Probe).GetMethod(callback, BindingFlags.Public | BindingFlags.Static)!;
        if (target == null) { Log.LogWarning($"Probe target not found: {type}.{method}"); return; }
        if (prefix) _harmony!.Patch(target, prefix: new HarmonyMethod(postfix));
        else _harmony!.Patch(target, postfix: new HarmonyMethod(postfix));
        Log.LogInfo($"Probe hooked {target.DeclaringType?.FullName}.{target.Name}({string.Join(",", target.GetParameters().Select(p => p.ParameterType.Name))})");
    }

    private void PatchDialogueLifecycle(string typeName, string methodName, string callbackName)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Game")?.GetType(typeName);
        var candidates = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == methodName && m.GetParameters().Length == 0).ToArray() ?? Array.Empty<MethodInfo>();
        if (candidates.Length != 1)
        {
            Log.LogWarning($"Dialogue UI hook not installed: {typeName}.{methodName} declared zero-argument candidates={candidates.Length}; panel remains unavailable until a verified hook is added.");
            return;
        }
        var callback = typeof(Probe).GetMethod(callbackName, BindingFlags.Public | BindingFlags.Static);
        if (callback == null) { Log.LogWarning("Dialogue UI callback missing: " + callbackName); return; }
        _harmony!.Patch(candidates[0], prefix: new HarmonyMethod(callback));
        Log.LogInfo($"Hooked verified dialogue lifecycle {candidates[0].DeclaringType?.FullName}.{methodName}() declared on {typeName}.");
    }

    private void PatchDialogueSetData()
    {
        const string typeName = "Game.NpcDialoguePanel";
        var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Game")?.GetType(typeName);
        var hierarchy = new List<Type>();
        for (var current = type; current != null && current != typeof(object); current = current.BaseType) hierarchy.Add(current);
        var declaredMethods = hierarchy.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)).ToArray();
        const string npcIdType = "LubanDatas.TbNpcBaseCfgId";
        var candidates = declaredMethods.Where(m => m.Name == "SetDataInternal" &&
            m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(new[] { npcIdType, "System.Boolean" })).ToArray();
        var targetName = "SetDataInternal";
        if (candidates.Length == 0)
        {
            targetName = "SetData";
            candidates = declaredMethods.Where(m => m.Name == targetName && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.FullName == npcIdType).ToArray();
        }
        if (candidates.Length != 1)
        {
            var signatures = declaredMethods
                .Where(m => m.Name is "SetData" or "SetDataInternal")
                .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName))})");
            Log.LogWarning($"NPC dialogue {targetName} hook not installed: candidate count={candidates.Length}; observed signatures=[{string.Join(";", signatures)}].");
            return;
        }
        var callback = typeof(Probe).GetMethod(nameof(Probe.OnDialogueSetDataPrefix), BindingFlags.Public | BindingFlags.Static)!;
        _harmony!.Patch(candidates[0], prefix: new HarmonyMethod(callback));
        Log.LogInfo($"Hooked verified dialogue binding {candidates[0].DeclaringType?.FullName}.{targetName}({string.Join(",", candidates[0].GetParameters().Select(p => p.ParameterType.FullName))}); callback filters runtime instances to {typeName}.");
    }

    private void PatchUiInputGuards()
    {
        var gameAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Game");
        var buttonHandler = gameAssembly?.GetType("Game.InputHandler.ButtonInputHandler") ??
                            gameAssembly?.GetType("Il2CppGame.InputHandler.ButtonInputHandler");
        var invokeCandidates = buttonHandler?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == "InvokePerformed" && m.GetParameters().Length == 2)
            .Distinct().ToArray() ?? Array.Empty<MethodInfo>();
        if (invokeCandidates.Length == 1)
        {
            PatchUniqueMethod(buttonHandler!.Assembly.GetName().Name!, buttonHandler.FullName!, "InvokePerformed",
                m => m == invokeCandidates[0], nameof(Probe.OnUiShortcutPrefix));
        }
        else
        {
            var signatures = buttonHandler?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "InvokePerformed")
                .Select(m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName))) ?? Array.Empty<string>();
            Log.LogWarning($"UI shortcut guard not installed: ButtonInputHandler.InvokePerformed two-argument candidates={invokeCandidates.Length}; signatures=[{string.Join(";", signatures)}].");
        }
        PatchUniqueMethod("UnityEngine.UI", "UnityEngine.EventSystems.EventSystem", "Update",
            m => m.GetParameters().Length == 0, nameof(Probe.OnUiEventSystemPrefix), nameof(Probe.OnUiEventSystemPostfix));
    }

    private void PatchUniqueMethod(string assemblyName, string typeName, string methodName,
        Func<MethodInfo, bool> signaturePredicate, string callbackName, string? postfixName = null)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName)?.GetType(typeName);
        var candidates = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == methodName && signaturePredicate(m)).ToArray() ?? Array.Empty<MethodInfo>();
        if (candidates.Length != 1)
        {
            Log.LogWarning($"UI input guard not installed: {typeName}.{methodName} exact candidate count={candidates.Length}.");
            return;
        }
        var callback = typeof(Probe).GetMethod(callbackName, BindingFlags.Public | BindingFlags.Static);
        if (callback == null) { Log.LogWarning("UI input callback missing: " + callbackName); return; }
        var postfix = postfixName == null ? null : typeof(Probe).GetMethod(postfixName, BindingFlags.Public | BindingFlags.Static);
        _harmony!.Patch(candidates[0], prefix: new HarmonyMethod(callback),
            postfix: postfix == null ? null : new HarmonyMethod(postfix));
        Log.LogInfo($"Hooked UI input guard {candidates[0].DeclaringType?.FullName}.{methodName}({string.Join(",", candidates[0].GetParameters().Select(p => p.ParameterType.FullName))}).");
    }

    private void PatchExact(string assembly, string type, string method, string[] parameterTypes, string prefixCallback, string postfixCallback, string? finalizerCallback = null)
    {
        var targetType = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(type);
        var target = targetType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == method && m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes));
        if (target == null) { Log.LogWarning($"Hook target not found with exact signature: {type}.{method}({string.Join(",", parameterTypes)})"); return; }
        var prefix = typeof(Probe).GetMethod(prefixCallback, BindingFlags.Public | BindingFlags.Static)!;
        var postfix = typeof(Probe).GetMethod(postfixCallback, BindingFlags.Public | BindingFlags.Static)!;
        var finalizer = finalizerCallback == null ? null : new HarmonyMethod(typeof(Probe).GetMethod(finalizerCallback, BindingFlags.Public | BindingFlags.Static)!);
        _harmony!.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix), finalizer: finalizer);
        Log.LogInfo($"Hooked exact signature {target.DeclaringType?.FullName}.{target.Name}({string.Join(",", parameterTypes)})");
    }

    private void PatchSingleParameter(string assembly, string type, string method, string parameterType, string prefixCallback, string postfixCallback)
    {
        var targetType = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(type);
        var target = targetType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == method && m.GetParameters() is { Length: 1 } p && p[0].ParameterType.FullName == parameterType);
        if (target == null) { Log.LogWarning($"Hook target not found with exact signature: {type}.{method}({parameterType})"); return; }
        var prefix = typeof(Probe).GetMethod(prefixCallback, BindingFlags.Public | BindingFlags.Static)!;
        var postfix = typeof(Probe).GetMethod(postfixCallback, BindingFlags.Public | BindingFlags.Static)!;
        _harmony!.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
        Log.LogInfo($"Hooked exact signature {target.DeclaringType?.FullName}.{target.Name}({parameterType})");
    }

    private void PatchCompleteBudgeted()
    {
        const string assembly = "Game";
        const string type = "Game.Model.AinpcRuntime";
        const string method = "CompleteBudgeted";
        var targetType = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)?.GetType(type);
        var target = targetType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(candidate =>
            {
                if (candidate.Name != method) return false;
                var p = candidate.GetParameters();
                return p.Length == 10 &&
                    p[0].ParameterType.FullName == "Game.Model.NpcModel" &&
                    p[1].ParameterType.FullName == "JNGame.Ainpc.Prompt.ModeParams" &&
                    p[2].ParameterType == typeof(string) && p[3].ParameterType == typeof(string) && p[4].ParameterType == typeof(string) &&
                    p[5].ParameterType.IsGenericType && p[5].ParameterType.GetGenericTypeDefinition().FullName == "Il2CppSystem.Collections.Generic.IReadOnlyList`1" &&
                    p[6].ParameterType == typeof(string) && p[7].ParameterType == typeof(string) &&
                    p[8].ParameterType.FullName == "JNGame.Ainpc.Llm.LlmCallOptions" &&
                    p[9].ParameterType.FullName == "JNGame.Ainpc.Prompt.PromptDocument";
            });
        if (target == null) { Log.LogWarning("Prompt enhancement hook not found with exact CompleteBudgeted signature; feature remains off for LLM requests."); return; }
        var prefix = typeof(Probe).GetMethod(nameof(Probe.OnCompleteBudgetedPrefix), BindingFlags.Public | BindingFlags.Static)!;
        _harmony!.Patch(target, prefix: new HarmonyMethod(prefix));
        Log.LogInfo("Hooked exact Game.Model.AinpcRuntime.CompleteBudgeted signature for actual-reply prompt/schema extension.");
    }
}

internal static class Probe
{
    private static readonly string RunId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 256);
    private static long _seq;
    private static int _maxChars = 120000;
    private static bool _full;
    private static bool _captureEnabled;
    private static long _lastInputSeq;
    private static long _lastRequestSeq;
    private static string _runDir = "";
    private static BepInEx.Logging.ManualLogSource? _log;
    private static BepInEx.Logging.ManualLogSource? _speechLog;
    private static ConfigEntry<bool>? _promptEnhancement;
    [ThreadStatic] private static int _npcDisplayScopeDepth;

    public static void SetSpeechLog(BepInEx.Logging.ManualLogSource log) => _speechLog = log;
    public static void SetPromptEnhancement(ConfigEntry<bool> enabled) => _promptEnhancement = enabled;

    public static void OnDialogueShowPrefix(object __instance)
    {
        try { SpeechPanelUi.OnDialogueShown(__instance); }
        catch (Exception e) { _speechLog?.LogWarning("Dialogue panel show observer failed: " + e.GetType().Name); }
    }

    public static void OnDialogueHidePrefix(object __instance)
    {
        try { SpeechPanelUi.OnDialogueHidden(__instance); }
        catch (Exception e) { _speechLog?.LogWarning("Dialogue panel hide observer failed: " + e.GetType().Name); }
    }

    public static void OnDialogueSetDataPrefix(object __instance, object[] __args)
    {
        if (__instance.GetType().FullName != "Game.NpcDialoguePanel") return;
        try { SpeechPanelUi.OnDialogueData(__instance, __args.Length > 0 ? __args[0] : null); }
        catch (Exception e) { _speechLog?.LogWarning("Dialogue NPC binding observer failed: " + e.GetType().Name); }
    }

    public static bool OnUiShortcutPrefix(UnityEngine.InputSystem.InputAction.CallbackContext __1)
    {
        if (StreamingAsr.IsActive && __1.control?.device?.TryCast<UnityEngine.InputSystem.Keyboard>() != null) return false;
        if (!SpeechPanelUi.ShouldBlockGameInput()) return true;
        // Mouse actions outside the overlay remain available; keyboard/gamepad shortcuts do not.
        var pointer = __1.control?.device?.TryCast<UnityEngine.InputSystem.Pointer>();
        return pointer != null && !SpeechPanelUi.ShouldBlockGamePointer();
    }

    public static bool OnUiEventSystemPrefix(UnityEngine.EventSystems.EventSystem __instance, out bool __state)
    {
        __state = __instance.sendNavigationEvents;
        if (SpeechPanelUi.ShouldBlockGameInput()) __instance.sendNavigationEvents = false;
        return !SpeechPanelUi.ShouldBlockGamePointer();
    }

    public static void OnUiEventSystemPostfix(UnityEngine.EventSystems.EventSystem __instance, bool __state)
        => __instance.sendNavigationEvents = __state;

    public static void OnCompleteBudgetedPrefix(object[] __args, ref string __6,
        ref JNGame.Ainpc.Llm.LlmCallOptions __8, ref PromptDocument __9)
    {
        var tail = __6;
        var options = __8;
        var sourceDocument = __9;
        if (__args.Length == 10)
        {
            var purposeObservation = __args[2]?.ToString() ?? "null";
            var sectionSummary = DescribePromptDocument(sourceDocument);
            _speechLog?.LogInfo($"Stage3 CompleteBudgeted observed purpose={purposeObservation} tailChars={tail.Length} inputChars={(__args[7] as string)?.Length ?? -1} doc={sectionSummary}");
            if (_captureEnabled && _full)
            {
                var formatValue = options.ResponseFormat;
                var format = "";
                try { if (formatValue != null) format = PromptEnhancer.ReadResponseFormatJson(formatValue); }
                catch (Exception e) { format = "<serialize_error:" + formatValue?.GetType().FullName + ":" + e.Message + ">"; }
                var hasContract = TryReadOutputContract(sourceDocument, out var contract);
                Emit(new { kind = "complete_budgeted_request", runId = RunId, purpose = purposeObservation,
                    tail = Limit(tail), tailSha256 = Hash(tail),
                    outputContractSource = hasContract ? "PromptDocument" : "none",
                    outputContract = hasContract ? Limit(contract) : null, outputContractSha256 = hasContract ? Hash(contract) : null,
                    responseFormat = Limit(format), responseFormatSha256 = Hash(format), doc = sectionSummary });
            }
        }
        if (__args.Length == 10 && string.Equals(__args[2] as string, "ActualReply", StringComparison.OrdinalIgnoreCase) &&
            (!SpeechMvp.IsFeatureEnabled || !(_promptEnhancement?.Value ?? false)))
        {
            string? schema = null;
            try { if (options.ResponseFormat != null) schema = PromptEnhancer.ReadResponseFormatJson(options.ResponseFormat); }
            catch (Exception e) { _speechLog?.LogWarning("Stage3 actual reply schema observation failed: " + e.GetType().Name); }
            SpeechPanelData.RecordPromptSnapshot(false, schema, null,
                !SpeechMvp.IsFeatureEnabled ? "功能总开关关闭，未向本轮请求添加语音字段" : "提示增强已关闭，本轮使用游戏原始提示和 Schema");
        }
        if (!SpeechMvp.IsFeatureEnabled || !(_promptEnhancement?.Value ?? false) || __args.Length != 10) return;
        var purpose = __args[2] as string;
        if (!string.Equals(purpose, "ActualReply", StringComparison.OrdinalIgnoreCase))
        {
            _speechLog?.LogInfo($"Stage3 prompt enhancement skipped purpose={purpose ?? "null"} reason=not_actual_reply");
            return;
        }
        string? originalSchema = null;
        try { if (options?.ResponseFormat != null) originalSchema = PromptEnhancer.ReadResponseFormatJson(options.ResponseFormat); }
        catch (Exception e) { _speechLog?.LogWarning("Stage3 original reply schema observation failed: " + e.GetType().Name); }
        SpeechPanelData.RecordPromptSnapshot(false, originalSchema, null, "正在观察实际出站请求");
        void RecordSkipped(string reason) => SpeechPanelData.RecordPromptSnapshot(false, originalSchema, null,
            "本轮未添加语音风格要求：" + reason);
        if (options == null || sourceDocument == null)
        {
            RecordSkipped("游戏请求参数不完整");
            _speechLog?.LogWarning($"Stage3 prompt enhancement skipped purpose=ActualReply reason=unexpected_arguments tail={tail?.GetType().Name ?? "null"} options={options?.GetType().Name ?? "null"} document={sourceDocument?.GetType().Name ?? "null"}");
            return;
        }
        var committed = false;
        try
        {
            var hasDocumentContract = TryReadOutputContract(sourceDocument, out var originalContract);
            var exampleSource = hasDocumentContract ? originalContract : tail;
            if (!PromptEnhancer.TryExtractReplyExample(exampleSource, out var originalExample, out _))
            {
                RecordSkipped("游戏输出契约中未找到 JSON 回复示例");
                _speechLog?.LogWarning($"Stage3 prompt enhancement skipped purpose=ActualReply reason=no_json_reply_example source={(hasDocumentContract ? "PromptDocument" : "tail")}");
                return;
            }
            string fieldDescriptions;
            try { fieldDescriptions = PromptEnhancer.CreateFieldDescriptionsFromResponseFormat(options); }
            catch (Exception e)
            {
                RecordSkipped("读取游戏字段说明失败：" + DescribeError(e));
                _speechLog?.LogWarning("Stage3 prompt enhancement skipped purpose=ActualReply reason=field_descriptions_failed: " + DescribeError(e));
                return;
            }
            object? requestOptions;
            PromptEnhancer.Extension? extension;
            try
            {
                if (!PromptEnhancer.TryCreateRequestSnapshot(options, tail, originalExample, fieldDescriptions,
                    out requestOptions, out extension) || requestOptions == null || extension == null)
                {
                    RecordSkipped("无法构造本轮提示增强快照");
                    _speechLog?.LogWarning("Stage3 prompt enhancement skipped purpose=ActualReply reason=request_snapshot_failed");
                    return;
                }
            }
            catch (Exception e)
            {
                RecordSkipped("构造本轮提示增强快照失败：" + DescribeError(e));
                _speechLog?.LogWarning("Stage3 prompt enhancement skipped purpose=ActualReply reason=request_snapshot_exception: " + DescribeError(e));
                return;
            }
            PromptDocument? requestDocument = sourceDocument;
            if (hasDocumentContract && (!TryClonePromptDocument(sourceDocument, extension, out requestDocument) || requestDocument == null))
            {
                RecordSkipped("无法复制游戏输出契约文档");
                _speechLog?.LogWarning("Stage3 prompt enhancement skipped purpose=ActualReply reason=document_contract_clone_failed");
                return;
            }
            // Commit the related prompt, response format, and output-contract document together.
            if (requestOptions is not JNGame.Ainpc.Llm.LlmCallOptions scopedOptions)
                throw new InvalidOperationException("Request options snapshot has an unexpected runtime type.");
            __6 = extension.Tail;
            __8 = scopedOptions;
            if (hasDocumentContract) __9 = requestDocument!;
            committed = true;
            var hasSchema = !string.IsNullOrEmpty(extension.ResponseFormatJson);
            SpeechPanelData.RecordPromptSnapshot(true, hasSchema ? extension.ResponseFormatJson : null, extension.ContractAddition,
                hasSchema
                    ? "已要求有可朗读台词时必须返回语音风格帧；已观测增强后的 ResponseFormat，最终供应商网络层未观测"
                    : "已要求有可朗读台词时必须返回语音风格帧，并添加示例；本轮自定义模型请求未提供 Schema（ResponseFormat 为空），沿用游戏文本输出契约");
            if (_captureEnabled && _full)
            {
                var responseFormat = extension.ResponseFormatJson;
                Emit(new { kind = "prompt_enhancement_applied", runId = RunId, purpose,
                    tail = Limit(extension.Tail), tailHasVoiceStyle = extension.Tail.Contains("voice_style", StringComparison.Ordinal),
                    responseFormat = Limit(responseFormat), schemaHasVoiceStyle = responseFormat.Contains("voice_style", StringComparison.Ordinal),
                    contractSource = hasDocumentContract ? "PromptDocument" : "tail" });
            }
            _speechLog?.LogInfo($"Stage3 prompt enhancement applied purpose={purpose} contractSource={(hasDocumentContract ? "PromptDocument" : "tail")} snapshot=tail+{(hasSchema ? "schema+" : "")}example+field_descriptions{(hasDocumentContract ? "+PromptDocument" : "")}");
        }
        catch (Exception e)
        {
            if (committed)
                _speechLog?.LogWarning("Stage3 prompt enhancement committed; subsequent diagnostics failed: " + DescribeError(e));
            else
            {
                RecordSkipped("提示增强失败：" + DescribeError(e));
                _speechLog?.LogWarning("Stage3 prompt enhancement failed open without modifying this request: " + DescribeError(e));
            }
        }
    }

    private static string DescribeError(Exception exception)
    {
        while (exception is TargetInvocationException invocation && invocation.InnerException is Exception inner) exception = inner;
        var message = exception.Message;
        return exception.GetType().FullName + ": " + (message.Length <= 240 ? message : message[..240]);
    }

    private static string DescribePromptDocument(object? document)
    {
        if (document is not PromptDocument promptDocument) return "null_or_unexpected";
        try
        {
            var sections = promptDocument.Sections;
            var collection = new Il2CppSystem.Collections.Generic.IReadOnlyCollection<PromptSection>(sections.Pointer);
            var result = new List<string>();
            for (var i = 0; i < collection.Count; i++)
            {
                var section = sections[i];
                if (section == null) continue;
                result.Add($"{section.Key}:{(section.Body ?? "").Length}:{Hash(section.Body ?? "")}");
            }
            return string.Join(",", result);
        }
        catch (Exception e) { return "unreadable:" + e.GetType().Name; }
    }

    private static bool TryReadOutputContract(PromptDocument document, out string body)
    {
        body = "";
        try
        {
            var sections = document.Sections;
            var collection = new Il2CppSystem.Collections.Generic.IReadOnlyCollection<PromptSection>(sections.Pointer);
            for (var i = 0; i < collection.Count; i++)
            {
                var section = sections[i];
                if (section != null && section.Key == PromptDocument.KEY_OUTPUT_CONTRACT)
                {
                    body = section.Body ?? "";
                    return !string.IsNullOrWhiteSpace(body);
                }
            }
        }
        catch (Exception e) { _speechLog?.LogWarning("Stage3 output contract read failed: " + e.GetType().Name); }
        return false;
    }

    private static bool TryClonePromptDocument(PromptDocument source, PromptEnhancer.Extension extension, out PromptDocument? clone)
    {
        clone = null;
        try
        {
            var sourceSections = source.Sections;
            var sourceCollection = new Il2CppSystem.Collections.Generic.IReadOnlyCollection<PromptSection>(sourceSections.Pointer);
            var result = new PromptDocument();
            var foundContract = false;
            for (var i = 0; i < sourceCollection.Count; i++)
            {
                var section = sourceSections[i];
                if (section == null) return false;
                var body = section.Body ?? "";
                if (section.Key == PromptDocument.KEY_OUTPUT_CONTRACT)
                {
                    foundContract = true;
                    if (!PromptEnhancer.TryExtractReplyExample(body, out _, out var updatedBody,
                        JsonNode.Parse(extension.ExampleJson) as JsonObject)) return false;
                    body = updatedBody;
                    if (!body.Contains(extension.ContractAddition, StringComparison.Ordinal))
                        body = body.TrimEnd() + "\n\n" + extension.ContractAddition;
                }
                result.Add(section.Key, body, section.Protection);
            }
            if (!foundContract) return false;
            clone = result;
            return true;
        }
        catch (Exception e) { _speechLog?.LogWarning("Stage3 output contract clone failed: " + e.GetType().Name); return false; }
    }

    public static void Start(BepInEx.Logging.ManualLogSource log, ConfigEntry<bool> full, ConfigEntry<int> maxChars, ConfigEntry<string> dir)
    {
        _log = log; _full = full.Value; _captureEnabled = true; _maxChars = Math.Clamp(maxChars.Value, 1024, 500000);
        var project = Path.GetFullPath(Path.Combine(Paths.GameRootPath, "A1IndexTTSMod"));
        _runDir = Path.GetFullPath(Path.Combine(project, dir.Value, RunId));
        if (!_runDir.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Capture path escapes project directory.");
        Directory.CreateDirectory(_runDir);
        _ = Task.Run(Writer);
        Emit(new { kind="run_start", captureFullPrompt=_full, pid=Environment.ProcessId, gameRoot=Paths.GameRootPath });
    }

    public static void OnPlayerInput(object __instance, object[] __args)
    {
        Safe(() => { var text = __args.OfType<string>().FirstOrDefault() ?? ""; _lastInputSeq = EmitText("player_input", __instance, text); });
    }

    // This game method receives the provider reply string before NpcModel maps it to
    // ChatReply.RawEnvelope. Observe both strings without changing the reply or return value.
    public static void OnPreGameReplyNormalization(object __instance, string __0, string __1)
    {
        if (!_captureEnabled) return;
        Safe(() =>
        {
            var raw = __0 ?? "";
            var previousContent = __1 ?? "";
            var separated = NpcReplyPayload.Separate(raw);
            Emit(new
            {
                kind = "llm_content_before_game_reply_mapping",
                runId = RunId,
                npcId = NpcId(__instance),
                rawLength = raw.Length,
                rawSha256 = Hash(raw),
                raw = _full ? Limit(raw) : null,
                previousContentLength = previousContent.Length,
                previousContentSha256 = Hash(previousContent),
                voiceStyleStatus = separated?.VoiceStyleStatus ?? "unparseable_or_not_reply_json",
                voiceStyleJsonType = separated?.VoiceStyleJsonType ?? "unknown",
                voiceStyleFailureReason = separated?.VoiceStyleFailureReason
            });
        });
    }

    public static void OnPreGameReplyNormalizationPostfix(object __instance, string __0, string __1, bool __result)
    {
        // Keep the target's decision visible in the same event stream to show whether the
        // game discarded a candidate as a duplicate. No game state is modified.
        Safe(() =>
        {
            var raw = __0 ?? "";
            var separated = NpcReplyPayload.Separate(raw);
            var fields = separated == null ? ExtractNpcReply(raw) : ExtractNpcReply(separated.GameJson);
            var npcKey = NpcId(__instance);
            var contentSha256 = fields?.Content is { } content ? Hash(content) : null;
            var acceptedContent = fields?.Content;
            var cached = !__result && separated != null &&
                !string.IsNullOrWhiteSpace(acceptedContent) && SpeechMvp.IsFeatureEnabled;
            if (cached)
                StyleStore.Store(npcKey, acceptedContent!, separated!.VoiceStyle, Hash(raw), raw,
                    separated.VoiceStyleStatus, separated.VoiceStyleFailureReason, separated.VoiceStyleSource);
            if (_captureEnabled) Emit(new
            {
                kind = "llm_content_repeat_check_result",
                runId = RunId,
                npcId = npcKey,
                rawSha256 = Hash(raw),
                previousContentSha256 = Hash(__1 ?? ""),
                isRepeated = __result,
                cachedVoiceStyle = cached,
                contentSha256,
                voiceStyleStatus = separated?.VoiceStyleStatus ?? "unparseable_or_not_reply_json"
            });
        });
    }
    internal sealed record NpcReplyFields(string? Content, string? Emotion, string? Control, bool? GiveGift, string? GiftItem, VoiceStyle? VoiceStyle, string DisplayIdentity);
    internal sealed class NpcDisplayPatchState
    {
        private int _exited;
        public NpcReplyFields? Reply { get; init; }
        public string? GameJson { get; init; }
        public string? RawReplyJson { get; init; }
        public string? PluginRawOutput { get; init; }
        public string VoiceStyleStatus { get; init; } = "unknown";
        public string? VoiceStyleSource { get; init; }
        public string? VoiceStyleFailureReason { get; init; }
        public bool EnteredScope { get; init; }
        public void ExitScope()
        {
            if (EnteredScope && Interlocked.Exchange(ref _exited, 1) == 0)
                _npcDisplayScopeDepth = Math.Max(0, _npcDisplayScopeDepth - 1);
        }
    }

    public static void OnNpcDisplayPrefix(object __instance, object[] __args, ref string __0, ref string __1, out NpcDisplayPatchState? __state)
    {
        _npcDisplayScopeDepth++;
        __state = new NpcDisplayPatchState { EnteredScope = true };
        try
        {
            if (__0 != null)
            {
                var displayEnvelope = StyleEnvelopeCodec.Decode(__0);
                if (displayEnvelope.Status != "absent") __0 = displayEnvelope.Content;
            }
            var raw = __1;
            if (raw == null) return;
            var separated = NpcReplyPayload.Separate(raw);
            if (separated == null) return;
            __1 = separated.GameJson;
            var fields = ExtractNpcReply(separated.GameJson);
            var pendingStyle = StyleStore.Take(NpcId(__instance), fields?.Content);
            var effectiveStyle = separated.VoiceStyle ?? pendingStyle?.Style;
            var originalReply = pendingStyle?.RawReply ?? raw;
            var pluginRawOutput = effectiveStyle == null ? null : NpcReplyPayload.WithVoiceStyle(separated.GameJson, effectiveStyle);
            EmitRawReplyObservation(__instance, raw, separated, effectiveStyle,
                separated.VoiceStyle != null ? separated.VoiceStyleSource : pendingStyle == null ? null : "IsRepeatedChatReply_or_content_envelope",
                pendingStyle?.SourceRawSha256);
            __state = new NpcDisplayPatchState
            {
                EnteredScope = true,
                GameJson = separated.GameJson,
                RawReplyJson = originalReply,
                PluginRawOutput = pluginRawOutput,
                VoiceStyleStatus = effectiveStyle == null ? pendingStyle?.Status ?? separated.VoiceStyleStatus : "valid",
                VoiceStyleSource = separated.VoiceStyle != null ? separated.VoiceStyleSource : pendingStyle?.Source,
                VoiceStyleFailureReason = separated.VoiceStyleFailureReason ?? pendingStyle?.FailureReason,
                Reply = fields == null ? null : fields with { VoiceStyle = effectiveStyle, DisplayIdentity = "" }
            };
        }
        catch (Exception e) { _speechLog?.LogWarning("Stage3 reply field separation error: " + e.GetType().Name); }
    }

    public static void OnNpcDisplay(object __instance, object[] __args, NpcDisplayPatchState? __state)
    {
        try { Safe(() =>
        {
            var text = __args.OfType<string>().FirstOrDefault() ?? "";
            // Re-clean at the consuming boundary: Harmony's __args array may still expose
            // the pre-prefix string even when ref __0 was replaced for the original method.
            text = SpeechTextFilter.RemoveVoiceStyleEnvelope(text);
            var raw = __state?.GameJson ?? __args.OfType<string>().Skip(1).FirstOrDefault();
            if (_captureEnabled) EmitText("npc_display_game_payload", __instance, text, raw);
            if (__state?.PluginRawOutput is { } pluginRawOutput)
            {
                var synchronized = SynchronizeLastNpcRawOutput(__instance, text, pluginRawOutput, out var actualRawOutput);
                if (_captureEnabled) Emit(new
                {
                    kind = "npc_raw_output_plugin_sync",
                    runId = RunId,
                    npcId = NpcId(__instance),
                    synchronized,
                    intendedRawSha256 = Hash(pluginRawOutput),
                    actualRawSha256 = actualRawOutput == null ? null : Hash(actualRawOutput),
                    voiceStyleStatus = ExtractNpcReply(actualRawOutput ?? "")?.VoiceStyle == null ? "absent" : "valid",
                    rawOutput = _full ? Limit(actualRawOutput ?? "") : null
                });
                if (!synchronized) _speechLog?.LogWarning("Stage3 could not synchronize recovered voice_style into ChatMessage.NpcRawOutput; TTS still uses the recovered style metadata.");
            }
            // This hook creates NPC messages only. A text-only message must additionally
            // match this character's preset library before it is admitted to speech.
            var reply = __state?.Reply ?? ExtractNpcReply(raw ?? "");
            var npcKey = NpcId(__instance);
            var preset = reply?.VoiceStyle == null ? PresetVoiceStyles.Resolve(npcKey, text, ActiveTopicId(__instance)) : null;
            if (!string.IsNullOrWhiteSpace(text) && (!string.IsNullOrWhiteSpace(reply?.Content) || preset != null))
            {
                var identity = DisplayMessageIdentity(__instance, npcKey);
                if (identity == null) { _speechLog?.LogWarning("Stage3 display reply has no ChatMessage timestamp identity; TTS skipped to avoid duplicate playback."); return; }
                var spokenText = SpeechTextFilter.RemoveParentheticals(text);
                var style = reply?.VoiceStyle ?? preset?.Style;
                SpeechPanelData.RecordReply(identity, npcKey, text, spokenText, reply?.Emotion, style,
                    __state?.RawReplyJson ?? raw, preset != null ? "preset_matched" : __state?.VoiceStyleStatus ?? "unknown",
                    preset != null ? "offline_preset_library" : __state?.VoiceStyleSource,
                    preset != null ? null : __state?.VoiceStyleFailureReason, preset?.Description);
                _speechLog?.LogInfo($"Stage3 ordinary display callback identity={identity} preset={preset?.EntryKey ?? "none"}");
                SpeechMvp.OnNpcReply(npcKey, text, reply?.Emotion, style, identity);
            }
        }); }
        finally { __state?.ExitScope(); }
    }

    public static Exception? OnNpcDisplayFinalizer(Exception? __exception, NpcDisplayPatchState? __state)
    {
        __state?.ExitScope();
        return __exception;
    }

    public static void OnPersuadeResponse(object __instance, object[] __args, MethodBase __originalMethod, string? __state)
    {
        if (_npcDisplayScopeDepth > 0)
        {
            _speechLog?.LogInfo("Stage3 persuade callback reentered AddNpcChatMessage; deferring TTS to its Postfix so the full voice_style is retained.");
            return;
        }
        try
        {
            var message = __args.FirstOrDefault(a => a?.GetType().FullName == "Game.Model.ChatMessage");
            if (message == null) return;
            var messageType = message.GetType();
            var textProperty = messageType.GetProperty("MessageText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var text = textProperty?.GetValue(message) as string;
            if (string.IsNullOrWhiteSpace(text)) return;
            var raw = messageType.GetProperty("NpcRawOutput")?.GetValue(message) as string;
            var marker = messageType.GetProperty("SystemMarker")?.GetValue(message) as string;
            var npcKey = GetPersuadeNpcKey(__instance);
            var observedRaw = __state ?? raw;
            var admitted = PersuadeSpeechPolicy.Resolve(npcKey, text, observedRaw, marker,
                __originalMethod.Name == "OnReceivePersuadeResponseNpc", GetPersuadeTopicId(__instance));
            if (admitted == null) return;
            var separated = admitted.Payload;
            var pending = StyleStore.Take(npcKey, admitted.ReplyContent);
            var style = separated?.VoiceStyle ?? pending?.Style;
            var preset = style == null ? admitted.Preset : null;
            style ??= preset?.Style;
            if (textProperty?.CanWrite == true)
            {
                var decodedText = admitted.Text;
                if (!string.Equals(decodedText, text, StringComparison.Ordinal)) textProperty.SetValue(message, decodedText);
            }
            text = admitted.Text;
            _speechLog?.LogInfo($"Stage3 persuade NPC response npc={npcKey} chars={text.Length} rawChars={observedRaw?.Length ?? 0} callback={__originalMethod.Name} preset={preset?.EntryKey ?? "none"}");
            var timestamp = messageType.GetProperty("MSTimestamp")?.GetValue(message)?.ToString();
            if (string.IsNullOrWhiteSpace(timestamp) || timestamp == "0") return;
            var identity = DisplayTurnIdentity.Create(npcKey, timestamp);
            if (identity == null) return;
            SpeechPanelData.RecordReply(identity, npcKey, text, SpeechTextFilter.RemoveParentheticals(text),
                admitted.Emotion ?? "normal", style, pending?.RawReply ?? observedRaw,
                preset != null ? "preset_matched" : style == null ? pending?.Status ?? separated?.VoiceStyleStatus ?? "unparseable_or_unavailable" : "valid",
                preset != null ? "offline_preset_library" : separated?.VoiceStyle != null ? separated.VoiceStyleSource : pending != null ? "pending_store" : null,
                preset != null ? null : separated?.VoiceStyleFailureReason ?? pending?.FailureReason, preset?.Description);
            _speechLog?.LogInfo($"Stage3 persuade display callback identity={identity}");
            if (_captureEnabled) Emit(new
            {
                kind = "persuade_reply_observation", runId = RunId, npcId = npcKey,
                displayIdentity = identity, callback = __originalMethod.Name,
                linkedRequestSeq = _lastRequestSeq,
                rawLength = observedRaw?.Length ?? 0,
                rawSha256 = observedRaw == null ? null : Hash(observedRaw),
                raw = _full && observedRaw != null ? Limit(RedactCredentialFields(observedRaw)) : null,
                text = _full ? Limit(text) : null,
                voiceStyleStatus = separated?.VoiceStyleStatus ?? "absent",
                voiceStyleFailureReason = separated?.VoiceStyleFailureReason,
                effectiveVoiceStyleSource = preset != null ? "offline_preset_library" : separated?.VoiceStyle != null ? separated.VoiceStyleSource : pending?.Source,
                effectiveVoiceStyleStatus = style == null ? "absent" : "valid",
                presetEntry = preset?.EntryKey,
                validStyle = _full ? style : null
            });
            SpeechMvp.OnNpcReply(npcKey, text, admitted.Emotion ?? "normal", style, identity);
        }
        catch (Exception e)
        {
            _speechLog?.LogWarning("Stage3 persuade response observer error: " + e.GetType().Name);
        }
    }

    public static void OnRequest(object[] __args)
    {
        Safe(() =>
        {
            var npc = __args.FirstOrDefault(a => a?.GetType().FullName == "Game.Model.NpcModel");
            var messages = __args.FirstOrDefault(a => a?.GetType().IsGenericType == true && a.GetType().GetGenericArguments().Any(t => t.FullName == "JNGame.Ainpc.Llm.LlmMessage"));
            var schema = __args.OfType<string>().LastOrDefault(s => s.Length > 64) ?? "";
            var fields = new List<object>();
            var messageList = messages as Il2CppSystem.Collections.Generic.IReadOnlyList<JNGame.Ainpc.Llm.LlmMessage>;
            if (messageList != null)
            {
                // IReadOnlyList<T> and IReadOnlyCollection<T> are separate generated wrappers
                // over the same IL2CPP object; the former supplies the indexer, the latter Count.
                var collection = new Il2CppSystem.Collections.Generic.IReadOnlyCollection<JNGame.Ainpc.Llm.LlmMessage>(messageList.Pointer);
                for (var i=0; i < Math.Min(collection.Count, 128); i++)
                {
                    var message = messageList[i];
                    if (message == null) continue;
                    var role = message.Role ?? "unknown";
                    var content = message.Content ?? "";
                    fields.Add(new { role, length=content.Length, sha256=Hash(content), content=_full ? Limit(content) : null });
                }
            }
            var argTypes = __args.Select((a,i) => new { index=i, type=a?.GetType().FullName ?? "null" }).ToArray();
            var providerDiagnostics = ReadOfficialProviderDiagnostics();
            _lastRequestSeq = Emit(new { kind="client_request_context", runId=RunId, npcId=NpcId(npc), mode="unknown", requestForInputSeq=_lastInputSeq, messagesCount=fields.Count, messagesType=messages?.GetType().FullName, messages=fields, schemaLength=schema.Length, schemaSha256=Hash(schema), schema=_full ? Limit(RedactCredentialFields(schema)) : null, providerDiagnostics, args=argTypes });
        });
    }

    public static void OnPersuadeResponsePrefix(object __instance, object[] __args, out string? __state)
    {
        __state = null;
        try
        {
            var message = __args.FirstOrDefault(a => a?.GetType().FullName == "Game.Model.ChatMessage");
            if (message == null) return;
            var type = message.GetType();
            var rawProperty = type.GetProperty("NpcRawOutput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var raw = rawProperty?.GetValue(message) as string;
            __state = raw;
            if (string.IsNullOrWhiteSpace(raw)) return;
            var separated = NpcReplyPayload.Separate(raw);
            if (separated == null) return;
            var fields = ExtractNpcReply(separated.GameJson);
            if (fields?.Content == null) return;
            var textProperty = type.GetProperty("MessageText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var text = textProperty?.GetValue(message) as string;
            if (text != null && textProperty?.CanWrite == true)
            {
                var decoded = StyleEnvelopeCodec.Decode(text);
                if (decoded.Status != "absent") textProperty.SetValue(message, decoded.Content);
            }
            if (rawProperty?.CanWrite == true) rawProperty.SetValue(message, separated.GameJson);
            if (separated.VoiceStyle != null)
            {
                var key = GetPersuadeNpcKey(__instance);
                StyleStore.Store(key, fields.Content, separated.VoiceStyle, Hash(raw), raw,
                    separated.VoiceStyleStatus, separated.VoiceStyleFailureReason, separated.VoiceStyleSource);
            }
        }
        catch (Exception e) { _speechLog?.LogWarning("Stage3 persuade response cleanup error: " + e.GetType().Name); }
    }

    private static string GetPersuadeNpcKey(object panel)
    {
        var npc = panel.GetType().GetProperty("_subscribedNpc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(panel);
        var key = NpcId(npc);
        if (key.StartsWith("npc:", StringComparison.Ordinal)) return key;
        var configId = panel.GetType().GetProperty("_npcId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(panel);
        var value = configId?.GetType().GetProperty("Value")?.GetValue(configId);
        return value is int id && id > 0 ? "npc:" + id : key;
    }

    private static string? GetPersuadeTopicId(object panel)
    {
        var id = panel.GetType().GetProperty("_topicId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(panel);
        return id?.GetType().GetProperty("Value")?.GetValue(id)?.ToString();
    }

    private static long EmitText(string kind, object npc, string text, string? raw=null)
    {
        var structured = kind == "npc_display" && !string.IsNullOrEmpty(raw) ? ExtractNpcReply(raw) : null;
        return Emit(new
        {
            kind, runId=RunId, npcId=NpcId(npc), linkedRequestSeq=kind == "npc_display" ? _lastRequestSeq : (long?)null,
            length=text.Length, sha256=Hash(text), text=Limit(text),
            rawLength=raw?.Length, rawSha256=raw == null ? null : Hash(raw),
            emotion=structured?.Emotion,
            structuredReply=_full ? structured : null
        });
    }

    private static void EmitRawReplyObservation(object npc, string raw, NpcReplyPayload.Separated separated,
        VoiceStyle? effectiveStyle = null, string? effectiveStyleSource = null, string? sourceRawSha256 = null)
    {
        if (!_captureEnabled) return;
        Emit(new
        {
            kind = "npc_reply_raw_observation", runId = RunId, npcId = NpcId(npc),
            linkedRequestSeq = _lastRequestSeq, rawLength = raw.Length, rawSha256 = Hash(raw),
            raw = _full ? Limit(raw) : null,
            voiceStyleStatus = separated.VoiceStyleStatus,
            voiceStyleJsonType = separated.VoiceStyleJsonType,
            voiceStyleFailureReason = separated.VoiceStyleFailureReason,
            effectiveVoiceStyleStatus = effectiveStyle == null ? "absent" : "valid",
            effectiveVoiceStyleSource = effectiveStyleSource,
            effectiveVoiceStyleSourceRawSha256 = sourceRawSha256,
            validStyle = _full ? effectiveStyle : null
        });
    }

    internal static void RecordTtsInstruction(string identity, string backend, VoiceStyle? style, string? instruction)
    {
        if (!_captureEnabled || !_full) return;
        Emit(new { kind = "tts_instruction_consumed", runId = RunId, displayIdentity = identity,
            backend, style, instruction = instruction ?? "" });
    }

    private static NpcReplyFields? ExtractNpcReply(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out _)) return null;
            VoiceStyle? voiceStyle = null;
            if (root.TryGetProperty("voice_style", out var style)) voiceStyle = VoiceStyle.Parse(style);
            var content = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var emotion = root.TryGetProperty("emotion", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var control = root.TryGetProperty("control", out var ctl) && ctl.ValueKind == JsonValueKind.String ? ctl.GetString() : null;
            bool? giveGift = null;
            string? giftItem = null;
            if (root.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.Object)
            {
                if (intent.TryGetProperty("give_gift", out var g) && g.ValueKind is JsonValueKind.True or JsonValueKind.False) giveGift = g.GetBoolean();
                if (intent.TryGetProperty("gift_item", out var item) && item.ValueKind == JsonValueKind.String) giftItem = item.GetString();
            }
            return new NpcReplyFields(content, emotion, control, giveGift, giftItem, voiceStyle, "");
        }
        catch { return null; }
    }

    private static bool IsOfficialTransport(object? transport)
    {
        try
        {
            if (transport == null) return false;
            var runtime = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Game")?.GetType("Game.Model.AinpcRuntime");
            var official = runtime?.GetProperty("Official", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (official == null) return false;
            var providerType = official.GetType();
            var officialTransport = providerType.GetProperty("_transport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(official)
                ?? providerType.GetField("_transport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(official);
            if (officialTransport == null) return false;
            var expected = NativePointer(officialTransport);
            var actual = NativePointer(transport);
            return expected.HasValue && actual.HasValue && expected.Value == actual.Value;
        }
        catch { return false; }
    }

    private static long? NativePointer(object? value)
    {
        try
        {
            var type = value?.GetType();
            var pointer = type?.GetProperty("Pointer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value)
                ?? type?.GetField("Pointer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value);
            return pointer switch { IntPtr p => p.ToInt64(), long n => n, ulong n => unchecked((long)n), _ => null };
        }
        catch { return null; }
    }

    private static object ReadOfficialProviderDiagnostics()
    {
        try
        {
            var runtime = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Game")?.GetType("Game.Model.AinpcRuntime");
            var official = runtime?.GetProperty("Official", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (official == null) return new { providerType = (string?)null, providerNativeType = (string?)null, providerPointer = (long?)null, transportType = (string?)null, transportNativeType = (string?)null, transportPointer = (long?)null };
            var type = official.GetType();
            var transport = type.GetProperty("_transport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(official)
                ?? type.GetField("_transport", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(official);
            var (providerNativeType, transportNativeType, nativeFields) = ReadNativeProviderFields(official);
            return new { providerType = type.FullName, providerNativeType, providerPointer = NativePointer(official), transportType = transport?.GetType().FullName, transportNativeType, transportPointer = NativePointer(transport), nativeFields };
        }
        catch (Exception e)
        {
            return new { providerType = (string?)null, providerPointer = (long?)null, transportType = (string?)null, transportPointer = (long?)null, error = e.GetType().Name };
        }
    }

    private static (string? providerType, string? transportType, object[] fields) ReadNativeProviderFields(object official)
    {
        try
        {
            var pointer = NativePointer(official);
            if (!pointer.HasValue) return (null, null, Array.Empty<object>());
            var nativeObject = new IntPtr(pointer.Value);
            var klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(nativeObject);
            var providerType = NativeClassName(klass);
            var fields = new List<object>();
            var transportType = (string?)null;
            var iterator = IntPtr.Zero;
            while (true)
            {
                var field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_fields(klass, ref iterator);
                if (field == IntPtr.Zero) break;
                var name = Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_name_(field) ?? "";
                if (name.Length == 0) continue;
                var value = Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_value_object(field, nativeObject);
                var valueType = value == IntPtr.Zero ? null : NativeClassName(Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(value));
                if (name.Contains("transport", StringComparison.OrdinalIgnoreCase)) transportType = valueType;
                fields.Add(new { name, valueType });
            }
            return (providerType, transportType, fields.ToArray());
        }
        catch (Exception e)
        {
            return ("native-inspection-error:" + e.GetType().Name, null, Array.Empty<object>());
        }
    }

    private static string? NativeClassName(IntPtr klass)
    {
        if (klass == IntPtr.Zero) return null;
        var ns = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_namespace_(klass);
        var name = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name_(klass);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string? ActiveTopicId(object npc)
    {
        try
        {
            var id = npc.GetType().GetProperty("ActiveTopicId")?.GetValue(npc);
            return id?.GetType().GetProperty("Value")?.GetValue(id)?.ToString();
        }
        catch { return null; }
    }

    private static string? DisplayMessageIdentity(object npc, string npcKey)
    {
        try
        {
            var messages = npc.GetType().GetProperty("ChatMessages", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(npc);
            if (messages == null) return null;
            var collectionType = messages.GetType();
            var count = (int)(collectionType.GetProperty("Count")?.GetValue(messages) ?? 0);
            var item = collectionType.GetProperty("Item") ?? collectionType.GetProperties().FirstOrDefault(property => property.GetIndexParameters().Length == 1);
            if (count < 1 || item == null) return null;
            var message = item.GetValue(messages, new object[] { count - 1 });
            if (message == null) return null;
            var messageType = message.GetType();
            var timestamp = messageType.GetProperty("MSTimestamp", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(message)?.ToString();
            if (string.IsNullOrWhiteSpace(timestamp) || timestamp == "0") return null;
            return DisplayTurnIdentity.Create(npcKey, timestamp);
        }
        catch (Exception e)
        {
            _speechLog?.LogWarning("Stage3 could not read native display ChatMessage identity: " + e.GetType().Name);
            return null;
        }
    }

    private static bool SynchronizeLastNpcRawOutput(object npc, string displayText, string pluginRawOutput, out string? actualRawOutput)
    {
        actualRawOutput = null;
        try
        {
            var messages = npc.GetType().GetProperty("ChatMessages", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(npc);
            if (messages == null) return false;
            var collectionType = messages.GetType();
            var count = (int)(collectionType.GetProperty("Count")?.GetValue(messages) ?? 0);
            var item = collectionType.GetProperty("Item") ?? collectionType.GetProperties().FirstOrDefault(property => property.GetIndexParameters().Length == 1);
            if (count < 1 || item == null) return false;
            var message = item.GetValue(messages, new object[] { count - 1 });
            if (message == null) return false;
            var messageType = message.GetType();
            var messageText = messageType.GetProperty("MessageText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(message) as string;
            if (!string.Equals(messageText, displayText, StringComparison.Ordinal)) return false;
            var rawOutput = messageType.GetProperty("NpcRawOutput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (rawOutput?.CanWrite != true) return false;
            rawOutput.SetValue(message, pluginRawOutput);
            actualRawOutput = rawOutput.GetValue(message) as string;
            return string.Equals(actualRawOutput, pluginRawOutput, StringComparison.Ordinal);
        }
        catch (Exception e)
        {
            _speechLog?.LogWarning("Stage3 ChatMessage.NpcRawOutput synchronization error: " + e.GetType().Name);
            return false;
        }
    }

    private static string NpcId(object? npc)
    {
        try
        {
            var type = npc?.GetType();
            // NpcCfgId.Value is the game's numeric NPC configuration ID, not the save entity ID.
            var configId = type?.GetProperty("NpcCfgId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(npc);
            var configValue = configId?.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(configId);
            if (configValue is int id && id > 0) return "npc:" + id;
            var pointer = type?.GetProperty("Pointer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(npc)
                ?? type?.GetField("Pointer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(npc);
            return pointer?.ToString() is { Length: > 0 } value ? "ptr:" + value : "unknown";
        }
        catch { return "unknown"; }
    }
    private static string RedactCredentialFields(string source)
    {
        if (source.Length == 0) return source;
        try
        {
            using var document = JsonDocument.Parse(source);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteRedacted(document.RootElement, writer);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch { return "[omitted: schema was not valid JSON]"; }
    }
    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    var key = property.Name.ToLowerInvariant();
                    if (new[] { "authorization", "api_key", "apikey", "token", "secret", "cookie", "password" }.Any(key.Contains)) writer.WriteStringValue("[REDACTED]");
                    else WriteRedacted(property.Value, writer);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array: writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteRedacted(item, writer); writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }
    private static string Limit(string s) => s.Length <= _maxChars ? s : s[.._maxChars] + "[TRUNCATED]";
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static void Safe(Action action) { try { action(); } catch (Exception e) { try { _log?.LogWarning("Stage2A observer error: " + e.GetType().Name); } catch { } } }
    private static long Emit(object record)
    {
        var seq = Interlocked.Increment(ref _seq);
        var json = JsonSerializer.Serialize(new { utc=DateTime.UtcNow, seq, pid=Environment.ProcessId, record });
        if (!Queue.TryAdd(json)) _log?.LogWarning("Stage2A event queue full; event dropped.");
        _log?.LogInfo($"Stage2A seq={seq} eventBytes={Encoding.UTF8.GetByteCount(json)} eventSha256={Hash(json)} file={Path.Combine(_runDir, "events.jsonl")}");
        return seq;
    }
    private static void Writer()
    {
        try { using var writer = new StreamWriter(Path.Combine(_runDir, "events.jsonl"), append:true, new UTF8Encoding(false)); foreach (var line in Queue.GetConsumingEnumerable()) { writer.WriteLine(line); writer.Flush(); } }
        catch (Exception e) { try { _log?.LogError("Stage2A writer failed: " + e.GetType().Name); } catch { } }
    }
}

internal static class PluginInfo
{
    public const string Guid = "org.a1indextts.mod";
    public const string Name = "A1-TTS-Mod";
    public const string Version = "0.7.8";
}
