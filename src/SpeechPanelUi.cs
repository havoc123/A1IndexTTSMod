using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Atom.UI.Runtime.Components;
using Il2CppInterop.Runtime;
using LocalModManager.Abstractions;
using UnityEngine;

namespace A1IndexTTSMod;

/// <summary>Small, independent IMGUI overlay bound only to the live NPC dialogue panel.</summary>
internal sealed class SpeechPanelUi : MonoBehaviour
{
    private static SpeechPanelUi? _instance;
    private static ManualLogSource? _log;
    private static ConfigEntry<int>? _volume;
    private static ConfigEntry<bool>? _autoRead;
    private static ConfigEntry<bool>? _featureEnabled;
    private static ConfigEntry<int>? _interfaceScale;
    private static ConfigEntry<int>? _lastTab;
    private static FeaturePluginState _featureState;
    private static string _featureStatus = "Unavailable";
    private static bool _autoReadValue = true;
    private static object? _dialoguePanel;
    private static string? _npcId;
    private static string _npcName = "当前角色";
    private bool _open;
    private int _tab;
    private Rect _window = new(0, 0, 650, 620);
    private Rect _scroll;
    private Vector2 _scrollPosition;
    private Vector2 _dataScrollPosition;
    private bool _positioned;
    private bool _dragging;
    private bool _pointerCaptured;
    private Vector2 _dragPointerStart;
    private Vector2 _dragWindowStart;
    private Rect _entryRect;
    private bool _entryAvailable;
    private GameObject? _entryVisual;
    private int _entrySourceInstanceId;
    private int _lastScreenWidth;
    private int _lastScreenHeight;
    private float _lastScale;
    private Rect _safeLogical;
    private bool _loggedUiMetrics;
    private bool _saveNpcSetting = true;
    private string? _selectedReferencePath;
    private NpcVoiceResolver.ReferenceChoice[] _referenceChoices = Array.Empty<NpcVoiceResolver.ReferenceChoice>();
    private string _referenceFilter = "";
    private bool _referenceDropdownOpen;
    private Vector2 _referenceDropdownScroll;
    private NpcVoiceResolver.ReferenceChoice[] _matchingReferences = Array.Empty<NpcVoiceResolver.ReferenceChoice>();
    private bool _referenceDropdownVisible;
    private string _uiMessage = "";
    private SpeechPanelTurn? _selectedTurn;
    private int _dataLayer;
    private bool _showRawReply;
    private bool _showAllTurns;
    private bool? _pendingSpeechEnabled;
    private static Texture2D? _paperTexture;
    private static Texture2D? _fieldTexture;
    private static Texture2D? _borderTexture;
    private static GUIStyle? _panelStyle;
    private static GUIStyle? _cardStyle;
    private static GUIStyle? _titleStyle;
    private static GUIStyle? _subtitleStyle;
    private static GUIStyle? _bodyStyle;
    private static GUIStyle? _mutedStyle;
    private static GUIStyle? _sectionStyle;
    private static GUIStyle? _buttonStyle;
    private static GUIStyle? _primaryButtonStyle;
    private static GUIStyle? _tabStyle;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public IntPtr Filter;
        public IntPtr CustomFilter;
        public int MaxCustFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public IntPtr InitialDir;
        public IntPtr Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public IntPtr DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr Reserved;
        public int ReservedValue;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetOpenFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOpenFileName(ref OpenFileName openFileName);
    [DllImport("comdlg32.dll", SetLastError = true)] private static extern uint CommDlgExtendedError();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    public SpeechPanelUi(IntPtr ptr) : base(ptr) { }

    internal static void Configure(ManualLogSource log, ConfigEntry<int> volume,
        ConfigEntry<bool> autoRead, ConfigEntry<bool> featureEnabled,
        ConfigEntry<int> interfaceScale, ConfigEntry<int> lastTab)
    {
        _log = log;
        _volume = volume;
        _autoRead = autoRead;
        _featureEnabled = featureEnabled;
        _interfaceScale = interfaceScale;
        _lastTab = lastTab;
        _interfaceScale.Value = Mathf.Clamp(_interfaceScale.Value, 75, 175);
        _lastTab.Value = Mathf.Clamp(_lastTab.Value, 0, 2);
        if (_instance != null) _instance._tab = _lastTab.Value;
        _autoReadValue = autoRead.Value;
    }

    internal static void SetFeatureStatus(FeaturePluginState state, string message)
    {
        _featureState = state;
        _featureStatus = message ?? "";
    }

    internal static void OnDialogueShown(object instance)
    {
        try
        {
            EnsureInstance();
            _dialoguePanel = instance;
            _npcId = ReadNpcId(instance);
            _npcName = ReadNpcName(instance) ?? NpcVoiceResolver.GetNpcName(_npcId);
            _instance?.LoadVoiceSelection();
            _instance?.RefreshEntryPosition();
        }
        catch (Exception e) { _log?.LogWarning("Speech panel context update failed: " + e.GetType().Name); }
    }

    internal static void OnDialogueHidden(object instance)
    {
        if (_instance != null)
        {
            _instance._open = false;
            _instance._pendingSpeechEnabled = null;
            _instance._dragging = false;
            _instance._pointerCaptured = false;
            _instance._referenceDropdownOpen = false;
        }
        _npcId = null;
        _dialoguePanel = null;
        if (_instance != null)
        {
            _instance.DestroyEntryVisual();
            _instance._entryAvailable = false;
            _instance.gameObject.SetActive(false);
        }
    }

