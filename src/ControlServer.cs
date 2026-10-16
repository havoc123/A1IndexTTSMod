using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace A1IndexTTSMod
{
    /// <summary>
    /// NPC 视频通话窗口的**控制通道**（端口 8894，与画面端口 8895 分开）。
    ///
    /// ★★为什么必须独立端口 —— 这是本文件存在的全部理由：
    ///   浏览器（Chrome/Edge/Android Chrome）对**同一 origin** 的并发 TCP 连接限 6 条。
    ///   8895 上常驻着两条**永不释放**的长连接：
    ///     ① <c>&lt;img src="/mjpeg"&gt;</c>   —— 1 条（偶尔重连会变2 条）
    ///     ② <c>&lt;audio src="/audio"&gt;</c>  —— 1 条（报错重连时会更多）
    ///   再加 <c>/status</c> 轮询，就只剩 2~3 条槽位。
    ///   <c>sendBeacon</c> 也**受同一个池的限制**（它只是优先级更低，不是独立通道）。
    ///   ⇒ 槽位满时，打字请求会**排队直到页面关闭时长连接一次性释放**才被处理。
    ///     实测症状：点「发送」要等几十秒；关掉网页再打开的瞬间才生效。
    ///
    /// ★★解法不是「优化优先级」，而是**换origin**：
    ///   Chrome 的连接池按 (协议, 主机, 端口) 分组，**不同端口 = 完全独立的池**。
    ///   把低频指令（打字/点击/按钮/状态/开关面板）搬到 8894后：
    ///     - 控制通道有自己完整的 6 条槽位，长流再忙也挤不占；
    ///     - <c>fetch</c> 恢复正常（能立刻拿到响应 = 能立刻回显成功/失败）；
    ///     - 端口职责清晰：8895 只管「下行画面与声音」这条长流。
    ///   代价是跨域，所以本服务每个响应都带 <c>Access-Control-Allow-Origin: *</c>
    ///   （★这正是之前音频独立成 8896 时**漏掉**的那一行，导致误报「连不上端口」）。
    ///
    /// ★本文件纯 .NET，不碰任何 Unity API，可在 <c>Plugin.Load()</c> 里直接启动。
    /// </summary>
    internal static class ControlServer
    {
        // ---------------------------------------------------------------- 状态

        private static TcpListener? _listener;
        private static Thread? _acceptThread;
        private static volatile bool _running;
        private static ManualLogSource? _log;
        private static int _port;

        /// <summary>手机底部面板（按钮栏 + 状态行）当前是否应显示。</summary>
        private static volatile bool _panelVisible;

        /// <summary>状态变更计数：手机轮询时用它判断「要不要立刻重画」。</summary>
        private static int _panelEpoch;

        // ---------------------------------------------------------------- 生命周期

        /// <summary>
        /// 启动控制服务。纯托管，可在 <c>Plugin.Load()</c> 调用。
        /// </summary>
        public static bool Start(ManualLogSource log, int port)
        {
            _log = log;
            _port = port;

            if (_running)
            {
                L($"控制服务已在运行（端口 {_port}），忽略重复启动。");
                return true;
            }

            try
            {
                // ★TcpListener 绑普通端口零权限（HttpListener 需要 HTTP.SYS URL ACL，
                //   非管理员进程会直接 ErrorCode=5，而经Steam 启动的游戏不可能是管理员）。
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _running = true;

                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "A1-Control-Accept"
                };
                _acceptThread.Start();

                L($"控制服务已启动 → http://<本机IP>:{_port}/（PC 快捷键面板）");
                LogLanUrls();
                return true;
            }
            catch (Exception e)
            {
                L("★控制服务启动失败：" + e.Message);
                _running = false;
                return false;
            }
        }

        public static void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _acceptThread = null;
            L("控制服务已停止。");
        }

        public static bool IsRunning { get { return _running; } }

        public static int Port { get { return _port; } }

        /// <summary>切换面板显隐，返回切换后的状态（PC 快捷键与手机手势共用）。</summary>
        public static bool TogglePanel()
        {
            _panelVisible = !_panelVisible;
            Interlocked.Increment(ref _panelEpoch);
            L("面板已切换为：" + (_panelVisible ? "显示" : "隐藏"));
            return _panelVisible;
        }

        public static void SetPanel(bool visible)
        {
            if (_panelVisible == visible) return;
            _panelVisible = visible;
            Interlocked.Increment(ref _panelEpoch);
            L("面板已切换为：" + (visible ? "显示" : "隐藏"));
        }

        public static bool PanelVisible { get { return _panelVisible; } }

        // ---------------------------------------------------------------- 服务循环

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
                    if (_running)
                        L("★AcceptTcpClient 异常：" + Environment.CurrentManagedThreadId);
                    return;                // Stop() 导致，属正常退出
                }

                try
                {
                    // ★控制通道全部是「短请求」，绝不能让它占住线程池：
                    //   ThreadPool 线程被长连接占满 → 新连接 accept 到了却迟迟不处理，
                    //   症状同样是「点了没反应」。这里用**独立线程**，不受池大小影响。
                    var t = new Thread(() => HandleClient(client))
                    {
                        IsBackground = true,
                        Name = "A1-Control-Client"
                    };
                    t.Start();
                }
                catch (Exception e)
                {
                    L("★处理请求时抛异常：" + e.Message);
                    try { client.Close(); } catch { }
                }
            }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                // 超时必须有：否则浏览器开着页面却不发请求的僵死连接会永久占住槽位。
                try { client.ReceiveTimeout = 10000; } catch { }
                try { client.SendTimeout = 10000; } catch { }

                using var stream = client.GetStream();

                // ★2026-10-14 语音上行：ReadRequest 改**字节级**读取，不再过
                //   StreamReader。原因：/voice 上传 PCM 是二进制，而 StreamReader
                //   会把 ≥0x80 的字节经 UTF-8 解码替换成 U+FFFD（数据静默损坏），
                //   且它自带缓冲会把 body 前几个字节吞进私有缓冲（从 stream 读不到）。
                //   逐字节读行 + stream.Read 精确读 body，文本/二进制都安全。
                var req = ReadRequest(stream);
                if (req == null) return;

                Dispatch(stream, req);
            }
            catch
            {
                // 客户端提前断开属常态，不值得记日志
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static void Dispatch(Stream stream, Req req)
        {
            // ★★★ CORS 预检（OPTIONS）—— 这是『点发送 → 显示发送失败』的唯一根因。
            //
            // 【为什么只有打字挂，其它都好使】
            //   sendType() 是 `POST /type` 且带 `Content-Type: application/json`。
            //   application/json 不属于 CORS「简单请求」的三个安全 Content-Type
            //   （application/x-www-form-urlencoded / multipart/form-data / text/plain），
            //   ⇒ 浏览器**必须先发一个 OPTIONS 预检**，问服务器「这个方法/头我能不能用」。
            //   而本方法原来只按 path 分发，OPTIONS 会掉进 default → 返回 404，
            //   且响应里没有 Access-Control-Allow-Methods / -Allow-Headers
            //   ⇒ 浏览器判定预检失败，**根本不会把真正的 POST 发出去**
            //   ⇒ 前端 fetch 立刻 reject → 页面显示『发送失败：TypeError: Failed to fetch』。
            //   反证就在现场：/tap、/cmd、/status、/panel 全是 **GET**（简单请求，不预检），
            //   所以它们全部正常—— 只有 POST 的打字坏。若不是预检问题，
            //   不会出现这种「同一个服务器上一半接口好使一半挂」的分裂现象。
            if (req.Method == "OPTIONS")
            {
                ServePreflight(stream);
                return;
            }

            switch (req.Path)
            {
                case "/":
                case "/index.html":
                    ServeControlPage(stream);
                    break;

                case "/type":
                    MjpegServer.HandleTypeControl(stream, req.Body, req.Query);
                    break;

                // ---- 手机麦克风语音上行（2026-10-14 新增）----
                // 协议：POST /voice?cmd=start|push|stop（start/stop 无 body，
                //   push 的 body = 原始 PCM，参数 rate/ch/bits 描述格式）。
                // 与 /type 共用同端口同源（无 CORS 预检问题——Content-Type 用
                // application/octet-stream 仍会触发预检，OPTIONS 已在方法开头统一处理）。
                case "/voice":
                    HandleVoice(stream, req);
                    break;

                case "/tap":
                    // ★必须把 query 一起传过去：手机端点击是 GET /tap?x=..&y=..，
                    //   坐标全在 query string 里，body 是空的。
                    //   旧代码只传 req.Body → x/y 全部丢失 → 服务端日志刷
                    //   「★收到 /tap 但坐标解析失败」，点击收0/注0（用户实测点击无效）。
                    MjpegServer.HandleTapControl(stream, req.Body, req.Query);
                    break;

                case "/cmd":
                    MjpegServer.HandleCmdControl(stream, req.Body, req.Query);
                    break;

                case "/status":
                    ServeBytes(stream, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(MjpegServer.BuildStatusJsonPublic()));
                    break;

                case "/panel":
                    // GET /panel → 查询；POST /panel?visible=1|0 → 设置；?toggle=1 → 切换
                    if (req.Query.TryGetValue("toggle", out _))
                    {
                        ServeBytes(stream, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"visible\":" + (TogglePanel() ? "true" : "false") + "}"));
                    }
                    else if (req.Query.TryGetValue("visible", out var vis))
                    {
                        bool want = vis == "1" || vis == "true";
                        SetPanel(want);
                        ServeBytes(stream, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"visible\":" + (want ? "true" : "false") + "}"));
                    }
                    else
                    {
                        ServeBytes(stream, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes(
                                "{\"visible\":" + (_panelVisible ? "true" : "false")
                                + ",\"epoch\":" + Volatile.Read(ref _panelEpoch).ToString(CultureInfo.InvariantCulture)
                                + "}"));
                    }
                    break;

                default:
                    ServeBytes(stream, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("not found: " + req.Path), status: "404 Not Found");
                    break;
            }
        }

        // ---------------------------------------------------------------- 手机麦克风语音上行

        /// <summary>
        /// /voice?cmd=toggle —— 手机「语音」按钮 = 游戏内麦克风按钮的远程映射。
        /// 走 <see cref="SpeechPanelUi.ToggleMic"/> 完全相同的逻辑：PC 麦克风 +
        /// 同一条 ASR 流水线（识别→草稿上屏→AsrSendGate 自动发送），游戏侧零改动。
        /// HTTP 线程入队后等主线程执行完再应答（默认 2s 超时），拒绝原因原样带回。
        /// ★2026-10-14 早期版本是「手机 getUserMedia 录 PCM 上传外部会话」，
        ///   已推翻：http://局域网 IP 不是安全上下文，getUserMedia 被浏览器完全
        ///   禁用（且 iPhone Safari 无开关）。遥控游戏内麦克风没有这个限制。
        /// </summary>
        private static void HandleVoice(Stream stream, Req req)
        {
            string cmd = req.Q("cmd", "").ToLowerInvariant();

            if (cmd != "toggle")
            {
                ServeBytes(stream, "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes("{\"ok\":false,\"reason\":\"unknown-cmd\"}"),
                    status: "400 Bad Request");
                return;
            }

            string result = SpeechPanelUi.RequestMicToggle(2000);
            bool ok = result == "started" || result == "stopped" || result == "canceled-warmup";
            ServeBytes(stream, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes("{\"ok\":" + (ok ? "true" : "false")
                    + ",\"result\":\"" + EscapeJson(result) + "\"}"));
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r", " ").Replace("\n", " ");
        }

        // ---------------------------------------------------------------- 极简 HTTP 读写

        private sealed class Req
        {
            public readonly string Method;
            public readonly string Path;
            public readonly Dictionary<string, string> Query;
            public readonly string Body;
            /// <summary>原始字节 body（二进制安全）；无 body 时为 null。</summary>
            public readonly byte[]? RawBody;

            public Req(string m, string p, Dictionary<string, string> q, string b, byte[]? raw = null)
            {
                Method = m; Path = p; Query = q; Body = b; RawBody = raw;
            }

            public string Q(string key, string def = "")
            {
                return Query.TryGetValue(key, out var v) ? v : def;
            }
        }

        /// <summary>字节级读一行（兼容 \n / \r\n）。流结束返回 null。</summary>
        private static string? ReadLineRaw(Stream stream)
        {
            var sb = new StringBuilder(128);
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return sb.Length > 0 ? sb.ToString() : null;
                if (b == '\n')
                {
                    var s = sb.ToString();
                    return s.EndsWith("\r") ? s.Substring(0, s.Length - 1) : s;
                }
                sb.Append((char)b);
                if (sb.Length > 8192) return null;   // 防御：异常超长行
            }
        }

        private static Req? ReadRequest(Stream stream)
        {
            var requestLine = ReadLineRaw(stream);
            if (string.IsNullOrEmpty(requestLine)) return null;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return null;

            var method = parts[0];
            var target = parts[1];

            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = target;
            int qm = target.IndexOf('?');
            if (qm >= 0)
            {
                path = target.Substring(0, qm);
                var qs = target.Substring(qm + 1);
                foreach (var kv in qs.Split('&'))
                {
                    if (kv.Length == 0) continue;
                    int eq = kv.IndexOf('=');
                    string k = eq < 0 ? kv : kv.Substring(0, eq);
                    string v = eq < 0 ? "" : kv.Substring(eq + 1);
                    try
                    {
                        query[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v);
                    }
                    catch { }
                }
            }

            int contentLength = 0;
            while (true)
            {
                var line = ReadLineRaw(stream);
                if (string.IsNullOrEmpty(line)) break;

                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon).Trim();
                string val = line.Substring(colon + 1).Trim();

                if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(val, out contentLength);
                else if (key.Equals("Expect", StringComparison.OrdinalIgnoreCase)
                    && val.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // 100-continue 必须先回应，否则浏览器会等 1 秒再发 body（可见的延迟）。
                    try
                    {
                        var b = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        stream.Write(b, 0, b.Length);
                        stream.Flush();
                    }
                    catch { }
                }
            }

            // ★原始字节读 body（二进制安全）。
            //   文本端点用 UTF8.GetString 解码（与旧行为等价）；/voice 用 RawBody。
            //   ★上限 8MB：60 秒 48k/16bit/mono ≈ 5.5MB，留余量。原来 64KB 的
            //     限制对语音上传不够（且旧代码对二进制根本不成立）。
            if (contentLength <= 0 || contentLength > 8 * 1024 * 1024)
                return new Req(method, path, query, "", null);

            var raw = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = stream.Read(raw, read, contentLength - read);
                if (n <= 0) break;                 // 流结束（对端已关闭）
                read += n;
            }
            if (read != contentLength) return null;   // 半截 body：按坏请求丢弃
            string body = Encoding.UTF8.GetString(raw, 0, read);
            return new Req(method, path, query, body, raw);
        }

        private static void WriteHead(Stream stream, string status, string contentType,
                                      long? contentLength = null)
        {
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (contentLength.HasValue)
                sb.Append("Content-Length: ").Append(contentLength.Value).Append("\r\n");
            // ★★跨源必须显式允许 —— 页面在 8895、指令走 8894，就是跨源。
            //   上一轮音频独立成 8896 时漏了这行，前端 fetch 直接被浏览器拦掉，
            //   误报成「连不上端口」，把排查方向整个带偏到防火墙上。
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            // ★预检要回答的两个问题（缺一个浏览器就掐断后续 POST）：
            //   Allow-Methods → 「除了 GET，POST 行不行？」
            //   Allow-Headers → 「我带了 Content-Type，行不行？」
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");
            var head = Encoding.UTF8.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
        }

        /// <summary>
        /// CORS 预检响应（204 No Content）。
        /// ★必须**在任何业务分发之前**处理 OPTIONS，否则预检 404 → 真POST 永远发不出去。
        /// </summary>
        private static void ServePreflight(Stream stream)
        {
            try
            {
                var sb = new StringBuilder(200);
                sb.Append("HTTP/1.1 204 No Content\r\n");
                sb.Append("Access-Control-Allow-Origin: *\r\n");
                sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                // 预检结果缓存 10 分钟，省掉每次打字都先往返一次。
                sb.Append("Access-Control-Max-Age: 600\r\n");
                sb.Append("Content-Length: 0\r\n");
                sb.Append("Connection: close\r\n");
                sb.Append("\r\n");
                var head = Encoding.UTF8.GetBytes(sb.ToString());
                stream.Write(head, 0, head.Length);
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
                // 客户端已断开
            }
        }

        // ---------------------------------------------------------------- PC 快捷键页面

        /// <summary>
        /// PC 端状态页（原「空格切换面板」控制页，2026-10-14 起功能作废）。
        /// 保留端口与页面是为了让用户打开 8894 时看到**明确说明**，
        /// 而不是一个看起来坏掉的空白页 —— 手机端输入框与发送按钮现在常驻显示。
        /// </summary>
        private static void ServeControlPage(Stream stream)
        {
            const string html = @"<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>A1 手机画面 · 控制通道</title>
<style>
  html,body{height:100%;margin:0;background:#111;color:#eee;
    font:15px/1.7 -apple-system,Segoe UI,Microsoft YaHei,sans-serif}
  body{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:16px}
  .k{background:#1d1d22;border:1px solid #333;border-radius:10px;padding:20px 30px;
     font-size:19px;letter-spacing:1px;text-align:center}
  .k b{color:#6cf}
  .s{font-size:15px;color:#6cf;min-height:26px}
  .h{font-size:13px;color:#888;max-width:520px;text-align:center;line-height:1.9}
  code{background:#222;border-radius:4px;padding:2px 7px;color:#9fd4ff}
  a{color:#6cf}
</style>
<div class=""k"">控制通道正常运行</div>
<div class=""s"" id=""s"">读取中…</div>
<div class=""h"">
  手机页面地址：<code id=""u"">http://&lt;本机IP&gt;:8895/phone</code><br>
  底部「输入框 + 发送」现在<b>常驻显示</b>，不再需要按快捷键唤出。<br>
  此页面仅作通道状态确认；指令（点击 / 打字 / 按钮）仍由本端口 8894 承载，<br>
  与 8895 的画面、声音长流相互独立，避免浏览器同源 6 连接上限把请求堵死。
</div>
<script>
  function render(v){
    document.getElementById('s').textContent = v ? '面板开关：已停用（面板常驻显示）' : '面板开关：已停用';
  }
  function q(){ fetch('/panel?t=' + Date.now()).then(function(r){return r.json();})
      .then(function(d){ render(d.visible); }).catch(function(e){ render(false); }); }
  try { document.getElementById('u').textContent =
        'http://' + location.hostname + ':8895/phone'; } catch (e) {}
  q(); setInterval(q, 3000);
</script>";
            ServeBytes(stream, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
        }

        // ---------------------------------------------------------------- 杂项

        private static void LogLanUrls()
        {
            try
            {
                var addrs = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
                foreach (var a in addrs)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                        L($"  → PC 快捷键面板：http://{a}:{_port}/");
                }
            }
            catch { }
        }

        private static void L(string msg)
        {
            // LogInfo 是 BepInEx.Logging 的扩展方法（与 MjpegServer.L 保持一致的写法）。
            try { _log?.LogInfo("[CTRL] " + msg); } catch { }
        }
    }
}