using System;
using System.IO;
using System.Text;
using System.Threading;
using NAudio.Wave;

namespace A1IndexTTSMod
{
    /// <summary>
    /// NPC 语音 + 游戏 BGM/音效 → 手机（声音下行）。
    ///
    /// ★★架构（2026-10-13 第三轮修订，用户实测反馈驱动）：
    ///   音频不再自己开 TcpListener，而是把 PCM 缓存在环形缓冲里，
    ///   由 <see cref="MjpegServer"/> 在**同一个 8895 端口**上以 `/audio` 端点推流。
    ///
    /// 【第一轮：合并端口】原来音频单独监听 8896，产生两个必然发生的 bug：
    ///   ① `/phone` 页面在 8895，`fetch(':8896')` 属于跨源，而当时响应没有
    ///      `Access-Control-Allow-Origin` → 请求**确实到了服务器**，但响应被浏览器
    ///      丢弃 → 前端 catch → 显示「连不上 8896 端口」。★这条假提示把排查方向
    ///      整个带偏到防火墙上，而规则当时确实是好的。
    ///   ② 页面加载即请求 `/audio`，此时 NPC 还没开口 → 服务端返回「还没声音」
    ///      → `<audio>` 触发 error 且**永不重试** → 之后 NPC 说话也没声。
    ///   合并端口后：CORS（同源）、双份防火墙规则、双份生命周期一并消失。
    ///
    /// 【第二轮：用户实测「有语音但没 BGM/音效」】
    ///   PCM 分路挂在 <c>PcmVolumeWaveProvider</c> 上，而它只包裹 **TTS 的 WAV 文件**
    ///   （WaveFileReader）。游戏自己的 BGM/音效走 Unity AudioSource → 系统输出设备，
    ///   **完全不经过 NAudio** ⇒ 结构上就不可能抓到。
    ///   ⇒ 新增 <see cref="LoopbackTap"/>：用 WASAPI loopback 抓系统默认输出设备的
    ///   **混音**（BGM + 音效 + TTS 全在里面）。两种音源互斥，见AudioSourceMode。
    ///
    /// 【第三轮：用户实测「爆音/ 漏词 / 第二句才听到第一句」】
    ///   ★三个症状**同一个根因**：ServeStream 没有实时节流 —— ring 里有多少就立刻推多少。
    ///   NPC 说 5 秒就把 5 秒音频瞬间灌进浏览器缓冲，手机按 24kHz 实时播放 →
    ///   积压数秒 ⇒ 听到的是「上一句」，且缓冲溢出丢包（漏词）+ TCP 突发（爆音）。
    ///   现改为：按 byteRate 严格实时节流 + 积压超过阈值就丢弃陈旧数据（跳到最新）。
    ///   通话场景里**宁可丢开头也不能延迟无限增长**。
    /// </summary>
    public static class AudioForwarder
    {
        private const string LogPrefix = "[AUDIO] ";
        private const string LogTag = LogPrefix + "> ";

        /// <summary>统一输出格式：48kHz / 16bit / 单声道。
        /// ★选48k 的理由：WASAPI loopback 默认就是 48kHz → 零重采样。
        ///   TTS 是 24kHz，用 2:1 线性插值升采样即可（纯托管，无 native 依赖）。
        ///   绝不能因为「TTS 是 24k」就让整个输出格式随音源变——那会让手机端
        ///   在流中途解码错乱。</summary>
        private const int TargetSampleRate = 48000;

        /// <summary>44 字节 WAV 头（第一块 PCM 到达时确定并缓存）。</summary>
        private static byte[]? _wavHeader;
        private static volatile WaveFormat _headerForFormat = new WaveFormat(TargetSampleRate, 16, 1);
        private static readonly object _headerLock = new object();

        /// <summary>「曾经有过真实音频」——与「此刻正在发声」区分开，便于诊断。</summary>
        private static volatile bool _hasAudio;
        private static int _chunkSeq;
        private static int _audioClients;
        private static string _deviceName = "等待音源…";
        private static string _audioSourceMode = "tts";
        private static string _lastError = "";

