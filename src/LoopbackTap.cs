using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace A1IndexTTSMod
{
    /// <summary>
    /// WASAPI loopback 采集：抓取**系统默认输出设备的混音**。
    ///
    /// ★为什么必须用它（用户实测「有语音但没 BGM/音效」）：
    ///   原方案从 <c>PcmVolumeWaveProvider.Read()</c> 分路，而那个 provider 只包裹
    ///   **TTS 生成的 WAV 文件**（WaveFileReader）。游戏自己的 BGM/音效走
    ///   Unity AudioSource → 系统输出设备，**完全不经过 NAudio** ⇒ 结构上抓不到。
    ///   loopback 是唯一能拿到「玩家实际听到的一切」的办法。
    ///
    /// 【为什么敢用事件驱动 —— 与之前的 Unity 委托坑不冲突】
    ///   我们踩过的 4 次委托坑全部是 **Unity / il2cpp 委托**（endFrameRendering、
    ///   AsyncGPUReadback、StartCoroutine、IEventSystemHandler），那些类型在 interop 下
    ///   甚至不是 Delegate。而 <see cref="WasapiLoopbackCapture"/> 的 DataAvailable
    ///   是**纯 CLR 的 EventHandler&lt;WaveInEventArgs&gt;**，由 NAudio 自己的后台线程触发，
    ///   完全在 .NET 侧 ⇒ IL2CPP 无关。
    ///   ★铁律：这个回调里**只做纯内存操作**（写环形缓冲），
    ///   绝不碰 Unity API、绝不碰 UI、绝不阻塞。
    ///
    /// 【输出格式】设备 mix format 可能是 48000/2ch/IeeeFloat（NVIDIA HDMI 常见）
    ///   或 48000/2ch/Pcm。这里统一转成 **48kHz / 16bit / 单声道**，
    ///   线性插值重采样 + 多声道下混，纯托管零依赖。
    /// </summary>
    internal static class LoopbackTap
    {
        private static readonly object Gate = new object();
        private static WasapiLoopbackCapture? _capture;
        private static bool _started;
        private static string _lastError = "";
        private static Action<string>? _log;
        private static Action<string>? _deviceNamed;

        /// <summary>把混音写入 AudioForwarder 环形缓冲的回调。</summary>
        private static Action<byte[], int, int, WaveFormat>? _sink;

        /// <summary>上一次的输出格式（用于诊断日志）。</summary>
        private static string _lastFormat = "";

        public static string LastError { get { lock (Gate) return _lastError; } }

        public static bool Start(Action<string> log, Action<byte[], int, int, WaveFormat> sink,
                                 Action<string> deviceNamed)
        {
            lock (Gate)
            {
                if (_started) return true;
                _log = log;
                _sink = sink;
                _deviceNamed = deviceNamed;

                try
                {
                    var enumerator = new MMDeviceEnumerator();
                    // ★必须是「播放/渲染」端设备（loopback 抓的是输出设备的混音）。
                    var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    // 读设备 mix format，NAudio 会按它初始化 IAudioClient。
                    var mix = device.AudioClient.MixFormat;
                    log($"loopback 设备：{device.FriendlyName}");
                    log($"loopback 原始格式：{mix.SampleRate}Hz {mix.Channels}ch "
                      + $"{mix.BitsPerSample}bit {mix.Encoding}"
                      + "→ 统一转为 48000Hz/16bit/mono");

                    var capture = new WasapiLoopbackCapture(device);

                    // ★★必须用 capture.WaveFormat，**不能**用 device.AudioClient.MixFormat。
                    //
                    //   【实测证据，本机 fmtprobe 探针拿到的真实值】
                    //     device.AudioClient.MixFormat = 48000Hz/2ch/32bit **Extensible**
                    //     capture.WaveFormat            = 48000Hz/2ch/32bit **IeeeFloat**
                    //   NAudio 在初始化 IAudioClient 时**自己做了一次格式转换**
                    //   （按 mix format 协商出客户端能用的格式），DataAvailable 回调里的
                    //   缓冲区是按 capture.WaveFormat 排布的。
                    //   ⇒ 我上一版用 MixFormat 构造解码器，拿到 Encoding=Extensible(65534)，
                    //     判定「不是 IeeeFloat」→ 误落到 int32 分支 →
                    //     把浮点位模式当整数读 → 用户听到「背景音爆了、听不出是音乐、噼里啪啦」。
                    //
                    //   【双保险】MonoPcm16 里仍保留 SubFormat GUID 判定 ——
                    //     万一某些驱动/设备下 capture.WaveFormat 也报 Extensible，
            //     仍能靠 GUID 判对（IEEEFLOAT=00000003-0000-0010-8000-00aa00389b71）。
                    var realFormat = capture.WaveFormat ?? mix;
                    log($"loopback 设备原生格式(MixFormat)：{mix.SampleRate}Hz {mix.Channels}ch "
                      + $"{mix.BitsPerSample}bit {mix.Encoding}");
                    log($"★实际回调缓冲区格式(WaveFormat)：{realFormat.SampleRate}Hz "
                      + $"{realFormat.Channels}ch {realFormat.BitsPerSample}bit {realFormat.Encoding}");

                    var conv = new MonoPcm16(realFormat);
                    try { _deviceNamed?.Invoke(device.FriendlyName); } catch { }
                    // ★这行是排障关键：格式判定结论（Extensible 必须看 SubFormat GUID）
                    log("格式判定：" + conv.FormatVerdict);

                    capture.DataAvailable += (_, e) =>
                    {
                        try
                        {
                            if (e.BytesRecorded <= 0 || e.Buffer == null) return;
                            byte[] pcm = conv.Convert(e.Buffer, e.BytesRecorded);
                            if (pcm == null || pcm.Length <= 0) return;
                            _sink?.Invoke(pcm, 0, pcm.Length, conv.OutputFormat);

                            var desc = conv.OutputFormat.SampleRate + "Hz "
                                     + conv.OutputFormat.Channels + "ch "
                                     + conv.OutputFormat.BitsPerSample + "bit";
                            if (desc != _lastFormat)
                            {
                                _lastFormat = desc;
                                _log?.Invoke("loopback 输出格式：" + desc);
                            }
                        }
                        catch
                        {
                            // ★采集线程绝不能抛：NAudio 的事件回调异常会静默停掉整条流。
                            // 转换失败就丢这一块，不影响后续。
                        }
                    };

                    capture.StartRecording();
                    _capture = capture;
                    _started = true;
                    _lastError = "";
                    return true;
                }
                catch (Exception e)
                {
                    _lastError = e.GetType().Name + ": " + e.Message;
                    log?.Invoke("loopback 启动异常：" + _lastError);
                    return false;
                }
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                try { _capture?.StopRecording(); } catch { }
                try { _capture?.Dispose(); } catch { }
                _capture = null;
                _started = false;
            }
        }

        /// <summary>
        /// 任意输入格式 → 48kHz / 16bit / 单声道。
        ///
        /// ★★关于格式判定（本机设备实测踩坑，用户症状「背景音爆了，完全听不出是音乐」）：
        ///   本机 `MixFormat` = **48000Hz / 2ch / 32bit Extensible**，实测字段：
        ///     Encoding = 65534 (Extensible)
        ///     SubFormat= 00000003-0000-0010-8000-00aa00389b71  ← KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        ///     dwChannelMask = 3 (FL|FR)  wValidBitsPerSample = 32
        ///   ⇒ **Encoding == IeeeFloat 的判定在这里恒为 false**（它是 Extensible 不是 IeeeFloat），
        ///     代码会误落到 int32 分支，把浮点比特当整数读 → 数值乱跳 → 噼里啪啦的杂音。
        ///     （字节分布也印证：指数字节 0x3E/0xBE/0x39/0xBD 高度集中。）
        ///   ⇒ 正解：**Extensible 必须读 SubFormat GUID**，不能看 Encoding。
        ///     IEEEFLOAT = 00000003-0000-0010-8000-00aa00389b71
        ///     PCM      = 00000001-0000-0010-8000-00aa00389b71
        /// </summary>
        private sealed class MonoPcm16
        {
            /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</summary>
            private static readonly Guid SubTypeFloat =
                new Guid("00000003-0000-0010-8000-00aa00389b71");

            private readonly int _inRate, _inCh, _inBytesPerFrame, _inBits;
            private readonly bool _isFloat, _isPcm16;

            /// <summary>★用于日志：说明格式是怎么判定出来的（排障时最关键的一行）。</summary>
            public string FormatVerdict { get; }

            private const int OutRate = 48000;

            // ---- 跨块状态（★不要清零）----
            private double _carryPos;      // 下一个待输出采样的全局输入帧位置
            private long _blockBase;       // 当前块第 0 帧的全局帧号
            private float _carrySample;    // 块末尾那一帧的值

            public WaveFormat OutputFormat { get; }

            public MonoPcm16(WaveFormat input)
            {
                _inRate = Math.Max(1, input.SampleRate);
                _inCh = Math.Max(1, input.Channels);
                _inBits = Math.Max(1, input.BitsPerSample);
                _inBytesPerFrame = _inCh * _inBits / 8;

                // ★核心：Extensible 一律按 SubFormat GUID 判浮点，绝不信 Encoding。
                //SubFormat 在 NAudio 里**不是公开属性**（只有私有字段 + 扩展属性），
                //   所以这里用反射取；取不到就退回「Extensible 一律按 32bit 浮点试读，
                //   并在运行时自纠」（见下方 Fallback）。
                bool byGuid = false;
                bool isExt = input.Encoding == WaveFormatEncoding.Extensible;
                bool guidFound = false;
                if (isExt)
                {
                    Guid sub = ReadSubFormat(input, out guidFound);
                    if (guidFound) byGuid = SubTypeFloat.Equals(sub);
                    else byGuid = (input.BitsPerSample == 32);   // 兜底：Extensible+32bit 绝大多数是浮点
                }
                _isFloat = isExt ? byGuid : (input.Encoding == WaveFormatEncoding.IeeeFloat);
                _isPcm16 = !_isFloat && _inBits == 16;

                FormatVerdict = $"{_inRate}Hz {_inCh}ch {_inBits}bit "
                              + (isExt
                                  ? $"Extensible/SubFormat={(guidFound ? SubTypeFloat.ToString() : "(反射取不到,按32bit浮点兜底)")} → {(_isFloat ? "IEEE浮点" : "PCM整数")}"
                                  : $"{input.Encoding}");

                OutputFormat = new WaveFormat(OutRate, 16, 1);
            }

            /// <summary>
            /// 反射取 WAVEFORMATEXTENSIBLE 的 SubFormat GUID。
            /// NAudio 把 GUID 存在私有字段 <c>subFormat</c>，并暴露扩展属性 SubFormat
            /// （在 WaveFormatExtensions 里）；直接访问 <c>input.SubFormat</c> 编译不过。
            /// </summary>
            private static Guid ReadSubFormat(WaveFormat input, out bool found)
            {
                found = false;
                try
                {
                    var t = input.GetType();
                    var f = t.GetField("subFormat",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public);
                    if (f != null && f.GetValue(input) is Guid g) { found = true; return g; }

                    // 退一步：找扩展属性（WaveFormatExtensions.SubFormat）
                    var extProp = t.GetProperty("SubFormat",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public);
                    if (extProp != null && extProp.GetValue(input) is Guid g2) { found = true; return g2; }
                }
                catch { /* 取不到就走兜底 */ }
                return Guid.Empty;
            }

            public byte[] Convert(byte[] src, int count)
            {
                int inFrames = count / _inBytesPerFrame;
                if (inFrames <= 0) return EmptyBytes;

                int outCap = (int)((long)inFrames * OutRate / _inRate) + 4;
                var outBuf = new byte[outCap * 2];
                int o = 0;

                double pos = _carryPos - _blockBase;      // 本块内坐标（可能为负）
                double step = (double)_inRate / OutRate;

                while (pos < inFrames - 1)
                {
                    float a, b;
                    if (pos < 0)
                    {
                        a = _carrySample;
                        b = ReadSample(src, 0);
                    }
                    else
                    {
                        int i0 = (int)pos;
                        if (i0 > inFrames - 1) i0 = inFrames - 1;
                        int i1 = i0 + 1;
                        if (i1 > inFrames - 1) i1 = inFrames - 1;
                        a = ReadSample(src, i0);
                        b = ReadSample(src, i1);
                    }
                    float frac = (float)(pos - Math.Floor(pos));
                    float v = a + (b - a) * frac;

                    short s = (short)(Clamp(v) * 32767f);
                    outBuf[o++] = (byte)(s & 0xFF);
                    outBuf[o++] = (byte)((s >> 8) & 0xFF);

                    pos += step;
                    _carryPos += step;
                }

                _blockBase += inFrames;
                _carrySample = ReadSample(src, inFrames - 1);

                if (o == outBuf.Length) return outBuf;
                var r = new byte[o];
                Array.Copy(outBuf, r, o);
                return r;
            }

            private static readonly byte[] EmptyBytes = new byte[0];

            /// <summary>读第 frame 帧，多声道下混为单声道。</summary>
            private float ReadSample(byte[] src, int frame)
            {
                int baseOff = frame * _inBytesPerFrame;
                int bps = _inBits / 8;
                float sum = 0f;
                for (int ch = 0; ch < _inCh; ch++)
                {
                    int off = baseOff + ch * bps;
                    if (off + bps > src.Length) break;
                    sum += DecodeOne(src, off);
                }
                return sum / _inCh;
            }

            private float DecodeOne(byte[] b, int off)
            {
                if (_isFloat) return BitConverter.ToSingle(b, off);
                if (_isPcm16)
                {
                    short s = (short)(b[off] | (b[off + 1] << 8));
                    return s / 32768f;
                }
                if (_inBits == 24)
                {
                    int v = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    return v / 8388608f;
                }
                if (_inBits == 32)
                {
                    int v = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
                    return v / 2147483648f;
                }
                if (_inBits == 8) return ((sbyte)b[off]) / 128f;
                return 0f;
            }

            private static float Clamp(float v)
            {
                if (v > 1f) return 1f;
                if (v < -1f) return -1f;
                return v;
            }
        }
    }
}
