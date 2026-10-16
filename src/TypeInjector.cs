using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using TMPro;
using UnityEngine;

namespace A1IndexTTSMod
{
    /// <summary>
    /// 手机端打字 → 直接写进游戏的对话输入框（可选自动发送）。
    ///
    /// ★为什么不走「键盘模拟」路线：
    ///   用 SendInput 敲键盘需要逐字符模拟，中文 IME 更是不可能靠虚拟按键搞定；
    ///   而游戏输入框是 `TMPro.TMP_InputField`，**可以直接写 .text**，
    ///   再点它自己的发送按钮 —— 这条路既可靠又省事（和 ASR 模块用的是同一套 API）。
    ///
    /// 【复用的现有资产（都在 SpeechPanelUi 里，已实测可用）】
    ///   · `TMP_InputField` 定位：DialogueInput → InputFieldMain → TMP_InputField
    ///   · 发送按钮：InputFieldMain → BtnInputFieldSubmit（UIButton）
    ///   本类只是把这两个能力开放给手机端，**不重复实现反射逻辑**。
    ///
    /// 【IL2CPP 约束】★所有 Unity 对象访问必须在主线程。
    ///   HTTP 线程只把字符串入队；`PumpOnMainThread()` 在 Update 里执行。
    /// </summary>
    public static class TypeInjector
    {
        private const string LogPrefix = "[TYPE] ";

        private static readonly object _queueLock = new object();
        private static string? _pendingText;
        private static bool _pendingSend;

        private static int _typeRxCount;
        private static int _typeOkCount;
        private static int _typeFailCount;
        private static string _lastError = "";

        public static int ReceivedCount => System.Threading.Volatile.Read(ref _typeRxCount);
        public static int OkCount => System.Threading.Volatile.Read(ref _typeOkCount);
        public static int FailCount => System.Threading.Volatile.Read(ref _typeFailCount);
        public static string LastError => _lastError;

        private static void L(string s) => MjpegServer.L(LogPrefix + s);

        /// <summary>HTTP 线程调用：入队一条待输入的文字（纯 .NET，不碰 Unity）。</summary>
        public static void EnqueueType(string text, bool send)
        {
            System.Threading.Interlocked.Increment(ref _typeRxCount);
            lock (_queueLock)
            {
                _pendingText = text;
                _pendingSend = send;
            }
        }

        /// <summary>主线程 Update 调用：执行入队的输入。</summary>
        public static void PumpOnMainThread()
        {
            string? text;
            bool send;
            lock (_queueLock)
            {
                if (_pendingText == null) return;
                text = _pendingText;
                send = _pendingSend;
                _pendingText = null;
                _pendingSend = false;
            }

            try
            {
                ApplyText(text!, send);
                System.Threading.Interlocked.Increment(ref _typeOkCount);
                _lastError = "";
            }
            catch (Exception e)
            {
                System.Threading.Interlocked.Increment(ref _typeFailCount);
                if (_lastError != e.Message)
                {
                    _lastError = e.Message;
                    L("★写入输入框失败：" + e.Message);
                }
            }
        }

        /// <summary>把文字写进游戏输入框（★必须在主线程）。</summary>
        private static void ApplyText(string text, bool send)
        {
            var field = FindDialogueInput();
            if (field == null)
            {
                throw new InvalidOperationException(
                    "找不到对话输入框（可能对话界面没打开）。");
            }

            // ★TMP_InputField.text 是受支持的可写属性；
            //   只改 text 不触发 OnValueChanged，所以游戏自己的发送按钮可能仍是"灰"的
            //   —— 因此需要同时设置 isFocused / 让其进入"有草稿"状态。
            field.text = text;
            field.caretPosition = text.Length;
            field.ForceLabelUpdate();
            // 置焦点让游戏知道用户正在输入（部分逻辑依赖 hasFocus 判断草稿有效性）
            field.ActivateInputField();

            L($"已写入输入框（{text.Length} 字）"
              + (send ? "，并尝试发送" : "，未发送"));

            if (send) TryClickSend();
        }