        /// <summary>积压上限：超过就把最旧的丢掉（跳到最新），保证手机端延迟有界。
        /// ★blockAlign=2（16bit 单声道）—— 环形缓冲的丢弃/读取都必须按它对齐。</summary>
        /// <summary>
        /// ★★音频订阅者（2026-10-14「杂音」根因修复，推翻共享单 ring）：
        /// 原来是所有连接共享一个 <see cref="RingBuffer"/>，每个 ServeStream 线程
        /// 都 TrimTo + Read 同一块数据。多连接时（用户手机直开 /audio 的同时
        /// /phone 页面也连着，日志反复出现「当前 2 个」）：
        ///   ① 连接之间**交替读走**对方的数据 → 两条流都是残缺的采样碎片 → 杂音；
        ///   ② A 连接的 TrimTo 把 B 还没读的陈旧数据当垃圾丢掉 → B 的流出现空洞；
        ///   ③ 旧连接断开后没人读，loopback 继续写 → 积压暴涨 → 新连接建立时
        ///      TrimTo 一次性丢一大批 → 刷新瞬间爆音（用户实测「刷新时杂音特别明显」）。
        /// 现改为**广播订阅**：每个连接一份独立 RingBuffer，loopback 数据到达时
        /// 分发给所有订阅者；积压上限各管各的，互不影响。
        /// </summary>
        private sealed class AudioSub
        {
            public readonly RingBuffer Ring;
            public readonly int MaxBacklogBytes;
            public AudioSub(int ringSize, int align, int maxBacklogBytes)
            {
                Ring = new RingBuffer(ringSize, align);
                MaxBacklogBytes = maxBacklogBytes;
            }
        }

        private static readonly List<AudioSub> _subs = new();
        private static readonly object _subGate = new();

        /// <summary>已推给客户端的音频块数（诊断：不变 = 采集或传输停了）。</summary>
        public static int ChunkCount => Volatile.Read(ref _chunkSeq);
        public static bool HasAudio => _hasAudio;
        public static int ClientCount => Volatile.Read(ref _audioClients);
        public static string DeviceName => _deviceName;
        public static string LastError => _lastError;
        public static string SourceMode => _audioSourceMode;
        public static int BufferedBytes
        {
            get
            {
                lock (_subGate)
                {
                    int total = 0;
                    foreach (var s in _subs) total += s.Ring.Count;
                    return total;
                }
            }
        }

        private static void L(string s) => MjpegServer.L(LogPrefix + s);

        /// <summary>
        /// 启动声音下行。<paramref name="mode"/> 决定音源：
        ///   "loopback"（默认）=抓系统输出混音 → **有 BGM/音效**，也含 NPC 语音
        ///   "tts"                = 只从 TTS 播放链路分路 → 只有 NPC 语音，但更干净
        /// </summary>
        public static void Start(int port, string mode)
        {
            _audioSourceMode = (mode == "loopback") ? "loopback" : "tts";
            L($"声音下行已挂到画面服务同一端口 /audio（配置项 AudioPort={port} 已不再单独监听）。");
            L($"音源模式：{_audioSourceMode}"
              + (_audioSourceMode == "loopback"
                  ? "→抓系统输出混音（NPC 语音 + 游戏 BGM + 音效）"
                  : "→ 只转发 NPC 语音（不含游戏 BGM/音效）"));

            if (_audioSourceMode == "loopback")
            {
                if (LoopbackTap.Start(L, OnLoopbackPcm, SetDeviceName))
                    L("已启动系统输出 loopback 采集，NPC 说话时请留意手机声音。");
                else
                {
                    L("★loopback 采集启动失败，回退为 tts 模式（只有 NPC 语音，没有 BGM/音效）。");
                    _audioSourceMode = "tts";
                }
            }
            else
            {
                L("等待 NPC 语音播放…（NPC 说话时手机才会有声音，这是预期行为）。");
            }
        }

