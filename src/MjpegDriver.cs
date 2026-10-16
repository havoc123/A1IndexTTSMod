using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace A1IndexTTSMod
{
    /// <summary>
    /// 主线程驱动器：把 <see cref="MjpegServer"/> 的取帧循环挂到 Unity 主线程。
    ///
    /// ★为什么必须有这个类（IL2CPP 硬约束）：
    ///   RenderTexture / Texture2D / Camera.Render 只能在 Unity 主线程调用，
    ///   而 HttpListener 跑在纯 .NET 后台线程上。所以拆成两半：
    ///     · 后台线程（MjpegServer.AcceptLoop）：只收发字节，不碰任何 Unity API
    ///     · 主线程（本类 Update）：取帧、编码、释放资源
    ///   两者只通过 volatile 字段交换数据，无锁。
    ///
    /// ★绝不在 Plugin.Load() 里 new GameObject/AddComponent（IL2CPP 下启动即崩）。
    ///   这里只做类型注册，实例化挂在 sceneLoaded 回调之后。
    /// </summary>
    public static class MjpegDriver
    {
        public static void Register()
        {
            ClassInjector.RegisterTypeInIl2Cpp<Driver>();
            MjpegServer.L("Driver 类型已注册到 IL2CPP");

            // interop 下 SceneManager.sceneLoaded 的委托类型是 il2cpp 的
            // UnityAction<Scene, LoadSceneMode>。**必须显式强转成该委托类型再挂**，
            // 否则编译器会把 lambda 当托管委托，类型不匹配直接编译失败
            // （已在独立验证工程中实测：CS0019）。
            UnityEngine.SceneManagement.SceneManager.sceneLoaded +=
                (UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene,
                                              UnityEngine.SceneManagement.LoadSceneMode>)
                ((scene, mode) => TryAttach("scene:" + scene.name));

            // 已有场景（插件加载较晚时 sceneLoaded 可能已错过）
            TryAttach("bootstrap");
        }

        private static void TryAttach(string reason)
        {
            try
            {
                if (_instance != null) return;

                var go = new GameObject("A1MjpegDriver");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var drv = go.AddComponent<Driver>();

                // ★必须在 AddComponent 之后**立刻**赋值，不能等 Start() 里赋。
                //   实测踩坑：bootstrap 时挂的 Driver 在 Start() 执行前就被销毁了
                //   （日志里它从未打印"驱动器就绪"），于是 Start() 没跑->_instance 仍为 null
                //   → 随后的 sceneLoaded 又挂出第二个 Driver，两个并存。
                //   立即赋值可让「挂载中」这一段也被幂等保护覆盖。
                _instance = drv;
                MjpegServer.L($"Driver 已挂载（{reason}）");
            }
            catch (Exception e)
            {
                MjpegServer.L($"Driver 挂载失败（{reason}）：{e.Message}");
            }
        }

        private static Driver? _instance;

        public class Driver : MonoBehaviour
        {
            private bool _started;
            private float _statTimer;
            private float _rebindTimer;
            private float _camSnapTimer;

            private void Start()
            {
                _instance = this;
                MjpegServer.L("主线程驱动器就绪，开始取帧。");
                _started = true;
            }

            private void Update()
            {
                //看门狗：若本 Driver 已被销毁/失效（例如场景切换时的边界情况），
                // 自动补挂一个，避免"服务在跑但没人取帧"的静默状态。
                _rebindTimer += Time.deltaTime;
                if (_rebindTimer >= 3f)
                {
                    _rebindTimer = 0f;
                    if (_instance == null || !_instance.enabled)
                    {
                        MjpegServer.L("检测到取帧 Driver 失效，自动补挂。");
                        TryAttach("watchdog");
                    }
                }

                if (!_started) return;

                // 取帧 + JPEG 编码（主线程）
                MjpegServer.Tick(Time.deltaTime);

                // ★把手机端的点击注入游戏窗口（★必须主线程：涉及 SetCursorPos/
                //   mouse_event，且需要先切前台窗口）。
                TapInjector.PumpOnMainThread();

                // ★把手机端打的字写进游戏输入框（★必须主线程：涉及 Unity 对象访问）。
                TypeInjector.PumpOnMainThread();

                // ★手机「语音」按钮远程切换游戏内麦克风（★必须主线程：
                //   走 SpeechPanelUi.ToggleMic，碰 Unity 对象）。
                SpeechPanelUi.PumpMicToggleOnMainThread();

                // 定期刷新相机列表快照（供 /cams 端点诊断黑屏）
                _camSnapTimer += Time.deltaTime;
                if (_camSnapTimer >= 2f)
                {
                    _camSnapTimer = 0f;
                    MjpegServer.RefreshCameraSnapshot();
                }

                // 操作回传：把手机发来的指令交给上层业务
                DrainCommands();

                // 每 5 秒打一次帧数，便于确认推流真的在动
                _statTimer += Time.deltaTime;
                if (_statTimer >= 5f)
                {
                    _statTimer = 0f;

                    // ★先自愈服务，再打统计。顺序很重要：
                    //   必须在统计之前，否则"服务已死但仍报一切正常"的假绿会误导判读。
                    MjpegServer.EnsureAlive();

                    if (MjpegServer.FrameCount > 0)
      {
              // ★延迟自证指标（2026-10-14 延迟优化新增）：
              //   均帧KB × fps = 下行码率；背压次数 = Write 超过一帧间隔的次数。
              //   两个数字一起看：码率<10Mbps 且背压不涨 ⇒ 链路 healthy，
              //   延迟只可能来自网络/浏览器本身；码率高或背压猛涨 ⇒ 调小
              //   StreamWidth / JpegQuality / Fps。
              MjpegServer.L($"运行中：已编码 {MjpegServer.FrameCount} 帧，"
            + $"推流 {MjpegServer.StreamW}x{MjpegServer.StreamH}，"
            + $"均帧 {MjpegServer.AvgFrameBytes / 1024}KB（≈{MjpegServer.EstMbps:F1}Mbps），"
            + $"背压 {MjpegServer.SlowWrites} 次（最长写出 {MjpegServer.MaxWriteMs}ms），"
            + $"客户端 {MjpegServer.ClientCount} 个，"
       + $"点击 收{TapInjector.ReceivedCount}/注{TapInjector.InjectedCount}"
      + $"（窗口 {TapInjector.WindowInfo}）。");
        }
        else if (MjpegServer.IsRunning)
          {
      // ★★ 这里不再自造诊断结论，只做**指路**（本次已因"乱猜原因"浪费两轮）。
  //   真正的失败原因一律由 MjpegServer.ReportCaptureProblem 打「★抓帧失败：」开头那行。
    //   若日志里根本没有该行 → 说明 Tick 没被调用（Driver 没接上）。
      MjpegServer.L("服务在运行但仍未出帧 → 往上翻找「★抓帧失败：」开头的行；"
            + "若没有该行，说明主线程 Tick 未被调用"
        + "（检查上方是否有「主线程驱动器就绪」）。");
        }
                    else
                    {
                        MjpegServer.L("★服务未运行，手机将无法访问。");
                    }
                }
            }

            /// <summary>
            /// 把手机端回传的指令转成游戏内动作。
            /// 目前先记录并留接口——具体接到对话选项/挂断流程由业务层决定。
            /// </summary>
            private static void DrainCommands()
            {
                int guard = 0;
                while (MjpegServer.TryDequeueCommand(out var cmd) && guard++ < 16)
                {
                    try
                    {
                        OnPhoneCommand(cmd);
                    }
                    catch (Exception e)
                    {
                        MjpegServer.L($"处理手机指令 '{cmd}' 异常：{e.Message}");
                    }
                }
            }

            /// <summary>
            /// 手机端操作回传入口。
            ///
            /// ★当前状态：**链路已通，但业务动作尚未接入**（2026-10-13）。
            ///   前端→后端这段本轮才修好（原因见 MjpegServer.HandleCommand 与
            ///   PhonePage.send 的注释：Chrome 同源连接池被 MJPEG 长流占满，
            ///   fetch 排队等不到槽位 → 点了完全没反应、后端 0 条日志）。
            ///   现在改为 sendBeacon + 后端支持 beacon 的 POST，
            ///   并在 /status 暴露 cmdRx 计数供手机页显示，用户可自行确认链路通没通。
            ///
            /// 待接入：把 look/talk/bye/hangup 映射到游戏真实动作
            ///（需先 probe 出对话系统的调用入口，见 plan 文档 D7）。
            /// </summary>
            internal static void OnPhoneCommand(string cmd)
            {
                MjpegServer.L($"手机指令：{cmd}（链路已通，业务动作待接入）");
            }

            /// <summary>
            /// ★★这里绝不能停 MjpegServer 服务（实测教训，见 2026-10-13 18:18）。
            ///
            /// 背景：本 Driver 是在 Plugin.Load 阶段（场景未加载）挂上的，
            /// 随后主场景加载时它会被 Unity 销毁 → OnDestroy 触发 →
            /// 之前在这里调 MjpegServer.Stop()，把整个 HTTP 服务杀了，
            /// 于是端口不再监听、手机直接"无法访问此网站"。
            /// 之后 sceneLoaded 又挂了新 Driver，但**只恢复了取帧、没恢复服务**。
            ///
            /// 正确职责划分：
            ///   · HTTP 服务生命周期 = **游戏进程**（Plugin.Load 启动 /进程退出自然结束）
            ///   · Driver 生命周期     = 场景（销毁就销毁，下个场景重新挂）
            /// 两者不是一对一，不该互相绑定。
            ///
            /// 这里只清引用，让新 Driver 能挂上来；Unity 对象（RT/Texture2D）也一并释放，
            /// 因为它们确实属于这个 Driver。
            /// </summary>
            private void OnDestroy()
            {
                MjpegServer.L("Driver 被销毁（服务保持运行，等待新 Driver 接管取帧）。");
                MjpegServer.DisposeUnityObjects();

                // 只有当"当前没有任何 Driver 存活"时才认为取帧真的停了，
                // 但服务本身**不停止**。
                if (_instance == this)
                {
                    _instance = null;
                    // 兜底：如果游戏正在退出，才真正收服务。
                    if (!IsGameRunning) MjpegServer.Stop();
                }
            }
        }

        /// <summary>游戏进程是否仍在运行（退出时为false）。</summary>
        private static bool IsGameRunning
        {
            get
            {
                try { return UnityEngine.Application.isPlaying; }
                catch { return true; }
            }
        }
    }
}