        /// <summary>
        /// 触发游戏自己的发送按钮。
        ///
        /// ★为什么不用 `Selectable.OnSelect(BaseEventData)`（初版就这么写的，编译不过）：
        ///   反射实测 interop 签名（`Unity.TextMeshPro` / `UnityEngine.UI`）：
        ///   · `Selectable.OnSelect(UnityEngine.EventSystems.BaseEventData)` 确实存在
        ///   · 但 `UnityEngine.UI.Button` **根本没有 onClick 事件**（探针输出"(无) onClick"）
        ///     —— 游戏用的是自研 `UIButton`，事件名叫 `OnClick`（大写），
        ///       类型是 `Il2CppSystem.Action`，不是 `UnityEvent`。
        ///   · `Select()` 只是「选中」（移动焦点），**不会触发发送**。
        ///   ⇒ 正确做法：读游戏 `UIButton.OnClick` 委托并 Invoke 它，
    ///     这与 `SpeechPanelUi.ConfigureEntryButton` 里已实测可用的手法一致。
        /// </summary>
        private static void TryClickSend()
        {
            var button = FindSubmitButton();
            if (button == null)
            {
                L("★找不到发送按钮（已写入文字，请手动点发送）。");
                return;
            }

            try
            {
                // 反射取 OnClick（UIButton 的委托字段名就是 OnClick）。
                // 拿它自己的委托再 Invoke —— 等于「游戏自己按下发送」，语义最正确。
                var onClick = GetMember(button, "OnClick");
                if (onClick == null)
                {
                    L("★发送按钮没有 OnClick 委托（已写入文字，请手动发送）。");
                    return;
                }

                // Il2CppSystem.Action.Invoke() —— interop 委托类型**不继承 System.Delegate**，
                //   所以既不能强转成 Action，也不能用 DynamicInvoke；
                //   唯一可行的是在它自己的类型上找 Invoke 方法再反射调。
                var invoke = onClick.GetType().GetMethod("Invoke");
                if (invoke == null)
                {
                    L("★OnClick 委托没有 Invoke 方法（已写入文字，请手动发送）。");
                    return;
                }

                invoke.Invoke(onClick, null);
                L("已触发发送按钮 OnClick。");
            }
            catch (Exception e)
            {
                // 点击失败不算致命：文字已经进去了，用户可手动点发送
                L("★触发发送按钮失败（文字已写入，请手动点发送）：" + e.Message);
            }
        }

        // ---------------------------------------------------------------- 反射定位

    private static object? GetMember(object? target, string name)
        {
        if (target == null) return null;
      const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            // ★必须沿基类链往上找：interop 的包装类成员常声明在基类上
            //（如 UIButton 的 OnClick 在游戏自己的基类里），
            //   只查 target.GetType() 会漏掉（与 SpeechPanelUi.GetMember 同理）。
        for (var t = target.GetType(); t != null; t = t.BaseType)
      {
      var prop = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
            if (prop != null) { try { return prop.GetValue(target); } catch { } }
    var field = t.GetField(name, flags | BindingFlags.DeclaredOnly);
            if (field != null) { try { return field.GetValue(target); } catch { } }
            }
  return null;
        }

        /// <summary>定位游戏的对话输入框（与 SpeechPanelUi.FindDialogueInput 同一路径）。</summary>
        private static TMP_InputField? FindDialogueInput()
        {
            // 路径：NpcDialoguePanel 面板 → DialogueInput → InputFieldMain → TMP_InputField
            // 直接在场景里找：对话打开时输入框是激活状态，用 FindObjectsOfType 更稳，
            // 避免依赖面板层级（面板可能已销毁/重建）。
            var fields = UnityEngine.Object.FindObjectsOfType<TMP_InputField>(false);
            TMP_InputField? best = null;
            int bestScore = -1;

            foreach (var f in fields)
            {
                if (f == null) continue;
                // ★打分选最可能的那个：激活 + 可见 + 有内容能力
                int score = 0;
                if (f.gameObject.activeInHierarchy) score += 4;
                if (f.isActiveAndEnabled) score += 2;
                if (f.gameObject.activeSelf) score += 1;
                // 对话框里常见的 placeholder 提示文字
                // ★placeholder 的静态类型是 UnityEngine.UI.Graphic，**它没有 .text**
                //   （反射实测：Graphic ← UIBehaviour ← MonoBehaviour；
                //     只有 TMPro.TMP_Text 才有 text 属性）。
                //   必须先 TryCast 成 TMP_Text 再读，否则编译直接报
                //   "Graphic 未包含 text 的定义"（初版就踩了这个）。
                var ph = f.placeholder;
                if (ph != null)
                {
                    var tmpText = ph.TryCast<TMPro.TMP_Text>();
                    if (tmpText != null)
                    {
                        var t = tmpText.text;
                        if (!string.IsNullOrEmpty(t) && (t.Contains("输入") || t.Contains("说")))
                            score += 8;   // ★强特征：placeholder 含"输入/说"
                    }
                }
                if (score > bestScore) { bestScore = score; best = f; }
            }

              // ★打分最高的那个（哪怕分数是 0 也要传下去）：
            //   兜底路径 FindPanelAbove 从它出发照样有机会，
            //   若在这里就因 bestScore<=0 把 best 丢掉，兜底等于没有。
    if (best != null && bestScore > 0) return best;
        if (best == null) return null;

      // 兜底：拿对话面板的面板对象走反射路径
    //   ★注意：这里**绝不能**再调 FindDialoguePanel()——
    //   那个方法内部会回调 FindDialogueInput()，两者互调 = 无限递归 → 栈溢出崩溃。
    //   正确做法是把面板查找做成**不依赖本方法**的独立路径（见 FindPanelAbove）。
            var panel = FindPanelAbove(best);
   if (panel != null)
            {
       var dialogueInput = GetMember(panel, "DialogueInput");
                var commonInput = GetMember(dialogueInput, "InputFieldMain");
      var direct = GetMember(commonInput, "InputFieldMain");
                if (direct is TMP_InputField df) return df;

       // ★interop 下 object 不能直接 `as Component`（托管侧看不到 il2cpp 基类转换），
                //   必须用 TryCast；而 TryCast 是 Il2CppObjectBase 的**实例泛型方法**，
                //   静态类型是 object 时调不到 → 用 MakeGenericMethod 反射调。
      var comp = TryCastGeneric<Component>(commonInput);
    if (comp != null)
  {
         var nested = comp.GetComponentInChildren<TMP_InputField>(true);
    if (nested != null) return nested;
    }
         }

          return null;
        }

