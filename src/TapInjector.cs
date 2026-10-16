using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace A1IndexTTSMod
{
    /// <summary>
    /// 手机点击 → PC 鼠标点击（把画面变成可交互的远程桌面式操作）。
    ///
    /// ★为什么需要这个：手机上的画面是 MJPEG 图片，**像素本身不可点击**。
    ///   用户看到的是游戏里真实的对话选项按钮，想点它们就必须把坐标映射回 PC 屏幕，
    ///   再注入一次真实鼠标点击（远程桌面/云游戏的标准做法）。
    ///
    /// 【坐标映射链路（最容易出错的一环）】
    ///   手机点击 (px,py) 落在&lt;img&gt; 上
    ///     → 减去元素左上角 → 得到元素内坐标
    ///     → 按 object-fit 反算图片实际绘制区域（contain有黑边 / cover 会裁切）
    ///     → 归一化到 [0,1]（由前端算，见 PhonePage）
    ///     → 乘以游戏窗口客户区尺寸 + 窗口左上角 → 屏幕绝对坐标
    ///     → SetCursorPos + mouse_event
    ///
    /// ★★关键：object-fit 的反算必须和前端 CSS 完全一致，否则点偏。
    ///   contain：图片完整显示，两侧/上下有黑边 → 点击落在黑边上时**不应**转发
    ///   cover  ：图片填满容器，超出部分被裁 → 任何点击都在图内，可直接映射
    ///   本类不自己算（前端算好归一化坐标），但会**校验范围**并记录裁切情况。
    ///
    /// 【IL2CPP 约束】★所有 Unity 对象访问必须在主线程。
    ///   HTTP 线程只把 (nx,ny) 入队；主线程的 Update 里取出并执行注入。
    /// </summary>
    public static class TapInjector
    {
        private const string LogPrefix = "[TAP] ";

        // ---- 待处理点击队列（HTTP 线程写，主线程读）----
        private const int QueueMax = 16;
        private static readonly float[] _queueX = new float[QueueMax];
        private static readonly float[] _queueY = new float[QueueMax];
        private static readonly bool[] _queueCover = new bool[QueueMax];
        private static int _queueCount;
        private static readonly object _queueLock = new object();

        // ---- 统计（诊断）----
        private static int _tapRxCount;
        private static int _tapInjectedCount;
        private static int _tapOutOfRangeCount;
        private static int _tapFailCount;
        private static string _lastError = "";

        // ---- 缓存的游戏窗口信息 ----
        private static IntPtr _gameWindow;
        private static Rect _clientRectScreen = new Rect(0, 0, 1, 1);
        private static string _windowInfo = "未探测";

        public static int ReceivedCount => Volatile.Read(ref _tapRxCount);
        public static int InjectedCount => Volatile.Read(ref _tapInjectedCount);
        public static int OutOfRangeCount => Volatile.Read(ref _tapOutOfRangeCount);
        public static int FailCount => Volatile.Read(ref _tapFailCount);
        public static string WindowInfo => _windowInfo;
        public static string LastError => _lastError;

        private static void L(string s) => MjpegServer.L(LogPrefix + s);

        // ------------------------------------------------------------ P/Invoke

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        // ------------------------------------------------------------ 对外接口

        /// <summary>
        /// 接收一次点击（HTTP 线程调用，**不碰 Unity API**）。
        /// </summary>
        /// <param name="nx">归一化 X，[0,1]</param>
        /// <param name="ny">归一化 Y，[0,1]</param>
        /// <param name="cover">前端当前是否用 object-fit: cover（竖屏模式）</param>
        public static void EnqueueTap(float nx, float ny, bool cover)
        {
            Interlocked.Increment(ref _tapRxCount);

            if (nx < 0f || nx > 1f || ny < 0f || ny > 1f)
            {
                Interlocked.Increment(ref _tapOutOfRangeCount);
                if (_lastError != $"坐标越界 nx={nx:F3} ny={ny:F3}")
                {
                    _lastError = $"坐标越界 nx={nx:F3} ny={ny:F3}";
                    L("★收到越界坐标（前端映射可能算错了）：" + _lastError);
                }
                return;
            }

            lock (_queueLock)
            {
                if (_queueCount >= QueueMax)
                {
                    // 队列满：丢掉最旧的（用户狂点时宁可漏点也不要延迟累积）
                    for (int i = 0; i < QueueMax - 1; i++)
                    {
                        _queueX[i] = _queueX[i + 1];
                        _queueY[i] = _queueY[i + 1];
                        _queueCover[i] = _queueCover[i + 1];
                    }
                    _queueCount = QueueMax - 1;
                }
                _queueX[_queueCount] = nx;
                _queueY[_queueCount] = ny;
                _queueCover[_queueCount] = cover;
                _queueCount++;
            }
        }

        /// <summary>
        /// 由主线程 Update 每帧调用：取出队列里的点击并注入到游戏窗口。
        /// </summary>
        public static void PumpOnMainThread()
        {
            if (_queueCount == 0) return;

            // 先把队列整体取出来（HTTP 线程可能正在写）
            var xs = new float[_queueCount];
            var ys = new float[_queueCount];
            var covers = new bool[_queueCount];
            int n;
            lock (_queueLock)
            {
                n = _queueCount;
                for (int i = 0; i < n; i++)
                {
                    xs[i] = _queueX[i];
                    ys[i] = _queueY[i];
                    covers[i] = _queueCover[i];
                }
                _queueCount = 0;
            }

            for (int i = 0; i < n; i++)
            {
                try { InjectOne(xs[i], ys[i], covers[i]); }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _tapFailCount);
                    if (_lastError != e.Message)
                    {
                        _lastError = e.Message;
                        L("★点击注入异常：" + e.Message);
                    }
                }
            }
        }

        /// <summary>注入一次真实鼠标点击（★必须在主线程）。</summary>
        private static void InjectOne(float nx, float ny, bool cover)
        {
            var hwnd = ResolveGameWindow();
            if (hwnd == IntPtr.Zero)
            {
                Interlocked.Increment(ref _tapFailCount);
                if (_lastError != "找不到游戏窗口")
                {
                    _lastError = "找不到游戏窗口";
                    L("★找不到游戏窗口，点击无法注入（游戏可能在后台或已最小化）。");
                }
                return;
            }

            // ★必须把游戏窗口切到前台，否则注入的点击游戏收不到
            //（Windows 不允许后台窗口接收输入）。
            if (GetForegroundWindow() != hwnd)
            {
                // 最小化的窗口要先还原，否则 SetForegroundWindow 无效
                if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
                SetForegroundWindow(hwnd);
                // 留一点时间给窗口切换，否则后续点击会落到旧的前台窗口
                Thread.Sleep(30);
            }

            // 客户端区尺寸（排除标题栏/边框），游戏渲染都在这块
            if (!GetClientRect(hwnd, out var cr))
            {
                Interlocked.Increment(ref _tapFailCount);
                return;
            }
            int cw = cr.Right - cr.Left;
            int ch = cr.Bottom - cr.Top;
            if (cw <= 0 || ch <= 0)
            {
                Interlocked.Increment(ref _tapFailCount);
                return;
            }

            // 归一化 → 客户端区坐标 → 屏幕绝对坐标
            int clientX = (int)Math.Round(nx * cw);
            int clientY = (int)Math.Round(ny * ch);
            // ★夹紧：cover 模式下裁切掉的边缘可能算出略超范围的值
            clientX = Math.Clamp(clientX, 0, cw - 1);
            clientY = Math.Clamp(clientY, 0, ch - 1);

            var pt = new POINT { X = clientX, Y = clientY };
            if (!ClientToScreen(hwnd, ref pt))
            {
                Interlocked.Increment(ref _tapFailCount);
                return;
            }

            SetCursorPos(pt.X, pt.Y);
            // 给系统一点时间处理光标移动，否则后续 click 可能落在旧位置
            Thread.Sleep(15);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

            Interlocked.Increment(ref _tapInjectedCount);
            _lastError = "";
            L($"已注入点击 → 屏幕({pt.X},{pt.Y}) 归一化({nx:F3},{ny:F3})"
              + (cover ? " [竖屏cover]" : " [横屏contain]"));
        }

        /// <summary>
        /// 找到游戏自己的窗口句柄。
        /// ★不用 GetForegroundWindow：那可能是手机页面的浏览器窗口，
        ///   而我们要点的是游戏窗口。
        /// ★也不用进程名匹配（游戏 exe 名不确定）：Unity 的主窗口
        ///   在 IL2CPP 下可通过 Application / 或枚举顶层窗口 + 尺寸筛选。
        ///   这里用「当前进程的主窗口」——插件就在游戏进程里，最可靠。
        /// </summary>
        private static IntPtr ResolveGameWindow()
        {
            // ★IL2CPP 下 Application 不一定有 Unity 窗口句柄；
            //   可靠做法是枚举当前进程的所有顶层窗口，取面积最大的可见窗口。
            var found = FindLargestVisibleWindowOfCurrentProcess();
            if (found != IntPtr.Zero)
            {
                if (_gameWindow != found)
                {
                    _gameWindow = found;
                    L($"已定位游戏窗口：hwnd=0x{found:X}");
                    DescribeWindow(found);
                }
                return found;
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static IntPtr _bestWindow;
        private static long _bestArea;

        private static bool EnumProc(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return true;
            if (!GetWindowRect(hWnd, out var r)) return true;

            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            long area = (long)w * h;
            // 排除 0 尺寸与工具窗口
            if (w < 200 || h < 200) return true;
            if (area > _bestArea)
            {
                _bestArea = area;
                _bestWindow = hWnd;
            }
            return true;
        }

        private static IntPtr FindLargestVisibleWindowOfCurrentProcess()
        {
            _bestWindow = IntPtr.Zero;
            _bestArea = 0;
            EnumWindows(EnumProc, IntPtr.Zero);
            return _bestWindow;
        }

        private static void DescribeWindow(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var r)) return;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            _windowInfo = $"{w}x{h} @({r.Left},{r.Top})";
            L($"游戏窗口尺寸：{_windowInfo}（点击坐标按此区域换算）");
        }

        /// <summary>供 /status 显示。玩家真正能用的关键就是这几条数据。</summary>
        public static string BuildStatusJson()
        {
            return "{\"tapRx\":" + ReceivedCount
                 + ",\"tapInjected\":" + InjectedCount
                 + ",\"tapOutOfRange\":" + OutOfRangeCount
                 + ",\"tapFail\":" + FailCount
                 + ",\"window\":\"" + _windowInfo.Replace("\"", "") + "\""
                 + ",\"error\":\"" + _lastError.Replace("\"", "") + "\"}";
        }
    }
}