        /// <summary>由 loopback 采集回调提供的设备名（诊断用）。</summary>
        private static void SetDeviceName(string name)
        {
            if (!string.IsNullOrEmpty(name)) _deviceName = name + "（系统输出混音）";
        }

        /// <summary>
        /// 供 <c>PcmVolumeWaveProvider.Read()</c> 调用：把一份已解码的 PCM 喂进来。
        /// 纯托管、无委托、无 Unity API，可在 NAudio 播放线程上安全调用。
        ///
        /// ★loopback 模式下**必须静默**：NPC 语音是本MOD 用 NAudio 播到系统设备的，
        ///   loopback 抓的混音里本来就有它；若这里再写一份，手机会听到**叠加两遍**的
        ///   NPC 语音（振幅超 1.0 →削波失真）= 用户报的「爆音」。
        /// </summary>
        public static void OnPcmAvailable(byte[] buffer, int offset, int count, WaveFormat format)
        {
            if (_audioSourceMode == "loopback") return;      // ★互斥：别叠加
            PushPcm(buffer, offset, count, format);
        }

        /// <summary>
        /// loopback 采集的入口（走独立路径，不受 tts 互斥守卫影响）。
        ///loopback 已经包含 NPC 语音，所以这里直接写缓冲。
        /// </summary>
        internal static void OnLoopbackPcm(byte[] buffer, int offset, int count, WaveFormat format)
        {
            PushPcm(buffer, offset, count, format);
        }

        /// <summary>
        /// 把一块 PCM 分发给所有订阅者（广播）。
        /// ★锁序：先 _subGate 取快照，再逐个锁 ring——绝不在持 _subGate 时
        ///   做长时间操作（ServeStream 线程增删订阅者不能因此阻塞）。
        ///   RingBuffer.Write 自带「满则丢最旧」，单个订阅者积压有界，
        ///   不需要（也不能）跨订阅者 Trim。
        /// </summary>
        private static void Broadcast(byte[] buffer, int offset, int count)
        {
            AudioSub[] snapshot;
            lock (_subGate) snapshot = _subs.ToArray();
            foreach (var s in snapshot)
            {
                lock (s.Ring) { s.Ring.Write(buffer, offset, count); }
            }
        }

        /// <summary>注册一个订阅者（新连接），返回其独立缓冲。断开时必须 <see cref="Unsubscribe"/>。</summary>
        private static AudioSub Subscribe(int ringSize, int align, int maxBacklogBytes)
        {
            var sub = new AudioSub(ringSize, align, maxBacklogBytes);
            lock (_subGate) _subs.Add(sub);
            return sub;
        }

        private static void Unsubscribe(AudioSub sub)
        {
            lock (_subGate) _subs.Remove(sub);
        }

        private static void PushPcm(byte[] buffer, int offset, int count, WaveFormat format)
        {
            if (count <= 0) return;
            try
            {
                // 格式头在第一块数据时确定并缓存（WAV 头里写死了采样率/声道）。
                // 后续即使格式变化也不重写，避免手机端解码错乱。
                lock (_headerLock)
                {
                    if (_wavHeader == null || !SameFormat(_headerForFormat, format))
                    {
                        _headerForFormat = format;
                        _wavHeader = BuildWavHeader(format);
                        L($"音频格式：{format.SampleRate}Hz {format.Channels}ch "
                          + $"{format.BitsPerSample}bit {format.Encoding}");
                    }
                }

                // ★广播给所有订阅者（每个连接一份独立缓冲，互不干扰）。
                //   loopback 回调线程只做纯内存拷贝，绝不阻塞（NAudio 事件回调
                //   抛异常会静默停掉整条流）。
                Broadcast(buffer, offset, count);
                _hasAudio = true;
            }
            catch (Exception e)
            {
                if (_lastError != e.Message)
                {
                    _lastError = e.Message;
                    L("PCM 转发异常：" + e.Message);
                }
            }
        }