        /// <summary>
        /// 从给定输入框出发，**沿父级链向上**找持有 DialogueInput / InputFieldMain 的组件。
        ///
        /// ★与 FindDialogueInput 的关系：只**向下**依赖（接收现成的 field），
        ///   绝不反过来调 FindDialogueInput —— 否则两者互调无限递归。
        /// </summary>
   private static object? FindPanelAbove(TMP_InputField? field)
        {
            if (field == null) return null;
            var comp = field.TryCast<UnityEngine.Component>();
            if (comp == null) return null;

            var cur = comp.transform;
            while (cur != null)
    {
       var go = cur.gameObject;
    if (go != null && go.activeInHierarchy)
      {
            foreach (var c in go.GetComponents<UnityEngine.Component>())
        {
     if (c == null) continue;
   if (GetMember(c, "DialogueInput") != null) return c;
    if (GetMember(c, "InputFieldMain") != null) return c;
          }
  }
              cur = cur.parent;
            }
         return null;
  }

        /// <summary>
        /// 对静态类型为 <see cref="object"/> 的 il2cpp 对象做 TryCast&lt;T&gt;。
        ///
        /// ★为什么需要这个 helper：`TryCast&lt;T&gt;()` 是 `Il2CppObjectBase` 的**实例泛型方法**，
        ///   只有编译期静态类型是某个 `Il2CppObjectBase` 派生类时才能直接调。
        ///   而反射 `GetMember()` 返回 `object` → 写 `obj.TryCast&lt;T&gt;()` 编译不过（CS1061）。
        ///   这里用 MakeGenericMethod 在运行期补上类型参数。
        /// </summary>
        private static T? TryCastGeneric<T>(object? il2cppObject) where T : Il2CppObjectBase
        {
            if (il2cppObject == null) return null;
            try
            {
                // 沿继承链找 Il2CppObjectBase（TryCast 的声明类）
                Type? owner = null;
                for (var t = il2cppObject.GetType(); t != null; t = t.BaseType)
            {
   if (t.Name.StartsWith("Il2CppObjectBase", StringComparison.Ordinal))
      { owner = t; break; }
      }
   if (owner == null) return null;

    var mi = owner.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition
                      && m.GetParameters().Length == 0);
                if (mi == null) return null;

                return mi.MakeGenericMethod(typeof(T)).Invoke(il2cppObject, null) as T;
         }
      catch
    {
      return null;
       }
        }

        /// <summary>
        /// 定位游戏发送按钮（InputFieldMain → BtnInputFieldSubmit）。
    /// ★只走这一条精确路径，**没有"随便找个按钮"的兜底** ——
        ///   初版的兜底是"场景里第一个 interactable 的按钮"，那会点到
        ///   挂断/诊断之类的按钮上，属于会误操作玩家的行为，不能留。
        /// </summary>
        private static object? FindSubmitButton()
        {
            // 路径：输入框 → 父级链上的面板 → DialogueInput → InputFieldMain → BtnInputFieldSubmit
            var field = FindDialogueInput();
            if (field == null) return null;

            var comp = field.TryCast<UnityEngine.Component>();
            if (comp == null) return null;

            // 先在输入框自己的层级里找（按钮通常和输入框同级或紧邻）
            var submit = FindMemberInHierarchy(field.gameObject, "BtnInputFieldSubmit");
            if (submit != null) return submit;

            // 再从父级链找持有 InputFieldMain 的面板
            var cur = comp.transform;
            while (cur != null)
            {
                var go = cur.gameObject;
                if (go != null && go.activeInHierarchy)
                {
                    foreach (var c in go.GetComponents<UnityEngine.Component>())
                    {
                        if (c == null) continue;
                        var commonInput = GetMember(c, "InputFieldMain")
                            ?? GetMember(c, "DialogueInput");
                        if (commonInput != null)
                        {
                            var btn = GetMember(commonInput, "BtnInputFieldSubmit");
                            if (btn != null) return btn;
                        }
                    }
                }
                cur = cur.parent;
            }
            return null;
        }

        /// <summary>在某个 GameObject 及其子对象里找持有指定名字成员/字段的组件。</summary>
        private static object? FindMemberInHierarchy(UnityEngine.GameObject root, string memberName)
        {
            if (root == null) return null;
            var comps = root.GetComponentsInChildren<UnityEngine.Component>(true);
            foreach (var c in comps)
            {
                if (c == null) continue;
                if (GetMember(c, memberName) != null) return c;
            }
            return null;
        }

        public static string BuildStatusJson()
        {
            return "{\"typeRx\":" + ReceivedCount
                 + ",\"typeOk\":" + OkCount
                 + ",\"typeFail\":" + FailCount
                 + ",\"error\":\"" + _lastError.Replace("\"", "") + "\"}";
        }
    }
}