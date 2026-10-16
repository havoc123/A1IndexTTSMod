using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace A1IndexTTSMod
{
    /// <summary>
    /// 游戏内「手机网页」二维码浮窗（2026-10-14）。
    ///
    /// 背景（用户原话）：「游戏打开的时候能不能生成个手机网页端网址的二维码？
    ///   那样就不怕网址变了，我手机直接扫就可以打开了。」
    /// 之前 PC 换过 IP（DHCP 租约变更 .100→.102）导致手机打不开旧地址，
    /// 二维码内容 = 当前实时探测的局域网 URL，IP 再变会自动重新编码。
    ///
    /// ★为什么显示在游戏内而不是 PC 控制页：用户要「打开游戏就能看到」，
    ///   控制页还得先手动开浏览器；游戏内浮窗零额外操作。
    ///   IMGUI（OnGUI）手绘，不碰游戏任何 uGUI——零侵入、随场景自动重建。
    ///
    /// ★为什么 QR 编码器自己写（src/QrEncoder.cs）：不引第三方 DLL，
    ///   且已用 python qrcode 库对 522 个用例逐模块验证（见 qrlab）。
    ///
    /// ★IL2CPP 约束：
    ///   · 全部 Unity 调用都是静态无委托方法（GUI.* / Texture2D / Input / Event）
    ///   · 绝不用 GUI.Window/DragWindow——那需要 WindowFunction 委托，
    ///     interop 下托管委托不能直接过给 Unity（本项目踩过），
    ///     拖动改用 OnGUI 里 Event.current 的 MouseDown/MouseDrag 自己算
    ///   · 实例化挂 sceneLoaded 之后（Plugin.Load 里 new GameObject 会崩）
    /// </summary>
    public static class QrOverlay
    {
        private static ConfigEntry<bool>? _enabled;
        private static int _port = 8895;
        private static bool _serverRunning;
        private static string? _serverError;

        /// <summary>
        /// 注册类型 + 挂载。由 Plugin.Load 在 MjpegServer.Start 之后调用。
        /// ★无条件挂载：这里不判断 IsRunning —— 服务没起来时浮窗会在界面上直接
        ///   显示失败原因（LastError），这正是用户唯一能立刻看到问题的方式。
        /// </summary>
        public static void Register(ConfigEntry<bool> enabled, int port)
        {
            _enabled = enabled;
            _port = port;
            _serverRunning = MjpegServer.IsRunning;
            _serverError = MjpegServer.LastError;
            ClassInjector.RegisterTypeInIl2Cpp<Overlay>();

            // interop 下 SceneManager.sceneLoaded 的委托必须显式强转（同 MjpegDriver）
            UnityEngine.SceneManagement.SceneManager.sceneLoaded +=
                (UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene,
                                              UnityEngine.SceneManagement.LoadSceneMode>)
                ((scene, mode) => TryAttach("scene:" + scene.name));

            // 插件加载较晚时 sceneLoaded 可能已错过
            TryAttach("bootstrap");
        }

        private static void TryAttach(string reason)
        {
            try
            {
                if (_instance != null) return;
                var go = new GameObject("A1QrOverlay");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var ov = go.AddComponent<Overlay>();
                _instance = ov;   // 同 Driver：AddComponent 后立刻赋值，幂等保护
                MjpegServer.L($"二维码浮窗已挂载（{reason}）");
            }
            catch (Exception e)
            {
                MjpegServer.L($"二维码浮窗挂载失败（{reason}）：{e.Message}");
            }
        }

        private static Overlay? _instance;

        public class Overlay : MonoBehaviour
        {
            private const float WinW = 250f;
            private const float WinH = 360f;         // 浮窗高度（含二维码 + 地址 + 换地址按钮）
            private static readonly Rect WinInitial = new(24, 24, WinW, WinH);

            private Rect _win = WinInitial;
            private bool _closed;                    // 本次运行内用户点了 ×
            private bool _dragging;
            private Vector2 _dragOffset;

            private Texture2D? _qr;
            private string _qrUrl = "";              // 当前二维码编码的 URL
            private string _url = "";                // 当前选中（显示/编码）的 URL
            private List<string> _cands = new();     // 本机全部候选 URL（0 号 = 判定最佳）
            private int _candIdx;                    // 当前选中下标（用户可手动切换）
            private float _ipTimer;
            private int _onGuiTicks;
            private int _updateTicks;

            /// <summary>OnGUI 自证：若 Update 在跑而 OnGUI 从未被调用，日志报警并降级。</summary>
            private float _selfTestTimer;

            private void Update()
            {
                _updateTicks++;
                _ipTimer += Time.deltaTime;
                if (_ipTimer >= 5f)
                {
                    _ipTimer = 0f;
                    RefreshCandidates();
                }

                // OnGUI 可用性自检（IL2CPP 注入类型收不到 OnGUI 时至少让用户看到地址）
                _selfTestTimer += Time.deltaTime;
                if (_selfTestTimer >= 10f)
                {
                    _selfTestTimer = 0f;
                    if (_updateTicks > 0 && _onGuiTicks == 0)
                        MjpegServer.L("★二维码浮窗自检：Update 在跑但 OnGUI 未被调用"
                            + $"（IMGUI 渲染不可用）→ 降级为日志输出：手机访问 {_url}");
                }
            }

            private void OnGUI()
            {
                _onGuiTicks++;
                if (_enabled?.Value != true || _closed) return;

                if (_cands.Count == 0) RefreshCandidates();   // 首帧兜底：Update 还没跑过 5s
                if (_url == "") { GUI.Label(new Rect(_win.x, _win.y, 300, 20), "未探测到本机局域网 IP（二维码稍后重试）"); return; }
                if (!_serverRunning)
                {
                    // ★服务没起来就别显示二维码了 —— 扫码打开的页面是死的，
                    //   反而让人以为「二维码有问题」。直接把原因摆出来。
                    GUI.Label(new Rect(_win.x + 12, _win.y + 34, _win.width - 24, 20), "★画面/控制服务未启动");
                    GUI.Label(new Rect(_win.x + 12, _win.y + 56, _win.width - 24, 60),
                        "原因：" + (_serverError ?? "未知")
                        + "\n常见情况：端口被占用（本机已有一个MOD实例/其他程序）；"
                        + "换 Port/ControlPort 或关掉占用程序后重启游戏。");
                    return;
                }
                if (_qr == null)
                {
                    if (_qrUrl == _url) { GUI.Label(new Rect(_win.x, _win.y, 300, 20), "二维码生成中…"); return; }
                    BuildQr(_url);
                    if (_qr == null) { GUI.Label(new Rect(_win.x, _win.y, 300, 20), "二维码生成失败（见日志）"); return; }
                }

                _win.height = WinH;

                // ---- 窗口
                GUI.Box(_win, "");
                GUI.Label(new Rect(_win.x + 10, _win.y + 5, _win.width - 44, 22), "手机扫码在浏览器里玩");
                var closeRect = new Rect(_win.xMax - 34, _win.y + 3, 28, 20);
                if (GUI.Button(closeRect, "×")) { _closed = true; return; }

                // ---- 拖拽（标题栏；坐标统一用 Event.current 的 GUI 空间）
                var e = Event.current;
                var titleBar = new Rect(_win.x, _win.y, _win.width, 26);
                if (e.type == EventType.MouseDown && e.button == 0
                    && titleBar.Contains(e.mousePosition) && !closeRect.Contains(e.mousePosition))
                { _dragging = true; _dragOffset = e.mousePosition - new Vector2(_win.x, _win.y); }
                else if (e.type == EventType.MouseUp && e.button == 0) _dragging = false;
                else if (e.type == EventType.MouseDrag && _dragging) _win.position += e.delta;

                // ---- 二维码（★不用 Mathf.Min(float,float)：interop 里只有数组重载，sigprobe6 实测）
                float qrSide = _win.width - 24f;
                if (qrSide > 210f) qrSide = 210f;
                var qrRect = new Rect(_win.x + 12, _win.y + 30, qrSide, qrSide);
                GUI.DrawTexture(qrRect, _qr);

                // ---- 地址
                GUI.Label(new Rect(_win.x + 12, qrRect.yMax + 6, _win.width - 24, 18), "手机扫码打开：");
                GUI.Label(new Rect(_win.x + 12, qrRect.yMax + 24, _win.width - 24, 36), _url);

                // ---- 换地址（★多网卡时自动判定也可能挑错，用户手动翻到能用的那个）
                bool showCycle = _cands.Count > 1;
                if (showCycle && GUI.Button(new Rect(_win.x + 12, qrRect.yMax + 62, _win.width - 24, 22),
                        $"换一个地址（{_candIdx + 1}/{_cands.Count}）"))
                    CycleUrl();

                // ---- 网络提示（2026-10-15 用户要求新增）：扫码打不开的头号原因是
                //    手机连了别的 Wi-Fi / 走了移动数据。常驻在按钮下方。
                //    ★文案不用「同一 Wi-Fi」：PC 通常是插网线的，「同一路由器」才覆盖全部情况。
                GUI.Label(new Rect(_win.x + 12, qrRect.yMax + 90, _win.width - 24, 20),
                    "手机和电脑需连同一路由器");
            }

            private void BuildQr(string url)
            {
                try
                {
                    var qr = QrEncoder.Encode(url);
                    int size = qr.Size;
                    const int quiet = 4;   // 标准要求 quiet zone ≥ 4 模块
                    int texSize = size + quiet * 2;
                    var tex = new Texture2D(texSize, texSize, TextureFormat.RGB24, false);
                    tex.filterMode = FilterMode.Point;   // 放大不糊
                    var pixels = new Color[texSize * texSize];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
                    for (int r = 0; r < size; r++)
                        for (int c = 0; c < size; c++)
                        {
                            // Texture2D 行序自下而上，QR 矩阵自上而下 → 翻转行
                            if (qr.Modules[r, c]) pixels[(quiet + size - 1 - r) * texSize + quiet + c] = Color.black;
                        }
                    tex.SetPixels(pixels);
                    tex.Apply();
                    _qr = tex;
                    _qrUrl = url;
                    MjpegServer.L($"二维码已生成：{url}（QR ver{qr.Version} mask{qr.Mask}，{size}×{size} 模块）");
                }
                catch (Exception ex)
                {
                    MjpegServer.L($"二维码生成失败：{ex.Message}");
                    _qr = null;
                }
            }

            // ---------------------------------------------------------------- 地址探测
            /// <summary>刷新候选地址列表；保留用户的手动选择，失效时回落到最佳项。</summary>
            private void RefreshCandidates()
            {
                var list = EnumerateLanUrls();
                if (!SameList(list, _cands))
                {
                    _cands = list;
                    if (_cands.Count == 0) { ApplyUrl(""); return; }
                    int idx = _cands.IndexOf(_url);
                    // ★新地址出现时若用户没手动切过就跟随最佳；切过但当前项已消失则回到最佳
                    _candIdx = idx >= 0 ? idx : 0;
                    ApplyUrl(_cands[_candIdx]);
                }
            }

            private void ApplyUrl(string url)
            {
                if (url == _url) return;
                _url = url;
                _qr = null;      // 触发重新编码
                _qrUrl = "";
            }

            /// <summary>手动切到下一个候选地址（多网卡时自动判定也可能不对，留个出口）。</summary>
            private void CycleUrl()
            {
                if (_cands.Count <= 1) return;
                _candIdx = (_candIdx + 1) % _cands.Count;
                ApplyUrl(_cands[_candIdx]);
            }

            /// <summary>
            /// 枚举本机可用于手机访问的 URL，**按可信度排序**。
            ///
            /// ★踩过的坑（2026-10-15，用户报告「扫码显示的地址是错的」）：
            ///   原实现取 Dns.GetHostEntry(...).AddressList 的第一个 IPv4，结果拿到的是
            ///   **ZeroTier 虚拟网卡**（它甚至伪造了网关 25.255.255.254），
            ///   而手机真正同网段的物理网卡是另一个地址 —— 扫码必然打不开。
            ///   虚拟网卡（ZeroTier / Hyper-V / VMware / Radmin VPN / 加速器）在多网卡机器上
            ///   排序往往靠前，但流量根本不过它。
            ///
            /// ⇒ 首选「系统选路出口 IP」：UdpClient.Connect 一个公网地址后读 LocalEndPoint，
            ///   Windows 会返回默认路由所在网卡的 IP（实测正确）。
            ///   Connect 只查路由表、不发实际数据包，所以没有网络副作用。
            ///   失败时再退回 Dns 枚举（并剔除 169.254.x APIPA 这种无效地址）。
            /// </summary>
            private static List<string> EnumerateLanUrls()
            {
                var list = new List<string>();

                var best = PickDefaultRouteIp();
                if (best != null) list.Add($"http://{best}:{_port}/phone");

                try
                {
                    foreach (var a in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                    {
                        if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                        // 169.254.x 是自动私有地址（DHCP 失败），手机不可能连上，剔除
                        var b = a.GetAddressBytes();
                        if (b.Length == 4 && b[0] == 169 && b[1] == 254) continue;
                        var u = $"http://{a}:{_port}/phone";
                        if (!list.Contains(u)) list.Add(u);
                    }
                }
                catch { }

                return list;
            }

            /// <summary>取系统默认路由所在网卡的 IPv4（即本机访问外网时用的那个源地址）。</summary>
            private static IPAddress? PickDefaultRouteIp()
            {
                UdpClient? probe = null;
                try
                {
                    probe = new UdpClient();
                    probe.Connect("223.5.5.5", 80);   // 阿里 DNS，只用来查路由表
                    var local = probe.Client.LocalEndPoint as IPEndPoint;
                    var addr = local?.Address;
                    if (addr != null && addr.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(addr)) return addr;
                }
                catch { }
                finally
                {
                    try { probe?.Close(); } catch { }
                    try { probe?.Dispose(); } catch { }
                }
                return null;
            }

            private static bool SameList(List<string> a, List<string> b)
            {
                if (a.Count != b.Count) return false;
                for (int i = 0; i < a.Count; i++)
                    if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
                return true;
            }

            private void OnDestroy()
            {
                if (_qr != null) { UnityEngine.Object.Destroy(_qr); _qr = null; }
                if (_instance != null && ReferenceEquals(_instance, this)) _instance = null;
            }
        }
    }
}