        private static bool SameFormat(WaveFormat a, WaveFormat b)
        {
            return a.SampleRate == b.SampleRate
                && a.Channels == b.Channels
                && a.BitsPerSample == b.BitsPerSample
                && a.Encoding == b.Encoding;
        }

        // ------------------------------------------------------------ 推流（由 MjpegServer 在 Socket 线程调用）

        /// <summary>首帧音频最长等待秒数。超时返回 503，由手机端退避重试。</summary>
        private const int FirstAudioWaitSeconds = 20;

        /// <summary>
        /// 推 WAV 流。**由 MjpegServer.HandleClient 调用**（纯 Socket 线程，不碰 Unity）。
        ///
        /// ★★关键：严格实时节流。
        ///   原实现「ring 里有多少就立刻推多少」→ NPC 说完一句的音频一瞬间灌进浏览器，
        ///   手机按采样率慢慢播 ⇒ 听到的是上一句（延迟），缓冲溢出则漏词+ 爆音。
        ///   现在按 byteRate 计算「到此刻为止应该发多少字节」，只发那么多；
        ///   发不动就等，发超了就把 ring 里的陈旧数据丢掉跳到最新。
        /// </summary>
        public static void ServeStream(Stream stream)
        {
            byte[]? header;
            lock (_headerLock) { header = _wavHeader; }

            if (header == null)
            {
                // 还没人说过话。原地等一会，别把 audio 元素判死。
                var deadline = DateTime.UtcNow.AddSeconds(FirstAudioWaitSeconds);
                while (header == null && DateTime.UtcNow < deadline && MjpegServer.IsRunning)
                {
                    Thread.Sleep(200);
                    lock (_headerLock) { header = _wavHeader; }
                }
            }

            if (header == null)
            {
                var msg = Encoding.UTF8.GetBytes(
                    "audio not ready: 还没有采到任何声音（这不是端口问题，请稍后重试）");
                WriteHead(stream, "503 Service Unavailable", "text/plain; charset=utf-8", msg.Length);
                stream.Write(msg, 0, msg.Length);
                stream.Flush();
                return;
            }

            Interlocked.Increment(ref _audioClients);
            L($"音频客户端已连接（当前 {_audioClients} 个）。");

            // ★声明在 try 外：finally 的断开汇总要引用它们。
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long sentBytes = 0;
            AudioSub? sub = null;
            int byteRate = 0;   // ★移到 try 外：finally 的断开汇总要算丢弃 ms

            try
            {
                // 流式 WAV：不给 Content-Length，靠连接关闭定界。
                WriteHead(stream, "200 OK", "audio/wav", null,
                    "Cache-Control: no-cache, no-store, must-revalidate\r\n");
                stream.Write(header, 0, header.Length);
                stream.Flush();

                var fmt = _headerForFormat;
                int blockAlign = Math.Max(1, fmt.BlockAlign);
                byteRate = fmt.AverageBytesPerSecond;
                if (byteRate <= 0) byteRate = TargetSampleRate * blockAlign;

                // 手机端允许的最大积压。超过就丢最旧的 → 延迟恒定有界。
                // 320ms ≈ 少丢一点字；改成 150ms 会更实时但更容易丢词。
                int maxBacklog = byteRate * 320 / 1000;

                var buf = new byte[16384];
                var silence = new byte[Math.Max(2, blockAlign * 20)];   // 20ms 静音

                // ★订阅自己的独立缓冲（广播架构）：不再与其他连接共享 ring。
                //   新连接建立时缓冲是空的——天然不会读到上一个连接留下的
                //   陈旧数据（旧架构「刷新瞬间爆音」的根因之一）。
                sub = Subscribe(1 << 19, blockAlign, maxBacklog);
                // ★推流诊断（2026-10-14「没声音」故障用）：周期打印「服务端到底
                //   推没推、推了多少」。以前只有连/断两行，分不清是服务端没推
                //   还是客户端没播——这次两侧都要有客观指标。
                var diagClock = System.Diagnostics.Stopwatch.StartNew();
                long diagBytesAtLast = 0;

                while (MjpegServer.IsRunning)
                {
                    // 到此刻为止，「应该」已经发出多少字节（实时基准）。
                    long shouldHave = (long)(clock.Elapsed.TotalSeconds * byteRate);
                    int allowance = (int)Math.Min(buf.Length, Math.Max(0, shouldHave - sentBytes));
                    if (allowance < blockAlign) { Thread.Sleep(5); continue; }

                    // ★丢弃陈旧数据：积压超过阈值就跳到最新（只丢自己这份，
                    //   不再跨连接丢别人的数据）。
                    //   通话场景宁可丢开头，也不能让手机端滞后好几秒。
                    int got;
                    lock (sub.Ring) { sub.Ring.TrimTo(sub.MaxBacklogBytes); got = sub.Ring.Read(buf, allowance); }

                    if (got <= 0)
                    {
                        // 空闲：补静音保持流的时钟前进，否则浏览器可能停播。
                        stream.Write(silence, 0, silence.Length);
                        sentBytes += silence.Length;
                    }
                    else
                    {
                        stream.Write(buf, 0, got);
                        sentBytes += got;
                        // 没发满的部分用静音补齐，保持音频时间轴连续（避免爆音）。
                        int short0 = allowance - got;
                        if (short0 >= blockAlign)
                        {
                            int pad = Math.Min(short0, silence.Length);
                            stream.Write(silence, 0, pad);
                            sentBytes += pad;
                        }
                    }

                    stream.Flush();
                    Interlocked.Increment(ref _chunkSeq);

                    // 每 5 秒一次推流诊断：证明数据在离开 PC。
                    if (diagClock.Elapsed.TotalSeconds >= 5)
                    {
                        long delta = sentBytes - diagBytesAtLast;
                        diagBytesAtLast = sentBytes;
                        diagClock.Restart();
                        // ★本连接丢弃量（刷新爆音定位用）：TrimTo 跳过的陈旧音频。
                        //   刷新/重连时这个数大 = 积压曾超 maxBacklog → 丢头补尾 → 爆音感。
                        long droppedMs = byteRate > 0 ? sub.Ring.DroppedBytes * 1000L / byteRate : 0;
                        L($"音频推流中：{clock.Elapsed.TotalSeconds:F0}s·已推 {sentBytes / 1024}KB"
                          + $"（近5s {delta / 1024}KB≈{delta / 5 / 1024}KB/s）"
                          + $"，ring 积压 {BacklogMs()}ms，本连接丢弃 {droppedMs}ms"
                          + $"，块 {Volatile.Read(ref _chunkSeq)}。");
                    }
                }
            }
            catch
            {
                // 手机切后台/关页面导致断开，属正常路径。
            }
            finally
            {
                if (sub != null) Unsubscribe(sub);
                Interlocked.Decrement(ref _audioClients);
                // 断开汇总：本次连接到底推出去多少。0 字节 = 服务端侧问题，
                // 有字节 = 数据已到客户端，故障在播放端（下一步查手机侧）。
                // ★丢弃陈旧数据 ms 数（刷新爆音定位）：TrimTo 跳过量，大 = 积压爆过。
                long droppedMs = (sub != null && byteRate > 0)
                    ? sub.Ring.DroppedBytes * 1000L / byteRate : 0;
                L($"音频客户端已断开（本次共推 {sentBytes / 1024}KB，"
                  + $"丢弃陈旧 {droppedMs}ms，时长 {clock.Elapsed.TotalSeconds:F1}s）。");
            }
        }

