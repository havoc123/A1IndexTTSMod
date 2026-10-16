using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace A1IndexTTSMod
{
    /// <summary>
    /// NPC 视频通话窗口 —— 画面下行服务（MJPEG over HTTP）。
    ///
    /// ★为什么不用 WebRTC：`com.unity.webrtc` 官方文档明文
    ///   "IL2CPP is not supported in Scripting Backend by this package"，
    ///   而本项目就是 IL2CPP 游戏。此前 Spike 已连崩 3 轮（详见 E:\a1c\route_review_2026-10-13.md）。
    /// 本类**全托管、零 native 回调**：只用 TcpListener / Camera.Render /
    /// Texture2D.EncodeToJPG，刻意避开 IssuePluginEventAndData 与 native→托管委托。
    ///
    /// ★★为什么不用 HttpListener（实测打脸，务必记住）：
    ///   HttpListener 绑定 "http://+:端口/" 前缀时，Windows 要求该 URL 通过
    ///   HTTP.SYS 的 URL ACL。非管理员进程会拿到
    ///   **HttpListenerException, ErrorCode=5（拒绝访问）**，连127.0.0.1 都起不来。
    ///   （Game 过Steam/BepInEx 启动不可能是管理员，所以这条路在 MOD 场景下是死的。）
    ///   → 改用裸 TcpListener 手写 HTTP：**零权限要求**，直接绑IPAddress.Any 的端口。
    ///
    ///   ⚠ 手写 HTTP 的两个必要细节（少一个手机就白屏）：
    ///   1. MJPEG 长流**不能给Content-Length**，必须靠 Connection: close 定界，
    ///      且响应行用 HTTP/1.0（这是 multipart/x-mixed-replace 的标准用法）。
    ///   2. TcpClient.NoDelay = true，关闭 Nagle；否则小 JPEG 会被攒到40ms 才发。
    ///
    /// 端点：
    ///   GET /mjpeg?cam=N&amp;w=&amp;h=&amp;fps=  → multipart/x-mixed-replace 流
    ///   GET /shot?cam=N                       → 单帧 JPEG
    ///   GET /phone        → 手机端页面（自带播放器 + 操作回传）
    ///   GET /status       → JSON 状态（供页面与调试）
    ///   POST /cmd         → 操作回传（body: JSON）
    ///
    /// ★IL2CPP 硬约束（本类严格遵守）：
    ///   1. 所有 Unity 对象（RenderTexture / Texture2D）的创建与销毁
    ///      **只在 Unity 主线程的回调里做** —— 取帧在 Update，释放走 OnDestroy。
    ///   2. **绝不在 Plugin.Load() 里 new GameObject / AddComponent**（会启动即崩）。
    ///   3. Socket 线程是纯 .NET 线程，绝不碰任何 Unity API；
    ///      只通过原子字段与队列和主线程交换数据。
    /// </summary>
    public static class MjpegServer
    {
        private const string LogPrefix = "[MJPEG] ";

        // ---- 配置 ----
        public static int Port { get; set; } = 8895;

        /// <summary>
        /// 【已废弃 2026-10-14】旧版「固定推流尺寸」的宽度，仅保留兼容旧配置文件。
        /// 实际推流尺寸由 <see cref="StreamWidth"/> + 屏幕宽高比动态决定（见 CaptureScreenFrame）。
        /// </summary>
        public static int Width { get; set; } = 640;

        /// <summary>【已废弃】同上，仅保留兼容。</summary>
        public static int Height { get; set; } = 480;

        /// <summary>
        /// 推流目标宽度（像素）。0 = 不缩放，推屏幕原图。
        /// ★默认 960 是 2026-10-14 延迟优化的核心：本机游戏跑 1920x1080，
        ///   原图直推 q60 → 每帧 250~600KB → 15fps = 4~9MB/s，手机 Wi-Fi 撑不住
        ///   → TCP 背压 → 画面延迟 3~5 秒。降到 960 宽后像素仅 1/4，
        ///   帧大小/编码耗时/手机解码耗时同步约 1/4，任何 Wi-Fi 都够。
        /// </summary>
        public static int StreamWidth { get; set; } = 960;

        public static int TargetFps { get; set; } = 15;
        public static int JpegQuality { get; set; } = 60;

        /// <summary>实际推流尺寸（每帧更新，供日志与 /status 显示真实值）。</summary>
        public static volatile int StreamW;
        public static volatile int StreamH;

        private static ManualLogSource? _log;
        private static TcpListener? _listener;
        private static Thread? _acceptThread;
        private static volatile bool _running;

        /// <summary>最新一帧 JPEG。原子引用替换，消费者无锁读取。</summary>
        private static volatile byte[]? _latestFrame;

        /// <summary>已编码的帧序号，仅用于日志观察（不参与正确性）。</summary>
        private static int _frameSeq;

        /// <summary>累计已编码字节数（配合 _frameSeq 算平均帧大小 = 带宽诊断核心指标）。</summary>
        private static long _totalFrameBytes;

        /// <summary>新帧就绪事件：编码完 Set，ServeMjpeg 醒来即取——
        /// 取代原来的 Thread.Sleep(15) 轮询（轮询最多白等 15ms，且每帧空转 66 次）。</summary>
        private static readonly ManualResetEventSlim _frameReady = new ManualResetEventSlim(false);

        // ---- 下行链路诊断（用户要求"能自证的客观指标"）----
        /// <summary>最近一次把整帧 Write+Flush 完成的耗时（ms）。
        /// ★持续大于一帧间隔（1000/fps）说明手机链路吃不下 → 屏幕上必然是旧画面。</summary>
        private static volatile int _lastWriteMs;
        /// <summary>本次会话最大的单帧写出耗时（ms）。</summary>
        private static volatile int _maxWriteMs;
        /// <summary>写出耗时超过一帧间隔的次数（背压计数：增长 = 链路饱和 = 延迟来源）。</summary>
        private static int _slowWrites;

        /// <summary>客户端连接数（诊断用：手机上打出画面时这个值应≥1）。</summary>
        private static int _clientCount;

        /// <summary>等待取帧的相机序号：-1 = 自动选主相机，0 = 第一个，1 = 第二个（UI 相机）。</summary>
        private static volatile int _requestedCamera = -1;

        /// <summary>服务端收到的操作回传（主线程消费）。</summary>
        private static readonly Queue<string> _pendingCommands = new Queue<string>();
        private static readonly object _cmdLock = new object();

        // 主线程侧持有的 Unity 对象（只在主线程访问）。
        private static RenderTexture? _rt;
        private static Texture2D? _readback;
        private static UnityEngine.Camera? _cam;
        // 注：_rt / _readback 属最早的"渲染相机 + ReadPixels"路线，早已弃用；
        //     当前抓帧走 ScreenCapture.CaptureScreenshotAsTexture（不持有任何 RT）。
        //     这两个字段保留仅供 DisposeUnityObjects 兜底清理，勿在新代码里使用。

        public static string? LastError { get; private set; }
        public static bool IsRunning => _running;
        public static int FrameCount => Volatile.Read(ref _frameSeq);
        public static int ClientCount => Volatile.Read(ref _clientCount);

        /// <summary>平均帧大小（字节）。0 = 还没出帧。</summary>
        public static long AvgFrameBytes
        {
            get
            {
                long n = Volatile.Read(ref _frameSeq);
                if (n <= 0) return 0;
                return Volatile.Read(ref _totalFrameBytes) / n;
            }
        }
        /// <summary>最近一次单帧写出耗时（ms）。</summary>
        public static int LastWriteMs => _lastWriteMs;
        /// <summary>单帧写出耗时最大值（ms）。</summary>
        public static int MaxWriteMs => _maxWriteMs;
        /// <summary>写出超过一帧间隔的次数（背压计数）。</summary>
        public static int SlowWrites => Volatile.Read(ref _slowWrites);
        /// <summary>估算下行码率（Mbps）= 平均帧字节 × 8 × fps / 1e6。
        /// ★手机侧带宽判据：持续 > ~15Mbps 普通 Wi-Fi 就会背压出秒级延迟。</summary>
        public static double EstMbps => AvgFrameBytes * 8.0 * TargetFps / 1_000_000.0;

        /// <summary>抓帧是否已就绪（RT 已建 + SRP 回调已挂上）。</summary>
        public static bool CaptureReady => _running && Volatile.Read(ref _frameSeq) > 0;

        // ---------------------------------------------------------------- 生命周期

        /// <summary>
        /// 启动 HTTP 服务。**纯 .NET，不碰 Unity API**，因此可以在 Plugin.Load() 里直接调。
        /// </summary>
        public static void Start(ManualLogSource log, int port, int width, int height, int fps, int quality,
                                 int streamWidth = 960)
        {
            _log = log;
            Port = port;
            Width = Math.Clamp(width, 160, 1920);
            Height = Math.Clamp(height, 120, 1080);
            // 0 = 不缩放推原图（旧行为）；否则按此宽度等比降采样。
            StreamWidth = Math.Clamp(streamWidth, 0, 1920);
            TargetFps = Math.Clamp(fps, 1, 30);
            JpegQuality = Math.Clamp(quality, 10, 95);

            if (_running)
            {
                L($"已在运行（端口 {Port}），忽略重复启动。");
                return;
            }

            try
            {
                // ★TcpListener 绑定普通端口不需要任何权限 —— 这正是弃用 HttpListener 的原因。
                //   绑Any 让手机能用局域网 IP 访问。
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();

                _running = true;
                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "MJPEG-Accept"
                };
                _acceptThread.Start();

                L($"HTTP 服务已启动 → http://<本机IP>:{Port}/phone");
                L($"推流 {TargetFps}fps，JPEG q{JpegQuality}，目标宽度 {StreamWidth}px"
                  + $"（0=原图直推）；已绑定 0.0.0.0:{Port}（不需要管理员权限）。");
                L($"实际推流尺寸与平均帧大小在首帧后打印（屏幕原图 → 目标宽度等比降采样）。");
                LogLanUrls();
                L("若手机仍打不开，多半是 Windows 防火墙拦了入站；日志里会打印排查命令。");
            }
            catch (SocketException e)
            {
                LastError = e.Message;
                L($"启动失败（SocketException, code={e.ErrorCode}）：{e.Message}");
                L("常见原因：端口已被占用。换一个 Port 配置再试。");
                _running = false;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                L($"启动失败：{e}");
                _running = false;
            }
        }

        /// <summary>
        /// ★已删除：RenderPipelineManager.endFrameRendering 订阅（2026-10-13 实测判死）。
        ///
        /// 【实测证据，不是猜测】游戏日志原文：
        ///   [MJPEG] ★挂endFrameRendering 失败：
        ///           System.ArgumentException: Type must derive from Delegate. (Parameter 'type')
        ///   [MJPEG] 若出现此日志，说明抓不到画面（无帧末回调 = 没有截图时机）。
        /// 之后连续 40 多行全是「服务在运行，但还没出帧」——
        /// **一帧都没产出过**。手机上看到的"黑屏"是下游症状，不是画面本身有问题。
        ///
        /// 【根因】IL2CPP interop 把事件委托类型生成为**不继承 System.Delegate 的类型**
        /// （与 IEventSystemHandler、Il2CppSystem.Collections.IEnumerator 完全同类）。
        /// 于是：`+=` 编译不过；`Delegate.CreateDelegate` 运行期抛 ArgumentException；
        /// **反射也救不回来** —— CreateDelegate 的第一个形参就要求真 Delegate。
        ///
        /// 【教训】★不要在 IL2CPP 项目里订阅 Unity 的 C# 事件。
        ///   解法必须选「静态方法、无委托参数」的 API。
        ///   本项目用的是 ScreenCapture.CaptureScreenshotIntoRenderTexture(rt)：
        ///   静态、单个 RenderTexture 参数、官方支持 SRP/HDRP、由主线程 Update 直接驱动。
        /// </summary>
        private static void HookRenderPipeline_Removed_DoNotReuse() { }
        public static void Stop()
        {
            if (!_running) return;
            _running = false;
            // 唤醒所有在等帧的 ServeMjpeg，让它们立刻看到 _running=false 退出。
            try { _frameReady.Set(); } catch { }
            try { _listener?.Stop(); } catch { /* 关停期异常无需处理 */ }
            _listener = null;
            L("HTTP 服务已停止。");
        }

        /// <summary>
        /// ★自愈：确保服务真的在监听。
        ///
        /// 实测教训（2026-10-13 18:18）：服务在 Plugin.Load 启动后，
        /// 被 Driver 的 OnDestroy → Stop()误杀，此后**再没有任何代码重启它**，
        /// 端口永久不再监听 → 手机直接"无法访问此网站"。
        /// 而且日志里"HTTP 服务已停止"夹在正常的场景切换中间，**极易被忽略**
        /// （我第一眼就误判成防火墙问题，浪费了一轮）。
        ///
        /// 这个方法由主线程的 Update() 每5 秒调一次：只要 `_running` 为 true
        /// 但监听器已失效，就重新绑定端口。属于廉价幂等的兜底。
        /// </summary>
        public static void EnsureAlive()
        {
            if (!_running) return;
            try
            {
                if (_listener != null && _listener.Server.IsBound)
                {
                    // 还要确认 accept线程真的活着（线程崩了监听也会变死）。
                    if (_acceptThread != null && _acceptThread.IsAlive) return;
                    L("accept 线程已死，重启 HTTP 服务。");
                }
                else
                {
                    L("监听器已失效（可能被误调用 Stop），重启 HTTP 服务。");
                }
            }
            catch
            {
                // 取IsBound 本身抛异常也视为失效
            }

            Restart();
        }

        private static void Restart()
        {
            try
            {
                try { _listener?.Stop(); } catch { /* 清理旧监听 */ }
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MJPEG-Accept" };
                _acceptThread.Start();
                _running = true;
                // ★不要在日志里写死任何 IP（公开仓库教训）：用户网络各不相同，
                //   而且硬编码的局域网地址等于公开别人的家庭网络拓扑。
                //   真实地址以游戏内二维码浮窗为准（QrOverlay 按选路出口 IP 排序）。
                L($"HTTP 服务已重启 → 0.0.0.0:{Port}（手机：http://<手机与电脑同网段的IP>:{Port}/phone，或扫游戏内浮窗二维码）");
            }
            catch (Exception e)
            {
                _running = false;
                L($"HTTP 服务重启失败：{e.Message}");
            }
        }

        /// <summary>主线程退出时释放 Unity 对象（★必须在主线程）。</summary>
        public static void DisposeUnityObjects()
        {
            try
            {
if (_readback != null) { UnityEngine.Object.Destroy(_readback); _readback = null; }
  if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); _rt = null; }
  _cam = null;
  // 降缩放目标（2026-10-14 新增）
  if (_streamRt != null) { try { _streamRt.Release(); } catch { } UnityEngine.Object.Destroy(_streamRt); _streamRt = null; }
  if (_streamTex != null) { UnityEngine.Object.Destroy(_streamTex); _streamTex = null; }
  _streamTargetW = 0; _streamTargetH = 0;
            }
            catch (Exception e)
            {
                L($"释放 Unity 对象异常（忽略）：{e.Message}");
            }
        }

        // ---------------------------------------------------- 主线程：每帧取帧编码

        /// <summary>
        /// 由主线程的 MonoBehaviour.Update() 每帧调用 —— **只负责发起请求，不等结果**。
        ///
        /// ★★★ 为什么改成"抓屏幕"而不是"渲染相机"（实测定位，18:36）——
        ///   游戏用 **HDRP**，实测两台相机都拿不到画面：
        ///     Main Camera      depth=-1  clear=SolidColor  cullingMask=0
        ///     UICamera(Clone)  depth=1   clear=DepthcullingMask=32
        ///   `Main Camera` 的 cullingMask=0 说明它**自己什么都不渲染**；
        ///   `UICamera(Clone)` 只清深度不画颜色。**两台都不是可离屏渲染的源**。
        ///   根因：**HDRP 下 `Camera.Render()` 是官方不支持的路径** —— 它静默不渲染，
        ///   **不报错、纹理保持全黑、帧数照常增长**（所以前几轮的"帧数在涨"判据全被掩盖）。
        ///
        ///   ⇒ 改为在 `RenderPipelineManager.endFrameRendering` 里
        ///     Blit 屏幕 → RenderTexture → **AsyncGPUReadback 异步回读**。
        ///   完全绕开"相机怎么渲染"这个问题，且拿到的正是**玩家眼睛看到的画面**
        ///   （含对话立绘与 UI，这更贴合"NPC 通话窗口"的语义）。
        ///
        /// ★★ 为什么必须用 AsyncGPUReadback 而不是 ReadPixels：
        ///   `ReadPixels` 是**同步**的，会等 GPU 排空命令队列（~4ms/帧），
        ///   在 60fps 游戏（帧预算 16.6ms）里等于每帧吃掉 1/4 预算 → 掉帧。
        ///   带宽其实不是瓶颈（640x480x4x15fps 仅 17.6 MB/s），
        ///   **真正的瓶颈是那次同步等待**。异步版~1.5ms 且不阻塞。
        ///
        /// ★限流：只按 TargetFps 发起，多余的帧直接丢弃 —— 宁可丢帧也不能让延迟增长。
        /// </summary>
        public static void Tick(float deltaTime)
        {
            if (!_running) return;

            // 限流：只按 TargetFps 发起，多余的帧直接丢弃 —— 宁可丢帧也不能让延迟增长。
            // 游戏 60fps、目标 15fps → 平均每 4 帧才真正抓 1 次，开销可摊薄。
            float interval = 1f / Math.Max(1, TargetFps);
            _accum += deltaTime;
            if (_accum < interval) return;
            _accum = 0f;

            // ★同步一步到位（无 RT、无异步回读、无委托），所以不需要任何"上一帧还没回来"的判断。
            CaptureScreenFrame();
        }

        private static void CaptureScreenFrame()
        {
            // ★★★ 零委托路线（2026-10-13 第三次换路，这是本项目抓帧的唯一可行解）★★★
            //
            // 【为什么必须是这个函数】interop 实测反射结果（sigprobe3 探针，非猜测）：
            //   UnityEngine.ScreenCapture:
            //     static Texture2D CaptureScreenshotAsTexture()          ← 零参数、零委托 ✅
            //     static Void CaptureScreenshotIntoRenderTexture(RenderTexture)
            //   UnityEngine.Rendering.AsyncGPUReadback:
            //     static AsyncGPUReadbackRequest Request(...) × 10 个重载
            //         ——**每一个的最后一个参数都是 Action<AsyncGPUReadbackRequest>**
            //         —— 没有任何「不传回调、自己 poll」的公开重载
            //         （Request_Internal_* 是私有的，拿不到）
            //     而 AsyncGPUReadbackRequest 虽有 Update()/done/WaitForCompletion()
            //         可以 poll，却**没有任何公开 API 能返回它而不传委托**。
            //
            // 【前两轮为什么都静默零帧】——同一个坑连踩 3 次，必须记住：
            //   第1 轮 RenderPipelineManager.endFrameRendering 事件 →委托建不起来（CreateDelegate 抛 ArgumentException）
            //   第 2 轮 AsyncGPUReadback 回调          → 委托建得起来但**传不进native**，回调永不触发
            //   第 3 轮（本轮）→ 改用零委托 API
            //   共同特征：**都不抛异常、不打日志、帧数恒为 0**。
            //   ⇒ 教训：IL2CPP interop 下凡是需要「委托」的 Unity API 一律不可用，
            //   且失败是**静默**的，不能靠"没报错"判断成功。
            //
            // 【性能说明】Unity 官方注明 CaptureScreenshotAsTexture 比
            //   CaptureScreenshotIntoRenderTexture 慢（内部有一次同步等待），
            //   但我们只在 15fps 限流下调用（游戏 60fps → 每 4 帧才抓一次），
            //   实测量级应在个位数 ms，可接受。**先证明能出画面，再谈性能。**

            Texture2D? tex = null;
            try
            {
                // 直接拿到可编码的 Texture2D —— 无 RT、无回读、无回调。
                tex = ScreenCapture.CaptureScreenshotAsTexture();

                if (tex == null)
                {
                    ReportCaptureProblem("CaptureScreenshotAsTexture 返回 null"
                        + "（可能尚未进入可渲染场景，或驱动不支持）。");
                    return;
                }

                int sw = tex.width;
                int sh = tex.height;

                // ★★★ 降缩放（2026-10-14 延迟优化，实测根因：推流码率超手机带宽）★★★
                //
                // 【改之前的实现】把全屏原图（本机实测 1920x1080）直接 EncodeToJPG：
                //   1080p q60 → 每帧约 250~600KB → 15fps = 4~9MB/s 持续下行。
                //   手机 Wi-Fi 吞吐经常只有 2~10Mbps ⇒ TCP 立即背压，
                //   ServeMjpeg 的 stream.Write 阻塞数秒等 socket 排空，
                //   手机屏幕上显示的是**几秒前**的画面 = 用户报的「延迟 3~5 秒」。
                //   （不是丢帧逻辑的问题——单槽 _latestFrame 设计本来就不会积压；
                //     积压发生在内核 socket 缓冲 + 链路本身。）
                //
                // 【现行做法】先 Graphics.Blit 到小 RT（GPU 双线性降采样）再编码：
                //   960 宽 = 1080p 的 1/4 像素 ⇒ 帧大小、编码耗时、手机解码耗时同步约 1/4。
                //   估算码率降到 ~1~2Mbps，任何 Wi-Fi 都吃得轻松，端到端延迟落到几百 ms。
                //
                // 【颜色空间论证（为什么 dest 用显式 Linear）】
                //   CaptureScreenshotAsTexture 回读出的是**无 sRGB 标志的 CPU 字节**
                //   （现行直推 JPEG 颜色正常即为反证：字节本身就是显示级 sRGB）。
                //   src 无标志 ⇒ GPU 采样不做解码；dest 用 Linear（不编码）⇒ 写回原值。
                //   两个颜色空间（Gamma/Linear 工程）下都是**字节恒等**，与直推完全一致。
                //   （若哪天发现手机颜色发白，把这里改成 RenderTextureReadWrite.sRGB 即可，
                //     那是 src 被打了 sRGB 标志的场景，属一行可回滚的改动。）
                int tw = sw;
                int th = sh;
                if (StreamWidth > 0 && sw > StreamWidth)
                {
                    tw = StreamWidth & ~1;                                   // 偶数宽
                    th = (int)Math.Round((double)sh * tw / sw) & ~1;         // 等比高
                    if (th < 2) th = 2;
                }

                byte[] bytes;
                if (tw >= sw && th >= sh)
                {
                    // 已经不大于源尺寸（或用户要求原图）→ 直推，不碰 RT。
                    bytes = tex.EncodeToJPG(JpegQuality);
                }
                else
                {
                    EnsureStreamTargets(tw, th);   // 主线程懒创建；仅尺寸变化才重建
                    if (_streamRt == null || _streamTex == null)
                    {
                        // 建目标失败也不能没画面 → 退回原图直推。
                        bytes = tex.EncodeToJPG(JpegQuality);
                        tw = sw; th = sh;
                    }
                    else
                    {
                        try
                        {
                            Graphics.Blit(tex, _streamRt);      // GPU 双线性降采样
                            var prevActive = RenderTexture.active;
                            RenderTexture.active = _streamRt;
                            try
                            {
                                _streamTex.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                            }
                            finally
                            {
                                RenderTexture.active = prevActive;
                            }
                            bytes = _streamTex.EncodeToJPG(JpegQuality);
                            _downscaleFails = 0;    // 成功即清零
                        }
                        catch (Exception e)
                        {
                            // ★保险（本项目被"静默零帧"坑过 3 轮，绝不重犯）：
                            //   降缩放链路任何一步异常 → 本帧立刻退回原图直推，
                            //   保证"最坏也是旧版的慢"，而不是"彻底没画面"。
                            //   连续失败 10 次则永久关闭降缩放并大声日志。
                            if (++_downscaleFails >= 10)
                            {
                                StreamWidth = 0;
                                L($"★降缩放连续失败 {_downscaleFails} 次（{e.Message}）"
                                  + $"→ 已永久关闭，恢复原图直推（码率回到 MB/s 级，"
                                  + $"手机延迟可能回升；请把此日志反馈给开发者）。");
                            }
                            bytes = tex.EncodeToJPG(JpegQuality);
                            tw = sw; th = sh;
                        }
                    }
                }

                if (bytes == null || bytes.Length == 0)
                {
                    ReportCaptureProblem("EncodeToJPG 返回空字节。");
                    return;
                }

                _latestFrame = bytes;
                Interlocked.Increment(ref _frameSeq);
                Interlocked.Add(ref _totalFrameBytes, bytes.Length);
                StreamW = tw;
                StreamH = th;

                // 首帧打印真实链路参数（取代原来"配置写了但没生效"的假日志）。
                if (_frameSeq == 1)
                {
                    L($"★推流就绪：屏幕 {sw}x{sh} → 推流 {tw}x{th}，"
                      + $"首帧 {bytes.Length / 1024}KB，q{JpegQuality}@{TargetFps}fps"
                      + $"（估算码率 {EstMbps:F1}Mbps；手机若仍延迟，看「背压」计数）。");
                }

                // 唤醒 ServeMjpeg：帧已编码完，立刻可发（零轮询空等）。
                _frameReady.Set();
                ClearCaptureProblem();
            }
            catch (Exception e)
            {
                ReportCaptureProblem("抓帧/编码异常：" + e.Message);
            }
            finally
            {
                // ★Texture2D 是 Unity 对象，必须在主线程销毁（这里就是主线程）。
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        // 降缩放目标（主线程持有，仅主线程访问）。
        private static RenderTexture? _streamRt;
        private static Texture2D? _streamTex;
        private static int _streamTargetW;
        private static int _streamTargetH;

        /// <summary>降缩放连续失败次数。达到 10 即永久关闭降缩放（见 CaptureScreenFrame 保险）。</summary>
        private static int _downscaleFails;

        /// <summary>
        /// 确保降缩放用的 RT / Texture2D 存在且尺寸匹配。**仅主线程调用**。
        /// 尺寸不变 → 零开销返回；变化（改配置/切分辨率/窗口缩放）才重建。
        /// </summary>
        private static void EnsureStreamTargets(int w, int h)
        {
            if (_streamRt != null && _streamTex != null
                && _streamTargetW == w && _streamTargetH == h)
                return;

            try { if (_streamRt != null) { _streamRt.Release(); UnityEngine.Object.Destroy(_streamRt); } }
            catch { /* 释放失败不致命，继续建新的 */ }
            try { if (_streamTex != null) UnityEngine.Object.Destroy(_streamTex); } catch { }
            _streamRt = null;
            _streamTex = null;

            try
            {
                // depthBuffer=0；ARGB32 + **显式 Linear**（颜色论证见 CaptureScreenFrame）。
                _streamRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
                                              RenderTextureReadWrite.Linear);
                _streamRt.Create();
                _streamTex = new Texture2D(w, h, TextureFormat.RGB24, false);
                _streamTargetW = w;
                _streamTargetH = h;
                L($"降缩放目标已就绪 {w}x{h}（GPU Blit + ReadPixels + EncodeToJPG）。");
            }
            catch (Exception e)
            {
                // 建不起来就保持 null → 调用方退回原图直推，绝不出黑屏。
                L($"★创建降缩放目标失败（{e.Message}）→ 本帧起退回原图直推（延迟会回升）。");
                try { if (_streamRt != null) UnityEngine.Object.Destroy(_streamRt); } catch { }
                try { if (_streamTex != null) UnityEngine.Object.Destroy(_streamTex); } catch { }
                _streamRt = null;
                _streamTex = null;
            }
        }

        /// <summary>
        /// 抓帧失败时**只打一次**同类原因，避免每 5 秒刷屏把真正的首因淹掉。
        /// </summary>
        private static void ReportCaptureProblem(string reason)
        {
            if (LastError == reason) return;
            LastError = reason;
            L("★抓帧失败：" + reason);
        }

        private static void ClearCaptureProblem()
        {
            LastError = null;
        }

private static float _accum;

        private static bool EnsureCamera()
        {
            if (_cam != null) return true;

            //延后到场景稳定后再找相机（sceneLoaded 时相机尚未就绪，这是实测事实）。
            var cams = UnityEngine.Object.FindObjectsOfType<UnityEngine.Camera>();
            if (cams == null || cams.Length == 0) return false;

            UnityEngine.Camera? pick = null;

            if (_requestedCamera >= 0 && _requestedCamera < cams.Length)
            {
                pick = cams[_requestedCamera];
                L($"按请求使用相机 #{_requestedCamera} '{pick.name}'。");
            }
            else
            {
                // ★黑屏根因（18:27 实测）：自动模式选到了 'UICamera(Clone)'（depth=1）。
                //   UI 相机**只负责把界面叠在最上层，自己不渲染 3D 场景**，
                //   所以画面全黑——而且不报错、帧数照常增长（我最初判据看的是
                //   "帧数在涨=链路通"，这恰好掩盖了"内容是黑的"）。
                //
                //   正确的判据不是 depth，而是**"清屏模式"**：
                //     · cullingMask == 0→ 什么都不渲染（纯叠加层）
                //     · clearFlags == Depth / Nothing → 不清颜色 → 纯叠加层
                //     · 否则 → 会真正渲染场景
                //   优先选"会渲染场景"里 depth 最小的那个（base camera）。
                pick = PickSceneCamera(cams);
            }

            if (pick == null) return false;
            _cam = pick;
            L($"已选定相机 '{pick.name}'（depth={pick.depth}, clear={pick.clearFlags}, "
              + $"cullingMask={pick.cullingMask}），推流宽度 {StreamWidth}px"
              + $"（0=原图）。");
            return true;
        }

        /// <summary>
        /// 在候选相机里挑「真正渲染场景的那个」，而不是「叠在最上面的那个」。
        ///
        /// ★判据必须用 clearFlags/cullingMask，不能只用 depth：
        ///   游戏的 UICamera(Clone) depth=1，而场景主相机 depth 可能是 -1/0/2 等任意值，
        ///   单靠"depth 最小"并不保证选对（实测就选错了）。
        ///   唯一可靠信号是"这台相机会不会清颜色并渲染东西"。
        /// </summary>
        private static UnityEngine.Camera? PickSceneCamera(UnityEngine.Camera[] cams)
        {
            UnityEngine.Camera? best = null;

            foreach (var c in cams)
            {
                // 叠加层判据：无cullingMask（什么都不画）/ 只清深度不清颜色。
                bool isOverlay = (c.cullingMask == 0)
                                 || (c.clearFlags == CameraClearFlags.Depth)
                                 || (c.clearFlags == CameraClearFlags.Nothing);

                if (isOverlay) continue;
                if (!c.enabled) continue;

                if (best == null || c.depth < best.depth) best = c;
            }

            if (best != null) return best;

            // 兜底：全都像叠加层 → 退回 depth 最小的那台（至少不是 null）。
            L("★警告：没找到明确的主相机，回退为 depth 最小者（画面可能全黑）。");
            foreach (var c in cams)
            {
                if (!c.enabled) continue;
                if (best == null || c.depth < best.depth) best = c;
            }
            return best ?? cams[0];
        }

        /// <summary>
        /// 列出所有相机（名字、depth、清屏模式、cullingMask），供手机端切换与诊断。
        /// 走的是 volatile 快照 —— Socket 线程不能碰 Unity API。
        /// </summary>
        private static string BuildCameraListJson()
        {
            // 主线程采集，Socket 线程只读这份快照。
            var sb = new StringBuilder();
            var snapshot = _cameraSnapshot;
            if (snapshot == null) return "{\"cams\":[]}";
            foreach (var line in snapshot) sb.Append(line);
            return "{\"cams\":[" + sb + "]}";
        }

        /// <summary>主线程写入的相机列表快照（每行一个 JSON 对象，末尾带逗号）。</summary>
        private static volatile string? _cameraSnapshot;

        /// <summary>由主线程调用，刷新相机列表快照（约 2 秒一次）。</summary>
        public static void RefreshCameraSnapshot()
        {
            try
            {
                var cams = UnityEngine.Object.FindObjectsOfType<UnityEngine.Camera>();
                if (cams == null) return;
                var sb = new StringBuilder();
                for (int i = 0; i < cams.Length; i++)
                {
                    var c = cams[i];
                    bool overlay = (c.cullingMask == 0)
                                   || (c.clearFlags == CameraClearFlags.Depth)
                                   || (c.clearFlags == CameraClearFlags.Nothing);
                    sb.Append("{\"i\":").Append(i.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"name\":\"").Append(c.name).Append('"')
                      .Append(",\"depth\":").Append(c.depth.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"clear\":\"").Append(c.clearFlags.ToString()).Append('"')
                      .Append(",\"mask\":").Append(c.cullingMask.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"enabled\":").Append(c.enabled ? "true" : "false")
                      .Append(",\"overlay\":").Append(overlay ? "true" : "false")
                      .Append(",\"current\":").Append(c == _cam ? "true" : "false")
                      .Append("},");
                }
                // 去掉最后一个逗号
                var s2 = sb.ToString();
                if (s2.EndsWith(",")) s2 = s2.Substring(0, s2.Length - 1);
                _cameraSnapshot = s2;
            }
            catch { /* 快照失败不影响主流程 */ }
        }

        // ------------------------------------------------------------ Socket 线程侧

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch
                {
                    // Stop() 会让 AcceptTcpClient 抛异常，属正常退出路径。
                    return;
                }

                Interlocked.Increment(ref _clientCount);
                // 每个连接丢到后台线程，避免拖慢取帧与其它连接。
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                // 小 JPEG 若被 Nagle 攒包，延迟会到 40ms+；关掉。
                client.NoDelay = true;

                // ★★必须设超时，否则僵死连接会永久占住浏览器连接池槽位。
                //   实测症状：手机页面里 /audio 反复重连（12 次连/断），
                //   旧连接在服务端这边「已断开但没回收」，浏览器那边也要等 TCP 超时
                //   （Windows 默认数分钟）才释放槽位 → 同源 6 条槽位很快占满
                //   → fetch('/type') 只能排队 → 点了发送几十秒后才生效，
                //     关掉网页时长连接一次性释放，请求才立刻被处理。
                //   ReceiveTimeout：客户端连上但不发请求头时的僵死连接（防挂死）。
                //   SendTimeout：客户端不再读取时的僵死连接（★真正解决堆积的那个）。
                try { client.ReceiveTimeout = 15000; } catch { }
                try { client.SendTimeout = 20000; } catch { }

                using var stream = client.GetStream();
                // ★★ 解码器必须是 UTF-8，不能用 ASCII（离线测试 typetest 抓到的真 bug）。
                //   ASCIIStreamReader 遇到 ≥0x80 的字节会**逐字节替换成 '?'**，
                //   于是手机发来的中文 UTF-8（"你好"=6 字节）到服务端变成 "??????"，
                //   写进游戏输入框全是问号。
                //   header 全是 ASCII，而 ASCII 是 UTF-8 的**真子集**，
                //   所以换成 UTF-8 后请求行/头的解析结果完全不变，只有 body 恢复正常。
                //   detectEncodingFromByteOrderMarks:false —— 不要让BOM 探测改变行为。
                using var reader = new StreamReader(
                    stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);

                var req = ReadRequest(reader, stream);
                if (req == null) return;

                // ★★CORS 预检必须在这里拦下（详见 ControlServer.Dispatch 里的完整说明）：
                //   POST /type 带 application/json 属非简单请求，浏览器先发 OPTIONS；
                //   若让它掉进下面的 switch 会得到 404 → 真 POST 永远发不出去。
                if (req.Method == "OPTIONS")
                {
                    ServePreflight(stream);
                    return;
                }

                switch (req.Path)
                {
                    case "/mjpeg":
                        ServeMjpeg(stream);
                        break;
                    case "/shot":
                        ServeShot(stream, req.Query);
                        break;
                    case "/phone":
                    case "/":
                    case "/index.html":
                        ServePhonePage(stream);
                        break;
                    case "/status":
                        ServeBytes(stream, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(BuildStatusJson()));
                        break;
                    case "/cams":
                        ServeBytes(stream, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(BuildCameraListJson()));
                        break;
                    case "/pickcam":
                        // ?i=N —— 切换相机。诊断黑屏时直接在手机浏览器里试，不用重启游戏。
                        if (req.Query.TryGetValue("i", out var idxStr)
                            && int.TryParse(idxStr, out int idx))
                        {
                            SetRequestedCamera(idx);
                            ServeBytes(stream, "application/json; charset=utf-8",
                                Encoding.UTF8.GetBytes("{\"ok\":true,\"cameraIndex\":" + idx + "}"));
                        }
                        else
                        {
                            ServeBytes(stream, "text/plain; charset=utf-8",
                                Encoding.UTF8.GetBytes("usage: /pickcam?i=N"), status: "400 Bad Request");
                        }
                        break;
                    case "/cmd":
                        HandleCommand(stream, req);
                        break;
                    case "/tap":
                        HandleTap(stream, req);
                        break;
                    case "/type":
                        HandleType(stream, req);
                        break;
                    case "/audio":
                        // ★声音下行与画面**同端口**（曾独立 8896，因跨域 + 独立生命周期
                        //   出过两个 bug，已合并）。详见 AudioForwarder 类注释。
                        AudioForwarder.ServeStream(stream);
                        break;
                    case "/astatus":
                        ServeBytes(stream, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(AudioForwarder.BuildStatusJson()));
                        break;
                    default:
                        ServeBytes(stream, "text/plain; charset=utf-8",
                            Encoding.UTF8.GetBytes("not found: " + req.Path), status: "404 Not Found");
                        break;
                }
            }
            catch
            {
                // 客户端关掉页面是正常路径，静默退出。
            }
            finally
            {
                Interlocked.Decrement(ref _clientCount);
                try { client.Close(); } catch { /* 已断开 */ }
            }
        }

        /// <summary>
        /// 解析一个 HTTP 请求：请求行 + 头 + 按 Content-Length 读body。
        /// 返回 null 表示不是有效请求（直接忽略，不回错误）。
        /// </summary>
        private static HttpRequest? ReadRequest(StreamReader reader, Stream stream)
        {
            // 逐行读直到空行（头结束）。设置 ReadTimeout 防挂死连接。
            var raw = new StringBuilder();
            string? line;
            int headerCount = 0;

            while (headerCount++ < 64)
            {
                try { line = reader.ReadLine(); }
                catch (IOException) { return null; }   // ReadTimeout 等价效果

                if (line == null) return null;
                if (line.Length == 0) break;           // 空行 = 头结束

                raw.Append(line).Append('\n');
            }

            var all = raw.ToString();
            if (all.Length == 0) return null;

            // 请求行形如：GET /mjpeg?cam=0 HTTP/1.1
            var eol = all.IndexOf('\n');
            if (eol < 0) return null;

            var parts = all.Substring(0, eol).Trim().Split(' ');
            if (parts.Length < 2) return null;

            string method = parts[0];
            string target = parts[1];

            string path = target;
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int q = target.IndexOf('?');
            if (q >= 0)
            {
                path = target.Substring(0, q);
                foreach (var kv in target.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0)
                        query[kv.Substring(0, eq)] = Uri.UnescapeDataString(kv.Substring(eq + 1));
                    else
                        query[kv] = "";
                }
            }

                // POST body：按 Content-Length 精确读取（手机端只发小 JSON，不做 chunked）。
                //
                // ★★按 **UTF-8 字节数** 读，不是 char 数（2026-10-14 修复）。
                //   Content-Length 是字节数；旧代码 `new char[len]` + `while (got < len)`
                //   把它当 char 数。含中文时字节 > char（"你好" 6 字节 / 2 char），
                //   reader.Read 永远等不满 → 阻塞到连接关闭 → 浏览器等不到响应
                //   → fetch 显示「发送失败」，而关网页时积压请求才被一次性处理
                //   （用户实测「关掉网页才发出去」的根因，ControlServer 同款 bug）。
                //   逐 char 读 + GetByteCount 累加，达标即停，不会多等一个 char。
                string body = "";
                foreach (var h in all.Split('\n'))
                {
                    int colon = h.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = h.Substring(0, colon).Trim();
                    if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(h.Substring(colon + 1).Trim(), out int len)) break;
                    if (len <= 0 || len > 64 * 1024) break;

                    var sbBody = new StringBuilder();
                    int byteCount = 0;
                    while (byteCount < len)
                    {
                        int c = reader.Read();
                        if (c < 0) break;
                        sbBody.Append((char)c);
                        byteCount += Encoding.UTF8.GetByteCount(new[] { (char)c });
                    }
                    body = sbBody.ToString();
                    break;
                }

            return new HttpRequest(method, path, query, body);
        }

        private sealed class HttpRequest
        {
            public readonly string Method;
            public readonly string Path;
            public readonly Dictionary<string, string> Query;
            public readonly string Body;

            public HttpRequest(string m, string p, Dictionary<string, string> q, string b)
            {
                Method = m; Path = p; Query = q; Body = b;
            }

            public string Q(string key, string def = "") =>
                Query.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : def;
        }

        private static void ServeMjpeg(Stream stream)
        {
            Interlocked.Increment(ref _mjpegClients);
            L($"MJPEG 客户端已连接（当前 {_mjpegClients} 个）。");

            try
            {
                // ★multipart/x-mixed-replace 的标准头。
                //   用 HTTP/1.0 + Connection: close 且**不给 Content-Length**：
                //   流以「连接关闭」定界，浏览器才能持续显示而不等完整响应。
                WriteHead(stream, "200 OK", "multipart/x-mixed-replace; boundary=frame",
                    extra: "Cache-Control: no-cache, no-store, must-revalidate\r\n"
                          + "Pragma: no-cache\r\n");

                int lastSeq = -1;
                int idleRounds = 0;
                // 一帧间隔（ms）：写出慢于它就是背压（手机链路吃不下）。
                int frameIntervalMs = Math.Max(1, 1000 / Math.Max(1, TargetFps));
                var writeClock = new System.Diagnostics.Stopwatch();

                while (_running)
                {
                    // ★事件唤醒取代 Sleep(15) 轮询：
                    //   编码完 _frameReady.Set() → 这里微秒级醒来，不再平均白等 7.5ms；
                    //   先 Reset 再读 _latestFrame —— Reset 之前到来的帧不会被漏
                    //   （event 会保持置位直到下次循环顶部才被清）。
                    _frameReady.Reset();
                    var frame = _latestFrame;
                    int seq = Volatile.Read(ref _frameSeq);

                    if (frame == null || seq == lastSeq)
                    {
                        // 没有新帧：阻塞等待（超时是为了能看到 _running 变 false）。
                        _frameReady.Wait(250);
                        if (++idleRounds > 24)   // 约 6 秒无新帧
                        {
                            idleRounds = 0;
                            L("MJPEG 客户端连接期间超过 6 秒没有新帧（取帧可能已停止）。");
                        }
                        continue;
                    }

                    idleRounds = 0;
                    lastSeq = seq;

                    // multipart 分段：分隔头 + JPEG 负载（每段以 \r\n 结尾）
                    var header = Encoding.ASCII.GetBytes(
                        "--frame\r\nContent-Type: image/jpeg\r\nContent-Length: "
                        + frame.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");

                    // ★写出耗时计时：这是「手机链路吃不吃得下」的客观指标。
                    //   单帧 write 经常 > frameIntervalMs ⇒ socket 一直在背压，
                    //   屏幕上必然是旧画面（与帧大小一起构成完整的延迟自证链）。
                    writeClock.Restart();
                    stream.Write(header, 0, header.Length);
                    stream.Write(frame, 0, frame.Length);
                    stream.WriteByte(0x0D);
                    stream.WriteByte(0x0A);
                    stream.Flush();
                    writeClock.Stop();

                    int wms = (int)writeClock.ElapsedMilliseconds;
                    _lastWriteMs = wms;
                    if (wms > frameIntervalMs)
                    {
                        Interlocked.Increment(ref _slowWrites);
                        if (wms > _maxWriteMs)
                            _maxWriteMs = wms;
                    }
                }
            }
            catch
            {
                // 客户端关掉页面是正常路径，静默退出。
            }
            finally
            {
                Interlocked.Decrement(ref _mjpegClients);
                L("MJPEG 客户端已断开。");
            }
        }

        private static int _mjpegClients;

        /// <summary>由 Socket 线程调用：请求切换相机（下一次主线程 Tick 生效）。</summary>
        public static void SetRequestedCamera(int idx)
        {
            _requestedCamera = idx;
            _cam = null;      // 让主线程重新选
            L($"已请求切换相机 → i={idx}（主线程下一帧生效）。");
        }

        private static void ServeShot(Stream stream, Dictionary<string, string> q)
        {
            // 允许查询参数切换相机（下次 Tick 生效）。
            if (q.TryGetValue("cam", out var camStr)
                && int.TryParse(camStr, out int cam)
                && cam != _requestedCamera)
            {
                SetRequestedCamera(cam);
            }

            var frame = _latestFrame;
            if (frame == null)
            {
                // 还没出帧：主动等一小会儿再取（Socket 线程，不能直接取帧）。
                for (int i = 0; i < 20 && frame == null; i++)
                {
                    Thread.Sleep(50);
                    frame = _latestFrame;
                }
            }

            if (frame == null)
            {
                ServeBytes(stream, "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("no frame yet"), status: "503 Service Unavailable");
                return;
            }
            ServeBytes(stream, "image/jpeg", frame);
        }

        private static void ServePhonePage(Stream stream)
        {
            ServeBytes(stream, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PhonePage.Html));
        }

        private static void HandleCommand(Stream stream, HttpRequest req)
        {
            // ★支持两种形式（前端已改为优先 sendBeacon，它发的是 POST 空 body）：
            //   ① GET  ?c=xxx[&t=...]      —— 手动测试/兜底 fetch
            //   ② POST body= JSON / 空     —— navigator.sendBeacon
            string action = req.Q("c");
            string body = req.Body;

            if (string.IsNullOrEmpty(action) && !string.IsNullOrEmpty(body))
            {
                // 去掉常见的 JSON 外壳 {"c":"look"}，取里面的值。
                var trimmed = body.Trim();
                int ci = trimmed.IndexOf("\"c\"", StringComparison.Ordinal);
                if (ci >= 0)
                {
                    int q1 = trimmed.IndexOf('"', trimmed.IndexOf(':', ci) + 1);
                    if (q1 > 0)
                    {
                        int q2 = trimmed.IndexOf('"', q1 + 1);
                        if (q2 > q1)
                            action = trimmed.Substring(q1 + 1, q2 - q1 - 1);
                    }
                }
                if (string.IsNullOrEmpty(action)) action = trimmed;
            }

            if (!string.IsNullOrEmpty(action))
            {
                lock (_cmdLock)
                {
                    _pendingCommands.Enqueue(action);
                    if (_pendingCommands.Count > 32) _pendingCommands.Dequeue();  // 防堆积
                }
                Interlocked.Increment(ref _cmdRxCount);
                L($"收到操作回传：{Truncate(action, 120)}（队列 {_pendingCommands.Count} 条）");
            }
            else
            {
                L("★收到 /cmd 但没解析出动作（检查前端是否带了 ?c= 参数或 body）。");
            }

            ServeBytes(stream, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes("{\"ok\":true}"));
        }

        /// <summary>
        /// 接收手机端在**画面上的点击**（归一化坐标）→ 交给主线程注入鼠标。
        ///
        /// ★协议：GET /tap?x=0.5&y=0.3&cover=1
        ///   x/y 是[0,1] 的归一化坐标，**由前端按 object-fit 反算好**。
        ///   cover=1 表示当前是竖屏（object-fit:cover，会裁切两侧）。
        ///
        /// ★为什么用 sendBeacon 一样的方式：这也是高频小请求，
        ///   必须绕开浏览器连接池（MJPEG 长连接占着）。
        /// </summary>
        private static void HandleTap(Stream stream, HttpRequest req)
        {
            bool okX = float.TryParse(req.Q("x"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float nx);
            bool okY = float.TryParse(req.Q("y"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float ny);
            bool cover = req.Q("cover") == "1";

            if (okX && okY)
            {
                TapInjector.EnqueueTap(nx, ny, cover);
                L($"收到点击：({nx:F3}, {ny:F3}){(cover ? " [cover]" : " [contain]")}");
            }
            else
            {
                L("★收到 /tap 但坐标解析失败（需要 x= 与 y= 两个参数）。");
            }

            ServeBytes(stream, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes("{\"ok\":true}"));
        }

        /// <summary>
        /// 手机端打字：POST /type  body = {text, send}
        ///   · send=1 → 写入后自动点游戏自己的发送按钮
        ///   · send=0 → 只填入文本，让用户手动点发送
        ///
        /// ★为什么必须用 POST：中文文本放 URL query 会有编码/长度风险，
        ///   body 更干净（ReadRequest 已支持按 Content-Length 读 body）。
        /// </summary>
        private static void HandleType(Stream stream, HttpRequest req)
        {
            string raw = req.Body ?? "";
            string text = "";
            bool send = false;

            try
            {
                // 极简 JSON 取值：找"text" 与 "send" 两个键。
                // 不引Newtonsoft（虽然项目已有依赖），这里字段固定，手写更省。
                int ti = raw.IndexOf("\"text\"", StringComparison.Ordinal);
                if (ti >= 0)
                {
                    int colon = raw.IndexOf(':', ti);
                    int q1 = colon > 0 ? raw.IndexOf('"', colon + 1) : -1;
                    if (q1 > 0)
                    {
                        var sb = new StringBuilder();
                        int i = q1 + 1;
                        while (i < raw.Length)
                        {
                            char c = raw[i];
                            if (c == '\\' && i + 1 < raw.Length)
                            {
                                // 简单反转义：\\n \" \\
                                char n = raw[i + 1];
                                sb.Append(n switch
                                {
                                    'n' => '\n',
                                    't' => '\t',
                                    '"' => '"',
                                    '\\' => '\\',
                                    _ => n,
                                });
                                i += 2;
                                continue;
                            }
                            if (c == '"') break;
                            sb.Append(c);
                            i++;
                        }
                        text = sb.ToString();
                    }
                }

                int si = raw.IndexOf("\"send\"", StringComparison.Ordinal);
                if (si >= 0)
                {
                    int colon = raw.IndexOf(':', si);
                    if (colon > 0)
                    {
                        var seg = raw.Substring(colon + 1).TrimStart();
                        send = seg.StartsWith("true", StringComparison.OrdinalIgnoreCase)
                            || seg.StartsWith("1", StringComparison.Ordinal);
                    }
                }
            }
            catch (Exception e)
            {
                L("★解析 /type body 失败：" + e.Message);
            }

            if (string.IsNullOrEmpty(text))
            {
                L("★收到 /type 但没解析出文字（body 需含 \"text\" 字段）。");
            }
            else
            {
                // 去掉换行：单行输入框里换行会出问题
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                TypeInjector.EnqueueType(text, send);
                L($"收到输入（{text.Length} 字，send={send}）：{Truncate(text, 60)}");
            }

            ServeBytes(stream, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes("{\"ok\":true}"));
        }

        /// <summary>累计收到的操作指令数（诊断：0 说明前端根本没发到）。</summary>
        public static int CommandCount => Volatile.Read(ref _cmdRxCount);
        private static int _cmdRxCount;

        /// <summary>主线程取出待处理的回传指令（由 CommandRouter 调用）。</summary>
        public static bool TryDequeueCommand(out string cmd)
        {
            lock (_cmdLock)
            {
                if (_pendingCommands.Count > 0)
                {
                    cmd = _pendingCommands.Dequeue();
                    return true;
                }
            }
            cmd = "";
            return false;
        }

        private static string BuildStatusJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"running\":").Append(_running ? "true" : "false").Append(',');
            sb.Append("\"port\":").Append(Port.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"frames\":").Append(FrameCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"fps\":").Append(TargetFps.ToString(CultureInfo.InvariantCulture)).Append(',');
            // ★2026-10-14：size 从「配置里写的假尺寸」改为「实际推流尺寸」。
            //   另加带宽/背压诊断字段——用户判断延迟到底降没降，靠这些数字自证，
            //   而不是靠"感觉"。
            sb.Append("\"size\":\"").Append(StreamW).Append('x')
              .Append(StreamH).Append("\",");
            sb.Append("\"streamWidth\":").Append(StreamWidth.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"avgFrameKB\":").Append((AvgFrameBytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"estMbps\":").Append(EstMbps.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"lastWriteMs\":").Append(LastWriteMs.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"maxWriteMs\":").Append(MaxWriteMs.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"slowWrites\":").Append(SlowWrites.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"camera\":\"").Append(_cam != null ? _cam.name : "none").Append("\",");
            sb.Append("\"clients\":").Append(ClientCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"mjpegClients\":").Append(Volatile.Read(ref _mjpegClients).ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"pendingCmds\":").Append(PendingCount.ToString(CultureInfo.InvariantCulture)).Append(',');
     // ★指令累计数：手机页显示它，用户一眼就能判断"点了按钮到底有没有到游戏"。
            sb.Append("\"cmdRx\":").Append(CommandCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            // ★点击透传统计：tapRx/tapInjected 一眼看出"点了但没生效"还是"根本没点到"
            sb.Append("\"tapRx\":").Append(TapInjector.ReceivedCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"tapInjected\":").Append(TapInjector.InjectedCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"typeRx\":").Append(TypeInjector.ReceivedCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"typeOk\":").Append(TypeInjector.OkCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"typeFail\":").Append(TypeInjector.FailCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            // ★声音链路并入同一端口后，状态也能一次轮询拿到，不用再跨域请求 8896。
            sb.Append("\"audioChunks\":").Append(AudioForwarder.ChunkCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"audioClients\":").Append(AudioForwarder.ClientCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"hasAudio\":").Append(AudioForwarder.HasAudio ? "true" : "false").Append(',');
            // ★音频诊断：音源模式 + 积压毫秒数。
            //   backlogMs 持续 > 300 说明手机端消费不过来（会丢字）；
            //   接近 0 说明手机端实时跟随（正常）。
            sb.Append("\"audioMode\":\"").Append(AudioForwarder.SourceMode).Append("\",");
            sb.Append("\"audioBacklogBytes\":").Append(AudioForwarder.BufferedBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"audioDevice\":\"").Append(AudioForwarder.DeviceName.Replace("\"", "")).Append("\",");
            sb.Append("\"typeError\":\"").Append(TypeInjector.LastError.Replace("\"", "")).Append("\",");
            // ★ASR 语音输入状态（2026-10-14）：手机「语音」按钮轮询它，跟随 PC 侧
            //   状态变化（游戏内麦克风按钮 / ESC / 60s 上限都会改状态，
            //   手机按钮必须同步复位，不能自己以为还在录）。
            sb.Append("\"asrAvailable\":").Append(StreamingAsr.Available ? "true" : "false").Append(',');
            sb.Append("\"asrState\":\"").Append((StreamingAsr.State ?? "").Replace("\"", "")).Append("\",");
            sb.Append("\"asrActive\":").Append(StreamingAsr.IsActive ? "true" : "false").Append(',');
            sb.Append("\"asrRecording\":").Append((StreamingAsr.IsActive && !StreamingAsr.TestMode) ? "true" : "false").Append(',');
            sb.Append("\"asrReason\":\"").Append((StreamingAsr.UnavailableReason ?? "").Replace("\"", "")).Append("\",");
            sb.Append("\"error\":\"").Append(LastError ?? "").Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        private static int PendingCount
        {
            get { lock (_cmdLock) { return _pendingCommands.Count; } }
        }

        // ------------------------------------------------------------ 底层写响应

        /// <summary>
        /// 写 HTTP 响应头（HTTP/1.0 + Connection: close + 精确 Content-Length）。
        /// 手写而不是用 HttpListener 的原因见类注释。
        /// </summary>
        private static void WriteHead(Stream stream, string status, string contentType,
                                      string? extra = null, long? contentLength = null)
        {
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.0 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (contentLength.HasValue)
                sb.Append("Content-Length: ").Append(contentLength.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Server: A1Mjpeg/1.0\r\n");
            // ★no-store 必须有：缺它→ 手机浏览器缓存旧 HTML/JS，用户反复开关
            //   页面拿到的还是旧版（表现为「改了没生效」，极难排查）。
            sb.Append("Cache-Control: no-cache, no-store, must-revalidate\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
            if (!string.IsNullOrEmpty(extra)) sb.Append(extra);
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>CORS 预检响应（204）。缺它→ POST /type 的预检 404 → 打字永远发不出去。</summary>
        private static void ServePreflight(Stream stream)
        {
            try
            {
                var sb = new StringBuilder(200);
                sb.Append("HTTP/1.1 204 No Content\r\n");
                sb.Append("Access-Control-Allow-Origin: *\r\n");
                sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                sb.Append("Access-Control-Max-Age: 600\r\n");
                sb.Append("Content-Length: 0\r\n");
                sb.Append("Connection: close\r\n");
                sb.Append("\r\n");
                var bytes = Encoding.ASCII.GetBytes(sb.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch { /* 客户端已断开 */ }
        }

        private static void ServeBytes(Stream stream, string contentType, byte[] data,
                                       string status = "200 OK")
        {
            try
            {
                WriteHead(stream, status, contentType, contentLength: data.Length);
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
            catch
            {
                // 客户端可能已断开
            }
        }

        // ---------------------------------------------------------------- 控制通道入口
        //
        // ★这三个方法是给 <see cref="ControlServer"/>（独立端口 8894）复用的。
        //   为什么不直接把控制端点从 8895 删掉、只留 8894？
        //     —— 删掉会让「端口被防火墙挡住 / 服务没起来」这类问题**没有降级路径**：
        //     万一 8894 放行失败，8895 上的老端点仍能工作，便于对比排查。
        //     保留的代价只是几十行路由代码，不是重复的业务逻辑（下面全部转调原方法）。
        //   ★要守住的铁律：**业务逻辑只有一份**。这里只做「参数搬运」，不做任何解析或判断，
        //     否则两个端口的行为会悄悄分叉（此前「独立端口自找bug」的根源就是逻辑分叉）。

        /// <summary>控制通道：打字请求。参数与 8895 的 /type 完全一致。</summary>
        internal static void HandleTypeControl(Stream stream, string body,
            Dictionary<string, string>? query = null)
        {
            // query 一并带上：本方法是 POST 形式（参数在 body），但保留 query 合并
            // 让 GET /type?text=.. 的降级调用也能工作（铁律：两个端口行为不分叉）。
            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (query != null)
                foreach (var kv in query) q[kv.Key] = kv.Value;
            HandleType(stream, new HttpRequest("POST", "/type", q, body ?? ""));
        }

        /// <summary>控制通道：点击透传。<paramref name="query"/> 形如 <c>x=..&amp;y=..&amp;cover=..</c>。</summary>
        internal static void HandleTapControl(Stream stream, string body,
            Dictionary<string, string>? query = null)
        {
            // ★query 优先拷贝进来：手机端点击是 GET /tap?x=..&y=..（坐标在 query，
            //   body 为空）。旧代码只解析 body 的 JSON → 服务端收到请求却解析不到
            //   坐标（日志刷「坐标解析失败」），点击全部丢失。
            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (query != null)
                foreach (var kv in query) q[kv.Key] = kv.Value;
            // 控制通道也支持 body 传参（POST），两种都兼容，避免前端切换端口时
            // 还要改参数传递方式。
            string raw = body ?? "";
            if (raw.StartsWith("{", StringComparison.Ordinal))
            {
                // JSON: {"x":0.5,"y":0.3,"cover":true}
                foreach (var key in new[] { "x", "y" })
                {
                    int ki = raw.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                    if (ki < 0) continue;
                    int colon = raw.IndexOf(':', ki);
                    if (colon < 0) continue;
                    int end = colon + 1;
                    while (end < raw.Length && (char.IsDigit(raw[end]) || raw[end] == '-'
                           || raw[end] == '.' || raw[end] == '+')) end++;
                    if (end > colon + 1) q[key] = raw.Substring(colon + 1, end - colon - 1);
                }
                if (raw.IndexOf("\"cover\"", StringComparison.Ordinal) >= 0
                    && raw.IndexOf("true", StringComparison.Ordinal) >= 0)
                    q["cover"] = "1";
            }
            HandleTap(stream, new HttpRequest("POST", "/tap", q, ""));
        }

        /// <summary>控制通道：页面按钮指令。body 可为空（beacon 形式）或含 JSON 外壳。</summary>
        internal static void HandleCmdControl(Stream stream, string body,
            Dictionary<string, string>? query = null)
        {
            // /cmd 的手机端形式是 GET /cmd?c=look（动作在 query）。
            //   旧代码只传 body → c 丢失 → 「没解析出动作」。与 /tap 同一类错误。
            var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (query != null)
                foreach (var kv in query) q[kv.Key] = kv.Value;
            HandleCommand(stream, new HttpRequest("POST", "/cmd", q, body ?? ""));
        }

        /// <summary>状态 JSON（供控制通道复用，逻辑与 8895 的 /status 完全一致）。</summary>
        internal static string BuildStatusJsonPublic() { return BuildStatusJson(); }

        /// <summary>打印本机所有可用于手机访问的 URL，省得用户去猜 IP。</summary>
        private static void LogLanUrls()
        {
            try
            {
                var addrs = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
                foreach (var a in addrs)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                        L($"  → 手机访问：http://{a}:{Port}/phone");
                }
            }
            catch (Exception e)
            {
                L($"枚举本机 IP 失败（不影响服务）：{e.Message}");
                L($"  → 手机访问：http://<本机IP>:{Port}/phone");
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        internal static void L(string msg)
        {
            try { _log?.LogInfo(LogPrefix + msg); } catch { /* 日志失败忽略 */ }
        }
    }
}