    internal static void OnDialogueData(object panel, object? value)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var nested = value?.GetType().GetProperty("Value", flags)?.GetValue(value) ??
                     value?.GetType().GetField("Value", flags)?.GetValue(value) ?? value;
        var candidate = nested?.ToString()?.Trim();
        if (!int.TryParse(candidate, out var id) || id <= 0) return;
        _dialoguePanel = panel;
        EnsureInstance();
        if (_instance != null) _instance.gameObject.SetActive(true);
        _npcId = id.ToString();
        _npcName = NpcVoiceResolver.GetNpcName(_npcId);
        _instance?.LoadVoiceSelection();
        _instance?.RefreshEntryPosition();
        _log?.LogInfo("Speech panel bound to NPC " + _npcId + " from dialogue SetData.");
    }

    private static void EnsureInstance()
    {
        if (_instance != null) { _instance.gameObject.SetActive(true); return; }
        var plugin = Plugin.Instance;
        if (plugin == null) return;
        var host = new GameObject("A1IndexTTS.SpeechPanel");
        DontDestroyOnLoad(host);
        _instance = plugin.AddComponent<SpeechPanelUi>();
        if (_instance == null) { UnityEngine.Object.Destroy(host); throw new InvalidOperationException("Could not attach speech panel component."); }
        _instance._tab = _lastTab?.Value ?? 0;
        if (_instance.gameObject != host) _instance.transform.SetParent(host.transform, false);
        _instance.gameObject.SetActive(true);
    }

    private void OnGUI()
    {
        SetFeatureStatus(SpeechMvp.State, SpeechMvp.StatusMessage);
        if (_npcId == null || !_entryAvailable) return;
        EnsureTheme();
        var scale = GetUiScale();
        var coordinateScale = scale * GetScreenCoordinateRatio();
        if (!_loggedUiMetrics)
        {
            _loggedUiMetrics = true;
            _log?.LogInfo($"Speech panel UI metrics: Screen={Screen.width}x{Screen.height}, desktop={GetSystemMetrics(0)}x{GetSystemMetrics(1)}, safeArea={Screen.safeArea}, UnityDpi={Screen.dpi}, effectiveScale={scale:0.00}, coordinateRatio={GetScreenCoordinateRatio():0.00}, configured={_interfaceScale?.Value ?? 100}%.");
        }
        var priorMatrix = GUI.matrix;
        var priorEnabled = GUI.enabled;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
        try
        {
            if (_entryVisual != null) _entryVisual.SetActive(_entryAvailable);
            if (!_open) return;
            UpdateLogicalSafeArea(coordinateScale);
            if (!_positioned)
            {
                _window.width = Mathf.Max(360, Mathf.Min(650, _safeLogical.width - 16));
                _window.height = Mathf.Max(300, Mathf.Min(620, _safeLogical.height - 16));
                _window.x = Mathf.Clamp(_entryRect.x - _window.width - 10, _safeLogical.x, _safeLogical.xMax - _window.width);
                _window.y = Mathf.Clamp(_entryRect.y - _window.height + _entryRect.height, _safeLogical.y, _safeLogical.yMax - _window.height);
                _positioned = true;
            }
            _window.width = Mathf.Min(_window.width, _safeLogical.width - 16);
            _window.height = Mathf.Min(_window.height, _safeLogical.height - 16);
            _window.x = Mathf.Clamp(_window.x, _safeLogical.x, Mathf.Max(_safeLogical.x, _safeLogical.xMax - _window.width));
            _window.y = Mathf.Clamp(_window.y, _safeLogical.y, Mathf.Max(_safeLogical.y, _safeLogical.yMax - _window.height));

            GUI.DrawTexture(_window, _borderTexture ?? Texture2D.whiteTexture);
            var panelInner = new Rect(_window.x + 1.5f, _window.y + 1.5f, _window.width - 3f, _window.height - 3f);
            GUI.Box(panelInner, "", _panelStyle);
            GUI.Label(new Rect(_window.x + 20, _window.y + 9, _window.width - 76, 28), "语音设置 · " + _npcName, _titleStyle);
            if (GUI.Button(new Rect(_window.xMax - 48, _window.y + 8, 34, 30), "×", _buttonStyle))
            {
                _open = false;
                _pendingSpeechEnabled = null;
            }
            var confirming = _pendingSpeechEnabled.HasValue;
            GUI.enabled = priorEnabled && !confirming;
            {
                _scroll = new Rect(_window.x + 16, _window.y + 48, _window.width - 32, _window.height - 62);
                GUILayout.BeginArea(_scroll);
                _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Width(_scroll.width), GUILayout.Height(_scroll.height));
                DrawWindow(GetInstanceID());
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                if (!confirming) HandleWindowDrag(scale);
            }
            GUI.enabled = priorEnabled;
            if (confirming && _open) DrawSpeechConfirmation();
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                if (_pendingSpeechEnabled.HasValue) _pendingSpeechEnabled = null;
                else _open = false;
                Event.current.Use();
            }
        }
        finally
        {
            GUI.enabled = priorEnabled;
            GUI.matrix = priorMatrix;
        }
    }

    private void LateUpdate() => RefreshEntryPosition();

    internal static bool ShouldBlockGameInput()
    {
        if (_instance == null || !_instance.gameObject.activeInHierarchy || !Application.isFocused) return false;
        // Keyboard shortcuts stay blocked regardless of where the mouse is located.
        return _instance._open;
    }

    internal static bool ShouldBlockGamePointer()
    {
        if (!ShouldBlockGameInput())
        {
            if (_instance != null) _instance._pointerCaptured = false;
            return false;
        }
        var panel = _instance!;
        var mouse = Input.mousePosition;
        var coordinateScale = GetUiScale() * GetScreenCoordinateRatio();
        var point = new Vector2(mouse.x / coordinateScale, (Screen.height - mouse.y) / coordinateScale);
        var inside = panel._window.Contains(point);
        // A press started in the overlay owns its drag and release, even outside the window.
        // Keep capture through the release frame so game UI never receives half a click.
        if (inside && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)))
            panel._pointerCaptured = true;
        if (panel._pointerCaptured &&
            !Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2) &&
            !Input.GetMouseButtonUp(0) && !Input.GetMouseButtonUp(1) && !Input.GetMouseButtonUp(2))
            panel._pointerCaptured = false;
        return inside || panel._pointerCaptured || panel._dragging;
    }

    private void RefreshEntryPosition()
    {
        _entryAvailable = false;
        if (_dialoguePanel == null || _npcId == null) return;
        try
        {
            var panel = _dialoguePanel;
            var dialogueInput = GetMember(panel, "DialogueInput");
            var commonInput = GetMember(dialogueInput, "InputFieldMain");
            var submitButton = GetMember(commonInput, "BtnInputFieldSubmit");
            var submitComponent = submitButton as Component;
            var submitRect = submitComponent as RectTransform;
            if (submitRect == null && submitComponent != null) submitRect = submitComponent.GetComponent<RectTransform>();
            if (submitRect == null || submitComponent == null) return;
            EnsureEntryVisual(submitComponent, submitRect);
            if (_entryVisual == null) return;
            var entryTransform = _entryVisual.transform;
            var entryRectTransform = entryTransform as RectTransform ?? entryTransform.GetComponent<RectTransform>();
            if (entryRectTransform == null) return;
            var worldCenter = entryRectTransform.TransformPoint(entryRectTransform.rect.center);
            var screen = RectTransformUtility.WorldToScreenPoint(null, worldCenter);
            var scale = GetUiScale();
            var coordinateScale = scale * GetScreenCoordinateRatio();
            var corners = new Vector3[4];
            entryRectTransform.GetWorldCorners(corners);
            var screenA = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            var screenB = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            var width = Mathf.Abs(screenB.x - screenA.x) / coordinateScale;
            var height = Mathf.Abs(screenB.y - screenA.y) / coordinateScale;
            var x = screenA.x / coordinateScale;
            var y = (Screen.height - screenB.y) / coordinateScale;
            UpdateLogicalSafeArea(coordinateScale);
            _entryRect = new Rect(Mathf.Clamp(x, _safeLogical.x, _safeLogical.xMax - width),
                Mathf.Clamp(y, _safeLogical.y, _safeLogical.yMax - height), width, height);
            _entryAvailable = true;
        }
        catch (Exception e) { _log?.LogWarning("Could not follow the game's submit button: " + e.GetType().Name); }
    }

    private void EnsureEntryVisual(Component submitButton, RectTransform submitRect)
    {
        var sourceId = submitButton.GetInstanceID();
        if (_entryVisual != null && _entrySourceInstanceId == sourceId) return;
        DestroyEntryVisual();
        var visual = UnityEngine.Object.Instantiate(submitButton.gameObject, submitButton.transform.parent);
        visual.name = "A1IndexTTS.SpeechSettingsButton";
        var rect = visual.transform as RectTransform ?? visual.GetComponent<RectTransform>();
        if (rect == null)
        {
            UnityEngine.Object.Destroy(visual);
            return;
        }
        var layoutElement = visual.GetComponent<UnityEngine.UI.LayoutElement>() ??
                            visual.AddComponent<UnityEngine.UI.LayoutElement>();
        layoutElement.ignoreLayout = true;
        rect.anchorMin = submitRect.anchorMin;
        rect.anchorMax = submitRect.anchorMax;
        rect.pivot = submitRect.pivot;
        rect.sizeDelta = submitRect.sizeDelta + new Vector2(36f, 0f);
        rect.localScale = submitRect.localScale;
        rect.anchoredPosition = submitRect.anchoredPosition + new Vector2(
            submitRect.rect.width / 2f + rect.rect.width / 2f + 8f, 0f);
        rect.SetAsLastSibling();
        try { ConfigureEntryButton(visual); }
        catch
        {
            UnityEngine.Object.Destroy(visual);
            throw;
        }
        _entryVisual = visual;
        _entrySourceInstanceId = sourceId;
        _log?.LogInfo("Speech settings entry cloned the game's submit-button prefab for matching size and visual style.");
    }

    private void ConfigureEntryButton(GameObject visual)
    {
        // Game UIButton derives from Selectable, not UnityEngine.UI.Button. Base Component
        // wrappers also cannot expose UIText.text through managed GetType() reflection.
        var button = visual.GetComponent<UIButton>();
        if (button == null) throw new InvalidOperationException("Cloned submit control has no game UIButton.");
        button.OnClick = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)TogglePanel);
        button.OnDoubleClick = null;
        button.OnClickPointerPos = null;
        button.OnBtnDown = null;
        button.OnBtnUp = null;
        button.OnLongTouch = null;
        button.OnLongTouchRepeat = null;
        button.OnRightClick = null;
        button.enabled = true;
        button.SetInteractable(true);
        var navigation = button.navigation;
        navigation.mode = UnityEngine.UI.Navigation.Mode.None;
        button.navigation = navigation;
        var labels = visual.GetComponentsInChildren<TMPro.TMP_Text>(true);
        if (labels.Length == 0) throw new InvalidOperationException("Cloned submit control has no TMP label.");
        foreach (var label in labels)
        {
            var gameText = label.TryCast<UIText>();
            if (gameText != null) gameText.m_LocalizationId = 0;
            label.text = "语音设置";
            label.raycastTarget = false;
        }
        _log?.LogInfo($"Speech settings entry configured: label={labels[0].text}, interactable={button.interactable}, nativeClick={button.OnClick != null}.");
    }

    private void TogglePanel()
    {
        _open = !_open;
        _pendingSpeechEnabled = null;
        _log?.LogInfo("Speech settings native button: panel " + (_open ? "opened." : "closed."));
    }

    private void DestroyEntryVisual()
    {
        if (_entryVisual != null) UnityEngine.Object.Destroy(_entryVisual);
        _entryVisual = null;
        _entrySourceInstanceId = 0;
    }

    private static object? GetMember(object? target, string name)
    {
        if (target == null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
            if (property != null) return property.GetValue(target);
            var field = type.GetField(name, flags | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target);
        }
        return null;
    }

    private void DrawWindow(int id)
    {
        var oldContent = GUI.contentColor;
        GUI.contentColor = new Color(.93f, .89f, .80f, 1f);
        GUILayout.Label("本地语音 · 情感与说话方式继续来自游戏本轮响应", _subtitleStyle);
        GUILayout.Space(7);
        GUILayout.BeginHorizontal();
        GUILayout.Label("界面缩放", _mutedStyle, GUILayout.Width(82));
        if (GUILayout.Button("−", _buttonStyle, GUILayout.Width(40))) ChangeScale(-10);
        GUILayout.Label((_interfaceScale?.Value ?? 100) + "%", _mutedStyle, GUILayout.Width(52));
        if (GUILayout.Button("＋", _buttonStyle, GUILayout.Width(40))) ChangeScale(10);
        if (GUILayout.Button("复位", _buttonStyle, GUILayout.Width(58))) { _interfaceScale!.Value = 100; SaveConfig(); _positioned = false; }
        GUILayout.EndHorizontal();
        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        if (GUILayout.Toggle(_tab == 0, "音色与参数", _tabStyle)) SetTab(0);
        if (GUILayout.Toggle(_tab == 1, "最近语音", _tabStyle)) SetTab(1);
        if (GUILayout.Toggle(_tab == 2, "本轮数据", _tabStyle)) SetTab(2);
        GUILayout.EndHorizontal();
        GUILayout.Space(12);
        switch (_tab)
        {
            case 0: DrawVoiceTab(); break;
            case 1: DrawRecentTab(); break;
            default: DrawDataTab(); break;
        }
        if (!string.IsNullOrWhiteSpace(_uiMessage))
        {
            GUILayout.Space(8);
            GUILayout.Label(_uiMessage, _mutedStyle);
        }
        GUI.contentColor = oldContent;
    }

    private void DrawSpeechConfirmation()
    {
        if (!_pendingSpeechEnabled.HasValue) return;
        var enable = _pendingSpeechEnabled.Value;
        var width = Mathf.Min(430, _window.width - 40);
        var height = 176f;
        var dialog = new Rect(_window.center.x - width / 2, _window.center.y - height / 2, width, height);
        GUI.DrawTexture(dialog, _borderTexture ?? Texture2D.whiteTexture);
        var inner = new Rect(dialog.x + 1.5f, dialog.y + 1.5f, dialog.width - 3, dialog.height - 3);
        GUI.Box(inner, "", _panelStyle);
        GUI.Label(new Rect(dialog.x + 18, dialog.y + 14, width - 36, 28), enable ? "确认启用 Mod 语音功能？" : "确认关闭 Mod 语音功能？", _sectionStyle);
        GUI.Label(new Rect(dialog.x + 18, dialog.y + 48, width - 36, 66), enable
            ? "开启后，新收到的 NPC 回复会提交给当前配置的本地 TTS 服务合成并播放。"
            : "关闭后将停止当前语音、取消待合成任务，并关闭由本 Mod 启动的 audio.cpp TTS 服务。", _bodyStyle);
        var buttonWidth = (width - 46) / 2;
        if (GUI.Button(new Rect(dialog.x + 18, dialog.yMax - 48, buttonWidth, 32), "取消", _buttonStyle)) _pendingSpeechEnabled = null;
        if (GUI.Button(new Rect(dialog.x + 28 + buttonWidth, dialog.yMax - 48, buttonWidth, 32), enable ? "确认启用" : "确认关闭", _primaryButtonStyle))
        {
            _pendingSpeechEnabled = null;
            Plugin.Instance?.SetEnabled(enable);
        }
    }

    private void DrawVoiceTab()
    {
        GUILayout.Label("当前角色 · " + _npcName, _sectionStyle);
        GUILayout.Label("播放服务 · " + _featureState + (string.IsNullOrWhiteSpace(_featureStatus) ? "" : " · " + _featureStatus), _mutedStyle);
        var featureEnabled = _featureEnabled;
        if (featureEnabled != null)
        {
            var enabled = GUILayout.Toggle(featureEnabled.Value, "启用 Mod 语音功能", GUI.skin.toggle);
            if (enabled != featureEnabled.Value)
            {
                _pendingSpeechEnabled = enabled;
            }
        }
        if (_autoRead != null)
        {
            var auto = GUILayout.Toggle(_autoReadValue, "自动朗读新回复", GUI.skin.toggle);
            if (auto != _autoReadValue)
            {
                _autoReadValue = auto;
                Plugin.Instance?.SetAutoRead(auto);
            }
        }
        if (_volume != null)
        {
            GUILayout.Label("语音音量　" + _volume.Value + "%", _sectionStyle);
            var value = Mathf.RoundToInt(GUILayout.HorizontalSlider(_volume.Value, 0, 100));
            if (value != _volume.Value) Plugin.Instance?.SetPanelVolume(value, false);
            if (Event.current.type == EventType.MouseUp) Plugin.Instance?.SetPanelVolume(value, true);
        }
        GUILayout.Space(10);
        if (_npcId == null) GUILayout.Label("音色状态：角色身份不可用，不能执行角色绑定操作。");
        else
        {
            DrawReferenceControls();
        }
        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("停止当前语音", _buttonStyle)) SpeechMvp.StopFromPanel();
        GUILayout.EndHorizontal();
    }

    private void DrawReferenceControls()
    {
        if (Event.current.type == EventType.Layout)
        {
            var query = _referenceFilter.Trim();
            _matchingReferences = query.Length == 0 ? Array.Empty<NpcVoiceResolver.ReferenceChoice>() :
                _referenceChoices.Where(choice => choice.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            _referenceDropdownVisible = _referenceDropdownOpen;
        }
        var npcKey = "npc:" + _npcId;
        var effective = NpcVoiceResolver.Resolve(npcKey);
        GUILayout.BeginVertical(_cardStyle);
        GUILayout.Label("当前 NPC 参考音", _sectionStyle);
        GUILayout.Label("当前已应用：" + (effective.Skip ? "本地策略跳过 · " : "") +
            (effective.Path == null ? effective.Label : NpcVoiceResolver.GetReferenceLabel(effective.Path)), _mutedStyle);
        var pendingLabel = _selectedReferencePath == null ? "保持当前音色" :
            _referenceChoices.FirstOrDefault(choice => string.Equals(choice.Path, _selectedReferencePath, StringComparison.OrdinalIgnoreCase)).Label ?? Path.GetFileName(_selectedReferencePath);
        GUILayout.Label("待应用选择：" + pendingLabel, _bodyStyle);
        GUILayout.Space(5);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("导入 WAV…", _buttonStyle)) ImportReference();
        if (GUILayout.Button("播放参考音", _buttonStyle))
        {
            var path = _selectedReferencePath ?? effective.Path;
            _uiMessage = path == null ? "当前没有可播放的 WAV 参考音。" :
                SpeechMvp.PlayReference(path) ? "正在播放参考音；播放不会改变 NPC 音色绑定。" : "参考音播放失败。";
        }
        if (GUILayout.Button("用最近一句试听", _buttonStyle))
        {
            var recent = SpeechPanelData.GetRecent(npcKey).FirstOrDefault(turn => !string.IsNullOrWhiteSpace(turn.SpokenText));
            _uiMessage = recent == null ? "本次会话还没有可用于试听的 NPC 回复。" :
                SpeechMvp.PreviewTurn(recent, _selectedReferencePath) ? "已提交本地试听；不调用 LLM，也不修改 NPC 绑定。" : "试听未启动：请确认 Mod 语音功能和本地 TTS 服务已启用。";
        }
        GUILayout.EndHorizontal();
        GUILayout.Label("筛选参考音库", _sectionStyle);
        GUILayout.BeginHorizontal();
        GUILayout.Label("筛选", _mutedStyle, GUILayout.Width(42));
        var filter = GUILayout.TextField(_referenceFilter, GUILayout.MinWidth(110));
        if (filter != _referenceFilter)
        {
            _referenceFilter = filter;
            _referenceDropdownOpen = false;
            _referenceDropdownScroll = Vector2.zero;
        }
        GUILayout.EndHorizontal();
        if (_matchingReferences.Length > 0)
        {
            var matching = _matchingReferences;
            if (matching.Length > 0)
            {
                if (GUILayout.Button("选用匹配音色：" + matching[0].Label, _buttonStyle, GUILayout.ExpandWidth(true)))
                    _selectedReferencePath = matching[0].Path;
                if (matching.Length > 1)
                {
                    _referenceDropdownOpen = GUILayout.Toggle(_referenceDropdownOpen,
                        "其他匹配音色 " + (matching.Length - 1) + " 项　" + (_referenceDropdownOpen ? "▴" : "▾"), _buttonStyle);
                    if (_referenceDropdownVisible)
                    {
                        _referenceDropdownScroll = GUILayout.BeginScrollView(_referenceDropdownScroll, GUILayout.Height(150));
                        foreach (var choice in matching.Skip(1))
                            if (GUILayout.Button(choice.Label, _buttonStyle))
                                _selectedReferencePath = choice.Path;
                        GUILayout.EndScrollView();
                    }
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(_referenceFilter)) GUILayout.Label("没有匹配的 WAV。", _mutedStyle);
        else GUILayout.Label("输入 NPC 名字或部分编号筛选参考音。", _mutedStyle);
        var save = GUILayout.Toggle(_saveNpcSetting, "保存该 NPC 的音色设置");
        _saveNpcSetting = save;
        GUILayout.Label(save ? "应用后跨游戏重启保存，仅影响当前 NPC。" : "取消勾选后仅本次游戏会话使用。", _mutedStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("应用音色", _primaryButtonStyle))
        {
            try
            {
                if (_selectedReferencePath == null) _uiMessage = "请先选择一个可用的本地 WAV 参考音。";
                else { NpcVoiceResolver.ApplyOverride(npcKey, _selectedReferencePath, _saveNpcSetting); _uiMessage = _saveNpcSetting ? "已保存该 NPC 的参考音，下一次合成生效。" : "参考音已在本次会话临时应用。"; }
            }
            catch (Exception e) { _uiMessage = "应用失败：" + e.Message; }
        }
        if (GUILayout.Button("恢复角色默认", _buttonStyle))
        {
            NpcVoiceResolver.ResetOverride(npcKey);
            LoadVoiceSelection();
            _uiMessage = "已移除该 NPC 的临时与持久覆盖，恢复本地默认发声策略。";
        }
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private void ImportReference()
    {
        try
        {
            var source = ChooseWavFile();
            if (source == null) return;
            _selectedReferencePath = NpcVoiceResolver.ImportWav(source);
            _referenceChoices = NpcVoiceResolver.GetAvailableReferences();
            _uiMessage = "WAV 已验证并复制到 Mod 音色库；源文件保留不动。";
        }
        catch (Exception e) { _uiMessage = "导入失败：" + e.Message; }
    }

    private static string? ChooseWavFile()
    {
        const int maxPathChars = 32768;
        IntPtr filter = IntPtr.Zero;
        IntPtr title = IntPtr.Zero;
        IntPtr file = IntPtr.Zero;
        try
        {
            filter = Marshal.StringToHGlobalUni("WAV 音频 (*.wav)\0*.wav\0所有文件 (*.*)\0*.*\0\0");
            title = Marshal.StringToHGlobalUni("导入 NPC 参考音 WAV");
            file = Marshal.AllocHGlobal(maxPathChars * sizeof(char));
            Marshal.WriteInt16(file, 0);
            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = GetForegroundWindow(),
                Filter = filter,
                FilterIndex = 1,
                File = file,
                MaxFile = maxPathChars,
                Title = title,
                Flags = 0x00000008 | 0x00001000 | 0x00000800 | 0x00080000
            };
            if (GetOpenFileName(ref dialog)) return Marshal.PtrToStringUni(dialog.File);
            var error = CommDlgExtendedError();
            if (error != 0) throw new InvalidOperationException($"Windows 文件对话框错误 0x{error:X4}");
            return null;
        }
        finally
        {
            if (file != IntPtr.Zero) Marshal.FreeHGlobal(file);
            if (title != IntPtr.Zero) Marshal.FreeHGlobal(title);
            if (filter != IntPtr.Zero) Marshal.FreeHGlobal(filter);
        }
    }

    private void LoadVoiceSelection()
    {
        _referenceChoices = NpcVoiceResolver.GetAvailableReferences();
        _selectedReferencePath = _npcId == null ? null :
            NpcVoiceResolver.GetOverride("npc:" + _npcId) ?? NpcVoiceResolver.Resolve("npc:" + _npcId).Path;
        _uiMessage = "";
    }

    private void DrawRecentTab()
    {
        var npcKey = "npc:" + _npcId;
        _showAllTurns = GUILayout.Toggle(_showAllTurns, "显示所有 NPC 的最近语音");
        var recent = SpeechPanelData.GetRecent(_showAllTurns ? null : npcKey);
        GUILayout.Label($"本次游戏会话 · {recent.Count} 条（上限 20 条 / 音频 50 MiB）", _mutedStyle);
        if (recent.Count == 0)
        {
            GUILayout.Space(12);
            GUILayout.Label("当前角色在本次游戏会话中还没有结构化 NPC 回复。", _bodyStyle);
            return;
        }
        foreach (var turn in recent)
        {
            GUILayout.BeginVertical(_cardStyle);
            GUILayout.Label(turn.CreatedAt.ToLocalTime().ToString("HH:mm:ss") + " · " + NpcVoiceResolver.GetNpcName(turn.NpcKey) + " · " + turn.TtsStatus, _sectionStyle);
            GUILayout.Label(turn.DisplayText, _bodyStyle);
            var audioAvailable = !string.IsNullOrWhiteSpace(turn.AudioPath) && File.Exists(turn.AudioPath);
            GUILayout.Label("参考音：" + (turn.ReferenceLabel ?? "尚未请求") + " · 情感：" + (turn.Emotion ?? "未观测") +
                (turn.VoiceStyleJson == null ? "" : " · voice_style 已捕获") + (audioAvailable ? $" · WAV {turn.AudioBytes / 1024} KiB" : " · 无可重播 WAV"), _mutedStyle);
            if (!string.IsNullOrWhiteSpace(turn.Error)) GUILayout.Label(turn.Error, _mutedStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(audioAvailable ? "重播原音频" : "音频已淘汰", audioAvailable ? _buttonStyle : GUI.skin.button) && audioAvailable)
                _uiMessage = SpeechMvp.Replay(turn) ? "正在重播原 WAV；不发起 LLM 或 TTS 请求。" : "无法重播：音频已淘汰或 Mod 功能已关闭。";
            if (GUILayout.Button("按当前音色重合成", _buttonStyle))
                _uiMessage = SpeechMvp.Resynthesize(turn, _selectedReferencePath) ? "已提交重合成；会保留本条原始 WAV 与情感快照。" : "重合成未启动：请确认 Mod 功能与本地服务已启用。";
            if (GUILayout.Button("查看数据", _buttonStyle)) { _selectedTurn = turn; _dataLayer = 0; SetTab(2); }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.Space(6);
        }
    }

    private void DrawDataTab()
    {
        var turn = _selectedTurn ?? SpeechPanelData.GetTurn("npc:" + _npcId);
        if (turn == null)
        {
            GUILayout.Label("当前角色在本次会话内还没有捕获到结构化 NPC 回复。", _bodyStyle);
            GUILayout.Label("打开此页不会请求 LLM。收到下一条回复后，这里会显示实际捕获层。", _mutedStyle);
            return;
        }
        GUILayout.Label(NpcVoiceResolver.GetNpcName(turn.NpcKey) + " · " + turn.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), _sectionStyle);
        GUILayout.Label("轮次标识：" + turn.Identity, _mutedStyle);
        string[] layerNames = { "情感与样式", "实际 Schema", "实际响应", "TTS 请求" };
        GUILayout.BeginHorizontal();
        for (var i = 0; i < layerNames.Length; i++)
            if (GUILayout.Toggle(_dataLayer == i, layerNames[i], _tabStyle)) _dataLayer = i;
        GUILayout.EndHorizontal();
        if (_dataLayer == 2)
        {
            GUILayout.Label("解析视图：下方是插件解析后的干净正文与独立风格数据，不是原始回包。", _mutedStyle);
            _showRawReply = GUILayout.Toggle(_showRawReply, "展开查看实际收到的原始响应快照", _tabStyle);
            var separated = turn.ActualReplyJson == null ? null : NpcReplyPayload.Separate(turn.ActualReplyJson);
            var parsedView = separated == null
                ? turn.PresetVoiceStyleMatch != null
                    ? "预设台词正文：\n" + turn.DisplayText + "\n\n离线预设情感（不是模型回包字段）：\n" +
                      FormatJsonForDisplay(turn.VoiceStyleJson) + "\n\n匹配记录：" + turn.PresetVoiceStyleMatch
                    : "无法解析为 NPC 回复 JSON。原因：响应缺失、格式损坏或没有 content 字段。请展开原始响应快照查看。"
                : "干净游戏回复（已剥离本 Mod 协议帧与旧版 voice_style 字段）：\n" +
                  (FormatJsonForDisplay(separated.GameJson) ?? separated.GameJson) +
                  "\n\n独立语音风格：\n" + (turn.VoiceStyleJson == null ? "未捕获合法风格。" : FormatJsonForDisplay(turn.VoiceStyleJson)) +
                  "\n\n解析状态：" + TurnStyleStatusLabel(turn) +
                  "\n风格来源：" + StyleSourceLabel(turn.VoiceStyleSource) +
                  "\n原游戏 emotion：" + (turn.Emotion ?? "未观测");
            _dataScrollPosition = GUILayout.BeginScrollView(_dataScrollPosition, false, true,
                GUILayout.Height(_showRawReply ? 220 : 340), GUILayout.ExpandWidth(true));
            GUILayout.TextArea(parsedView, _bodyStyle, GUILayout.MinHeight(240), GUILayout.ExpandWidth(true));
            GUILayout.EndScrollView();
            if (_showRawReply)
            {
                GUILayout.Label("实际回包快照（本 Mod 最早观察到的响应；在到达此处前可能已被游戏或代理改写）：", _mutedStyle);
                var rawText = turn.ActualReplyJson ?? "未捕获原始响应。";
                _rawReplyScrollPosition = GUILayout.BeginScrollView(_rawReplyScrollPosition, false, true,
                    GUILayout.Height(200), GUILayout.ExpandWidth(true));
                GUILayout.TextArea(FormatJsonForDisplay(rawText) ?? rawText, _bodyStyle,
                    GUILayout.MinHeight(220), GUILayout.ExpandWidth(true));
                GUILayout.EndScrollView();
            }
            DrawDataActions(turn);
            return;
        }
        var json = _dataLayer switch
        {
            0 => FormatJsonForDisplay(turn.VoiceStyleJson) ?? TurnStyleStatusLabel(turn) + "\n本轮没有捕获合法语音风格。",
            1 => FormatJsonForDisplay(turn.OutputSchema) ?? turn.PromptStatus +
                (turn.PromptAddition != null ? "\n\n本轮文本输出契约增补：\n" + turn.PromptAddition : "\n本轮实际请求 Schema 未观测。"),
            _ => FormatJsonForDisplay(turn.TtsRequestJson) ?? "本轮尚未发起 TTS 请求：" + turn.TtsStatus
        };
        GUILayout.Label(_dataLayer switch
        {
            0 => $"解析状态：{TurnStyleStatusLabel(turn)} · 来源：{StyleSourceLabel(turn.VoiceStyleSource)} · 原游戏 emotion：{turn.Emotion ?? "未观测"}",
            1 => turn.PromptStatus,
            _ => "本地 TTS 请求摘要；参考音音频载荷已省略，不显示 base64。"
        }, _mutedStyle);
        if (turn.PresetVoiceStyleMatch != null)
            GUILayout.Label("预设匹配：" + turn.PresetVoiceStyleMatch, _mutedStyle);
        _dataScrollPosition = GUILayout.BeginScrollView(_dataScrollPosition, false, true,
            GUILayout.Height(340), GUILayout.ExpandWidth(true));
        GUILayout.TextArea(json, _bodyStyle, GUILayout.MinHeight(500), GUILayout.ExpandWidth(true));
        GUILayout.EndScrollView();
        if (_dataLayer == 3 && !string.IsNullOrWhiteSpace(turn.TtsInstruction))
            GUILayout.Label("实际 instruction：" + turn.TtsInstruction, _bodyStyle);
        DrawDataActions(turn);
    }

    private Vector2 _rawReplyScrollPosition;

    private static string TurnStyleStatusLabel(SpeechPanelTurn turn)
    {
        if (turn.VoiceStyleRequired && turn.VoiceStyleJson == null && (turn.VoiceStyleStatus is "absent" or "null"))
            return "已要求返回语音风格，但在本 Mod 收到的响应中未检测到；无法仅凭此判断模型漏生成或上游清洗";
        return StyleStatusLabel(turn.VoiceStyleStatus, turn.VoiceStyleFailureReason);
    }

    private static string StyleStatusLabel(string status, string? reason) => status switch
    {
        "valid" => "已解析",
        "preset_matched" => "已匹配离线预设情感",
        "null" => "明确为空",
        "absent" => "未检测到风格（本轮未记录强制要求）",
        "invalid" => "格式无效" + (reason == null ? "" : "（" + StyleFailureLabel(reason) + "）"),
        "unparseable" or "unparseable_or_unavailable" or "unparseable_or_not_reply_json" => "无法解析回包",
        "unknown" or "未观测" => "未观测",
        _ => status
    };

    private static string StyleFailureLabel(string reason) => reason switch
    {
        "invalid_json" => "帧内 JSON 无效",
        "incomplete_frame" => "帧未闭合",
        "multiple_frames" => "重复帧",
        "frame_too_long" => "帧超过长度限制",
        "frame_not_at_end" => "帧后还有文本",
        "unexpected_closing_tag" => "缺少开始标记",
        "emotion_tags_must_be_array" => "emotion_tags 必须是数组",
        "delivery_must_be_string" => "delivery 必须是文字",
        "intensity_must_be_number" => "intensity 必须是数字",
        "emotion_tag_count_out_of_range" => "标签数量需为 1 至 3 个",
        "intensity_out_of_range" => "intensity 需在 0 至 1 之间",
        "unexpected_or_missing_fields" => "字段不匹配",
        _ => reason
    };

    private static string StyleSourceLabel(string? source) => source switch
    {
        "content_envelope_v1" => "正文协议 v1",
        "offline_preset_library" => "离线预设库（按当前角色与台词匹配）",
        "legacy_voice_style" => "兼容旧版顶层字段",
        "pending_store" or "provider_reply" => "映射前回包缓存",
        "none" or null => "无",
        _ => source
    };

    private void DrawDataActions(SpeechPanelTurn turn)
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("复制本轮诊断摘要", _buttonStyle))
        {
            GUIUtility.systemCopyBuffer = BuildTurnSummary(turn);
            _uiMessage = "已复制当前轮次的安全诊断摘要。";
        }
        GUILayout.EndHorizontal();
    }

    private static string? FormatJsonForDisplay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch
        {
            // Some providers return a JSON string containing escaped JSON. Decode one layer for readability.
            try
            {
                var decoded = JsonSerializer.Deserialize<string>(raw);
                if (decoded != null) return FormatJsonForDisplay(decoded) ?? decoded;
            }
            catch { }
            return raw;
        }
    }

    private static string BuildTurnSummary(SpeechPanelTurn turn) => JsonSerializer.Serialize(new
    {
        turn.Identity, turn.NpcKey, turn.CreatedAt, turn.DisplayText, turn.SpokenText, turn.Emotion,
        turn.VoiceStyleJson, turn.VoiceStyleStatus, turn.VoiceStyleSource, turn.PresetVoiceStyleMatch, turn.VoiceStyleRequired, turn.PromptStatus, turn.OutputSchema, turn.PromptAddition, turn.ActualReplyJson,
        turn.TtsBackend, turn.ReferenceLabel, turn.TtsInstruction, turn.TtsRequestJson, turn.TtsStatus, turn.Error,
        audioBytes = turn.AudioBytes
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static void EnsureTheme()
    {
        if (_panelStyle != null || GUI.skin == null) return;
        _paperTexture = SolidTexture(new Color(.16f, .145f, .12f, 1f));
        _fieldTexture = SolidTexture(new Color(.13f, .12f, .105f, 1f));
        _borderTexture = SolidTexture(new Color(.38f, .33f, .25f, 1f));
        var lineTexture = SolidTexture(new Color(.38f, .33f, .25f, 1f));
        _panelStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(18, 18, 14, 14), border = new RectOffset(2, 2, 2, 2) };
        _panelStyle.normal.background = _paperTexture;
        _panelStyle.hover.background = _paperTexture;
        _panelStyle.active.background = _paperTexture;
        _panelStyle.normal.textColor = new Color(.93f, .89f, .80f);
        _cardStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 8, 8), margin = new RectOffset(2, 2, 4, 4), border = new RectOffset(2, 2, 2, 2) };
        _cardStyle.normal.background = _fieldTexture;
        _cardStyle.hover.background = _fieldTexture;
        _cardStyle.active.background = _fieldTexture;
        _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _titleStyle.normal.textColor = new Color(.94f, .88f, .73f);
        _subtitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true, stretchWidth = true };
        _subtitleStyle.normal.textColor = new Color(.72f, .65f, .50f);
        _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, stretchWidth = true, padding = new RectOffset(3, 3, 2, 2) };
        _bodyStyle.normal.textColor = new Color(.91f, .87f, .78f);
        _mutedStyle = new GUIStyle(_bodyStyle) { fontSize = 11 };
        _mutedStyle.normal.textColor = new Color(.70f, .63f, .49f);
        _sectionStyle = new GUIStyle(_bodyStyle) { fontSize = 14, fontStyle = FontStyle.Bold, margin = new RectOffset(2, 2, 9, 4) };
        _sectionStyle.normal.textColor = new Color(.92f, .87f, .75f);
        _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, fixedHeight = 31, padding = new RectOffset(8, 8, 4, 4), margin = new RectOffset(2, 2, 2, 2) };
        _buttonStyle.normal.background = _fieldTexture;
        _buttonStyle.hover.background = _paperTexture;
        _buttonStyle.active.background = lineTexture;
        _buttonStyle.normal.textColor = new Color(.92f, .87f, .76f);
        _buttonStyle.hover.textColor = new Color(1f, .95f, .83f);
        _buttonStyle.border = new RectOffset(2, 2, 2, 2);
        _primaryButtonStyle = new GUIStyle(_buttonStyle);
        _primaryButtonStyle.normal.background = SolidTexture(new Color(.40f, .32f, .17f, 1f));
        _primaryButtonStyle.hover.background = SolidTexture(new Color(.49f, .39f, .20f, 1f));
        _primaryButtonStyle.normal.textColor = new Color(.98f, .91f, .74f);
        _tabStyle = new GUIStyle(_buttonStyle) { fixedHeight = 34, stretchWidth = true };
        _tabStyle.onNormal.background = SolidTexture(new Color(.27f, .23f, .16f, 1f));
        _tabStyle.onHover.background = _tabStyle.onNormal.background;
        _tabStyle.onActive.background = _tabStyle.onNormal.background;
        _tabStyle.onNormal.textColor = new Color(.96f, .88f, .69f);
        GUI.skin.textField.normal.background = _fieldTexture;
        GUI.skin.textField.focused.background = _fieldTexture;
        GUI.skin.textField.normal.textColor = new Color(.92f, .87f, .76f);
        GUI.skin.textField.focused.textColor = GUI.skin.textField.normal.textColor;
        GUI.skin.textArea.normal.background = _fieldTexture;
        GUI.skin.textArea.normal.textColor = new Color(.92f, .87f, .76f);
        GUI.skin.textArea.focused.background = _fieldTexture;
        GUI.skin.textArea.focused.textColor = GUI.skin.textArea.normal.textColor;
    }

    private static Texture2D SolidTexture(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private void SetTab(int tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        if (_lastTab != null) { _lastTab.Value = tab; SaveConfig(); }
    }

    private void ChangeScale(int delta)
    {
        if (_interfaceScale == null) return;
        _interfaceScale.Value = Mathf.Clamp(_interfaceScale.Value + delta, 75, 175);
        SaveConfig();
        _positioned = false;
    }

    private void HandleWindowDrag(float scale)
    {
        var e = Event.current;
        var header = new Rect(_window.x + 8, _window.y, _window.width - 48, 28);
        if (e.type == EventType.MouseDown && e.button == 0 && header.Contains(e.mousePosition))
        {
            _dragging = true;
            _dragPointerStart = e.mousePosition;
            _dragWindowStart = new Vector2(_window.x, _window.y);
            e.Use();
        }
        else if (_dragging && e.type == EventType.MouseDrag)
        {
            var delta = (e.mousePosition - _dragPointerStart) / scale;
            _window.x = Mathf.Clamp(_dragWindowStart.x + delta.x, _safeLogical.x, Mathf.Max(_safeLogical.x, _safeLogical.xMax - _window.width));
            _window.y = Mathf.Clamp(_dragWindowStart.y + delta.y, _safeLogical.y, Mathf.Max(_safeLogical.y, _safeLogical.yMax - _window.height));
            e.Use();
        }
        else if (_dragging && e.type == EventType.MouseUp)
        {
            _dragging = false;
            e.Use();
        }
    }

    private static void SaveConfig()
    {
        try { Plugin.Instance?.Config.Save(); }
        catch (Exception e) { _log?.LogWarning("Could not save speech panel preferences: " + e.GetType().Name); }
    }

    private static float GetUiScale()
    {
        var userScale = Mathf.Clamp((_interfaceScale?.Value ?? 100) / 100f, .75f, 1.75f);
        float? dpiFactor = null;
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == Environment.ProcessId)
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi >= 72 && dpi <= 384) dpiFactor = dpi / 96f;
            }
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        if (dpiFactor == null && Screen.dpi is >= 72 and <= 384) dpiFactor = Screen.dpi / 96f;

        // If Unity/Windows does not expose DPI, use a gentle resolution fallback and a readable baseline.
        var resolutionFactor = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(1, Screen.height) / 900f), 1f, 1.65f);
        var automatic = dpiFactor.HasValue ? Mathf.Clamp(dpiFactor.Value, .9f, 2.25f) : Mathf.Max(1.25f, resolutionFactor);
        return Mathf.Clamp(automatic * userScale, .9f, 3.5f);
    }

    private void UpdateLogicalSafeArea(float scale)
    {
        var safe = Screen.safeArea;
        if (safe.width < 1 || safe.height < 1) safe = new Rect(0, 0, Screen.width, Screen.height);
        _safeLogical = new Rect(safe.x / scale, (Screen.height - safe.yMax) / scale,
            safe.width / scale, safe.height / scale);
        if (_lastScreenWidth != Screen.width || _lastScreenHeight != Screen.height || !Mathf.Approximately(_lastScale, scale))
        {
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;
            _lastScale = scale;
            _positioned = false;
        }
    }

    private static float GetScreenCoordinateRatio()
    {
        try
        {
            var desktopWidth = GetSystemMetrics(0);
            var desktopHeight = GetSystemMetrics(1);
            if (desktopWidth > 0 && desktopHeight > 0)
            {
                var widthRatio = (float)Screen.width / desktopWidth;
                var heightRatio = (float)Screen.height / desktopHeight;
                if (widthRatio is >= .75f and <= 4f && heightRatio is >= .75f and <= 4f &&
                    Mathf.Abs(widthRatio - heightRatio) < .08f)
                    return (widthRatio + heightRatio) * .5f;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1f;
    }

    private static string? ReadNpcId(object instance)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField("_npcId", flags | BindingFlags.DeclaredOnly) ??
                        type.GetField("npcId", flags | BindingFlags.DeclaredOnly);
            var value = field?.GetValue(instance);
            if (value == null) continue;
            var nested = value.GetType().GetProperty("Value", flags)?.GetValue(value) ?? value;
            if (int.TryParse(nested.ToString(), out var id) && id > 0) return id.ToString();
        }
        return null;
    }

    private static string? ReadNpcName(object instance)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty("NpcName", flags) ?? type.GetProperty("npcName", flags);
            if (property?.GetValue(instance) is string name && !string.IsNullOrWhiteSpace(name)) return name;
        }
        return null;
    }
}