        private static void WriteHead(Stream stream, string status, string contentType,
                                     int? contentLength, string extra = "")
        {
            var sb = new StringBuilder(192);
            sb.Append("HTTP/1.0 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (contentLength.HasValue)
                sb.Append("Content-Length: ").Append(contentLength.Value).Append("\r\n");
            sb.Append("Cache-Control: no-cache, no-store, must-revalidate\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append(extra);
            sb.Append("Connection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>JSON 状态（手机端显示声音链路是否真的通）。</summary>
        public static string BuildStatusJson()
        {
            return "{\"hasAudio\":" + (_hasAudio ? "true" : "false")
                 + ",\"chunks\":" + ChunkCount
                 + ",\"clients\":" + Volatile.Read(ref _audioClients)
                 + ",\"backlogMs\":" + BacklogMs()
                 + ",\"mode\":\"" + EscapeJson(_audioSourceMode) + "\""
                 + ",\"device\":\"" + EscapeJson(_deviceName) + "\""
                 + ",\"error\":\"" + EscapeJson(_lastError) + "\"}";
        }

        private static int BacklogMs()
        {
            var fmt = _headerForFormat;
            int rate = fmt.AverageBytesPerSecond;
            if (rate <= 0) return 0;
            return (int)(BufferedBytes * 1000L / rate);
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>
        /// 标准 44 字节 WAV 头（PCM）。
        /// ★布局里同时有 16 位字段（channels/blockAlign/bitsPerSample）和 32 位字段
        ///   （sampleRate/byteRate），**必须分开写**，用统一的 4 字节写会破坏偏移。
        ///   偏移：0 RIFF | 4 size | 8 WAVE | 12 fmt | 16 fmtsize | 20 format(16)
        ///        22 channels(16) | 24 sampleRate(32) | 28 byteRate(32)
        ///        32 blockAlign(16) | 34 bits(16) | 36 data | 40 datasize
        /// </summary>
        internal static byte[] BuildWavHeader(WaveFormat fmt)
        {
            var h = new byte[44];
            int channels = fmt.Channels;
            int sampleRate = fmt.SampleRate;
            int bits = fmt.BitsPerSample;
            int blockAlign = channels * bits / 8;
            int byteRate = sampleRate * blockAlign;

            WriteAscii(h, 0, "RIFF");
            WriteU32(h, 4, unchecked((int)0xFFFFFFFF));   // 流式：长度未知
            WriteAscii(h, 8, "WAVE");

            WriteAscii(h, 12, "fmt ");
            WriteU32(h, 16, 16);               // fmt 子块大小
            WriteU16(h, 20, 1);                // 1 = PCM
            WriteU16(h, 22, channels);
            WriteU32(h, 24, sampleRate);
            WriteU32(h, 28, byteRate);
            WriteU16(h, 32, blockAlign);
            WriteU16(h, 34, bits);

            WriteAscii(h, 36, "data");
            WriteU32(h, 40, unchecked((int)0xFFFFFFFF));   // 流式：长度未知
            return h;
        }

        private static void WriteAscii(byte[] b, int off, string s)
        {
            for (int i = 0; i < s.Length; i++) b[off + i] = (byte)s[i];
        }

        private static void WriteU16(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }
    }

    /// <summary>
    /// 极简环形字节缓冲：采集线程写、Socket 线程读，靠外部 lock 保证一致性。
    ///
    /// ★★两个已实测修复的 bug（离线验证 E:\a1c\ringuard）：
    ///  ① **超长写入时 _tail 未重置**：原实现在 `len >= 容量` 分支里只重置了
    ///     _head/_count，_tail 仍停在旧位置 → 后续写入**覆盖掉中间的有效数据**，
    ///     读出来是错乱的字节流 → **爆音**。
    ///  ② **积压时保留最旧的数据**：原实现满了就丢最旧的，于是手机端拿到的是
    ///     陈旧积压音频 → 「第二次对话才听到上一次」。通话场景要的是**跳到最新**，
    ///     所以改成 <see cref="TrimTo"/>：只保留最近 N 字节，丢弃更旧的。
    ///
    /// ★★第三个 bug（离线验证 E:\a1c\aligntest 抓出，用户症状「噼里啪啦的杂音」）：
    ///   **字节未按 blockAlign 对齐**。输出是 16bit PCM，采样占 2 字节；
    ///   而 `TrimTo` 的丢弃量是 `_count - maxBytes`（可能是奇数），
    ///   `Read` 的上限 `allowance` 也可能是奇数 → 读出奇数字节 →
    ///   **把一个采样切成两半**，前后两块波形错位 → 噼里啪啦。
    ///   实测 maxBacklog=301 时旧实现剩 301 字节（奇数 → 错位）。
    ///   修法：环形缓冲构造时接收 blockAlign，`TrimTo`/`Read` 全部按它取整。
    /// </summary>
    internal sealed class RingBuffer
    {
        private readonly byte[] _buf;
        private readonly int _align;
        private int _head, _tail, _count;

        /// <param name="blockAlign">采样字节数（16bit mono = 2）。所有丢弃/读取都对齐到它。</param>
        public RingBuffer(int size, int blockAlign)
        {
            _buf = new byte[size];
            _align = Math.Max(1, blockAlign);
        }

        public int Count { get { return _count; } }

        /// <summary>累计被TrimTo 丢弃的字节数（诊断：不为 0 说明手机端跟不上）。</summary>
        public long DroppedBytes { get; private set; }

        public void Write(byte[] src, int off, int len)
        {
            if (len <= 0) return;

            if (len >= _buf.Length)
            {
                // ★只保留最新的 len 字节：取 src 尾部的一段，_tail 归零。
                //   （原来这里 _tail 没重置 → 后续写入覆盖中间数据 → 爆音）
                int keep = _buf.Length - (_buf.Length % _align);
                if (keep <= 0) keep = _buf.Length;
                Array.Copy(src, off + (len - keep), _buf, 0, keep);
                _head = 0;
                _tail = 0;
                _count = keep;
                DroppedBytes += len - keep;
                return;
            }

            while (_count + len > _buf.Length)
            {
                _head = (_head + 1) % _buf.Length;
                _count--;
                DroppedBytes++;
            }
            int tail = _tail;
            for (int i = 0; i < len; i++)
            {
                _buf[tail] = src[off + i];
                tail = (tail + 1) % _buf.Length;
            }
            _tail = tail;
            _count += len;
        }

        /// <summary>
        /// ★丢弃陈旧数据，只保留最近 <paramref name="maxBytes"/> 字节。
        /// 通话场景：宁可丢开头，也不能让手机端滞后好几秒。
        /// ★丢弃量**向上取整到 blockAlign**，保证剩余字节数仍是对齐的。
        /// </summary>
        public void TrimTo(int maxBytes)
        {
            if (_count <= maxBytes) return;
            int drop = _count - maxBytes;
            int aligned = ((drop + _align - 1) / _align) * _align;
            int maxAlignedDrop = _count - (_count % _align);
            if (aligned > maxAlignedDrop) aligned = maxAlignedDrop;
            if (aligned <= 0) return;
            _head = (_head + aligned) % _buf.Length;
            _count -= aligned;
            DroppedBytes += aligned;
        }

        /// <summary>最多读<paramref name="maxBytes"/> 字节；实际读取量**向下取整到 blockAlign**。</summary>
        public int Read(byte[] dst, int maxBytes)
        {
            if (maxBytes > dst.Length) maxBytes = dst.Length;
            int n = Math.Min(_count, maxBytes);
            n -= n % _align;               // ★绝不能读出半个采样
            if (n <= 0) return 0;
            int head = _head;
            int first = Math.Min(n, _buf.Length - head);
            Array.Copy(_buf, head, dst, 0, first);
            if (n > first) Array.Copy(_buf, 0, dst, first, n - first);
            _head = (head + n) % _buf.Length;
            _count -= n;
            return n;
        }
    }
}
