using System;
using System.Collections.Generic;
using System.Text;

namespace A1IndexTTSMod
{
    /// <summary>
    /// 极简 QR 编码器（ISO/IEC 18004 子集），专为「手机网页地址二维码」场景编写（2026-10-14）。
    ///
    /// 支持范围（刻意收窄，换来实现简洁 + 可对拍验证）：
    ///   · Byte 模式（0100）——URL 是 ASCII，正好落在这一种
    ///   · ECC 等级 M（约 15% 纠错）——地址码长度 ~30 字符，低版本足够
    ///   · 版本 1..10 自适应——ver 1-9 字符计数 8 位，ver 10 起 16 位
    ///   · 8 种 mask 全试，按标准罚分规则（1-4）选最优；mask 可外部强制（对拍用）
    ///
    /// ★为什么不引 QRCoder/NuGet：MOD 部署目录要多带一个 DLL，且 IL2CPP 下要一并
    ///   处理依赖；自己写 ~400 行反而可用 Python qrcode 库逐模块对拍（见 qrlab）。
    ///
    /// 输出：<see cref="QrMatrix.Modules"/>（true=黑），由 QrOverlay 贴成 Texture2D。
    /// </summary>
    internal static class QrEncoder
    {
        internal const int MinVersion = 1;
        internal const int MaxVersion = 10;

        // ------------------------------------------------------------------ 公共入口

        internal sealed class QrMatrix
        {
            /// <summary>Modules[r, c]：true=黑模块。左上角原点。</summary>
            public bool[,] Modules = null!;
            public int Size => Modules.GetLength(0);
            public int Version;
            public int Mask;
        }

        /// <summary>
        /// 把文本编码为 QR 矩阵（Byte 模式 / ECC M / 自动选版本）。
        /// forcedMask：-1=自动选最优；0..7=强制（仅测试用）。
        /// </summary>
        internal static QrMatrix Encode(string text, int forcedMask = -1)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            // Byte 模式按 UTF-8 编码（地址是 ASCII，与 latin1 等价；非 ASCII 也能编）。
            byte[] data = Encoding.UTF8.GetBytes(text);
            if (data.Length > 213) throw new ArgumentException($"文本 {data.Length} 字节超过 ver10-M 上限 213", nameof(text));

            int version = PickVersion(data);
            var codewords = BuildCodewords(data, version);          // 数据码字 + ECC，已交错
            var (modules, func) = BuildMatrix(version);
            PlaceData(modules, func, codewords, version);

            int mask = forcedMask >= 0 ? forcedMask : ChooseBestMask(modules, func, version);
            ApplyMask(modules, func, mask);
            WriteFormatInfo(modules, mask);
            if (version >= 7) WriteVersionInfo(modules, version);
            // dark module 最后写：罚分评估阶段它与格式区一样保持全白，
            // 与 python qrcode 的 test=True 评估矩阵一致（保证自动选 mask 相同）
            modules[modules.GetLength(0) - 8, 8] = true;

            return new QrMatrix { Modules = modules, Version = version, Mask = mask };
        }

        // ------------------------------------------------------------------ GF(256) 与 RS

        private static readonly byte[] GfExp = new byte[512];
        private static readonly byte[] GfLog = new byte[256];

        static QrEncoder()
        {
            // 本原多项式 0x11D（x^8+x^4+x^3+x^2+1），QR 标准固定值
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = (byte)x;
                GfLog[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
        }

        private static byte GfMul(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        /// <summary>RS 生成多项式（eccCount 个系数，最高次在前）。</summary>
        private static byte[] GeneratorPoly(int eccCount)
        {
            var poly = new byte[] { 1 }; // 从 g(x)=1 开始乘 (x - α^i)
            for (int i = 0; i < eccCount; i++)
            {
                var next = new byte[poly.Length + 1];
                for (int j = 0; j < poly.Length; j++)
                {
                    next[j] ^= poly[j];
                    next[j + 1] ^= GfMul(poly[j], GfExp[i]);
                }
                poly = next;
            }
            return poly;
        }

        /// <summary>对一个数据块算 eccCount 个 ECC 码字（多项式长除）。</summary>
        private static byte[] ComputeEcc(byte[] dataBlock, int eccCount)
        {
            var gen = GeneratorPoly(eccCount);
            var rem = new byte[eccCount];
            foreach (var b in dataBlock)
            {
                byte factor = (byte)(b ^ rem[0]);
                Array.Copy(rem, 1, rem, 0, eccCount - 1);
                rem[eccCount - 1] = 0;
                for (int i = 0; i < eccCount; i++)
                    rem[i] ^= GfMul(gen[i + 1], factor);
            }
            return rem;
        }

        // ------------------------------------------------------------------ 版本容量表（ECC M，ver 1..10）

        // 每块 ECC 码字数、每块数据码字数（允许两种块长，如 8-M/9-M/10-M）
        private static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        private static readonly int[] BlocksG1 = { 0, 1, 1, 1, 2, 2, 4, 4, 2, 3, 4 };
        private static readonly int[] DataPerBlockG1 = { 0, 16, 28, 44, 32, 43, 27, 31, 38, 36, 43 };
        private static readonly int[] BlocksG2 = { 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 1 };
        private static readonly int[] DataPerBlockG2 = { 0, 0, 0, 0, 0, 0, 0, 0, 39, 37, 44 };
        /// <summary>放置完数据码字后的剩余位数（ver 2-6 为 7，其余 0）。</summary>
        private static readonly int[] RemainderBits = { 0, 0, 7, 7, 7, 7, 7, 0, 0, 0, 0 };
        /// <summary>校正图案中心坐标表（按版本 1-based 索引，index 0 占位）。</summary>
        private static readonly int[][] AlignmentCenters =
        {
            new[] { 0 },                   // index 0 占位（与 EccPerBlock 等表保持 1-based）
            Array.Empty<int>(),            // ver 1：无校正图案
            new[] { 6, 18 },
            new[] { 6, 22 },
            new[] { 6, 26 },
            new[] { 6, 30 },
            new[] { 6, 34 },
            new[] { 6, 22, 38 },
            new[] { 6, 24, 42 },
            new[] { 6, 26, 46 },
            new[] { 6, 28, 50 },
        };

        private static int DataCapacityBytes(int version) =>
            BlocksG1[version] * DataPerBlockG1[version] + BlocksG2[version] * DataPerBlockG2[version];

        private static int PickVersion(byte[] data)
        {
            for (int v = MinVersion; v <= MaxVersion; v++)
            {
                int countBits = v <= 9 ? 8 : 16;
                int need = 4 + countBits + data.Length * 8;   // mode(4) + count + payload
                if (need <= DataCapacityBytes(v) * 8) return v;
            }
            throw new ArgumentException("文本过长");
        }

        // ------------------------------------------------------------------ 数据码字流

        /// <summary>返回已交错的完整码字流（数据码字 + ECC 码字，按标准顺序）。</summary>
        private static byte[] BuildCodewords(byte[] data, int version)
        {
            int capacityBits = DataCapacityBytes(version) * 8;
            int countBits = version <= 9 ? 8 : 16;

            var bits = new List<bool>(capacityBits);
            void Add(int value, int n) { for (int i = n - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }

            Add(0b0100, 4);                       // Byte 模式
            Add(data.Length, countBits);
            foreach (var b in data) Add(b, 8);
            // 终止符：最多 4 个 0（剩余不足 4 位则只补到字节边界）
            for (int i = 0; i < 4 && bits.Count < capacityBits; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);
            const byte PadEc = 0xEC, Pad11 = 0x11;
            bool toggle = false;
            while (bits.Count + 8 <= capacityBits)
            {
                byte pad = toggle ? Pad11 : PadEc; toggle = !toggle;
                Add(pad, 8);
            }

            // bits → 数据码字
            var dataCw = new byte[bits.Count / 8];
            for (int i = 0; i < dataCw.Length; i++)
            {
                byte v = 0;
                for (int j = 0; j < 8; j++) v = (byte)((v << 1) | (bits[i * 8 + j] ? 1 : 0));
                dataCw[i] = v;
            }

            // 分块（G1 块在前，G2 块在后）
            var blocks = new List<byte[]>();
            for (int i = 0; i < BlocksG1[version]; i++)
            {
                var blk = new byte[DataPerBlockG1[version]];
                Array.Copy(dataCw, i * blk.Length, blk, 0, blk.Length);
                blocks.Add(blk);
            }
            int offset = BlocksG1[version] * DataPerBlockG1[version];
            for (int i = 0; i < BlocksG2[version]; i++)
            {
                var blk = new byte[DataPerBlockG2[version]];
                Array.Copy(dataCw, offset + i * blk.Length, blk, 0, blk.Length);
                blocks.Add(blk);
            }

            // 每块算 ECC
            int eccCount = EccPerBlock[version];
            var eccBlocks = new byte[blocks.Count][];
            for (int i = 0; i < blocks.Count; i++) eccBlocks[i] = ComputeEcc(blocks[i], eccCount);

            // 交错：数据列优先，再 ECC 列
            var stream = new List<byte>(DataCapacityBytes(version) + blocks.Count * eccCount);
            int maxData = 0, maxEcc = eccCount;
            foreach (var b in blocks) maxData = Math.Max(maxData, b.Length);
            for (int i = 0; i < maxData; i++)
                foreach (var b in blocks)
                    if (i < b.Length) stream.Add(b[i]);
            for (int i = 0; i < maxEcc; i++)
                foreach (var e in eccBlocks) stream.Add(e[i]);
            return stream.ToArray();
        }

        // ------------------------------------------------------------------ 矩阵构建

        private static (bool[,] modules, bool[,] func) BuildMatrix(int version)
        {
            int size = version * 4 + 17;
            var m = new bool[size, size];
            var f = new bool[size, size];

            void Set(int r, int c, bool v, bool isFunc)
            {
                m[r, c] = v;
                if (isFunc) f[r, c] = true;
            }

            // ---- finder + separator（三个角）
            foreach (var (fr, fc) in new[] { (0, 0), (0, size - 7), (size - 7, 0) })
            {
                for (int dr = -1; dr <= 7; dr++)
                    for (int dc = -1; dc <= 7; dc++)
                    {
                        int r = fr + dr, c = fc + dc;
                        if (r < 0 || c < 0 || r >= size || c >= size) continue;
                        bool dark = dr is >= 0 and <= 6 && dc is >= 0 and <= 6
                            && (dr is 0 or 6 || dc is 0 or 6 || (dr is >= 2 and <= 4 && dc is >= 2 and <= 4));
                        Set(r, c, dark, true);
                    }
            }

            // ---- timing patterns（第 6 行/列）
            for (int i = 8; i < size - 8; i++)
            {
                bool dark = i % 2 == 0;
                Set(6, i, dark, true);
                Set(i, 6, dark, true);
            }

            // ---- alignment patterns
            var centers = AlignmentCenters[version];
            int reserve = size - 8;   // finder 8x8 保留区边界（含 separator）
            foreach (int cr in centers)
                foreach (int cc in centers)
                {
                    // ★QR 只有三个 finder（左上/右上/左下），右下角没有！
                    //   曾误加「右下重叠」条件，把 V2 的 (18,18) 等校正图案全删了，
                    //   function 图少 25 个模块 → 放置流从该处起整体错位。
                    //   只排除 5x5 校正区与三个 finder 8x8 保留区重叠的中心。
                    bool overlapFinder =
                        (cr - 2 <= 7 && cc - 2 <= 7) ||        // 左上
                        (cr - 2 <= 7 && cc + 2 >= reserve) ||  // 右上
                        (cr + 2 >= reserve && cc - 2 <= 7);    // 左下
                    if (overlapFinder) continue;
                    for (int dr = -2; dr <= 2; dr++)
                        for (int dc = -2; dc <= 2; dc++)
                        {
                            bool dark = Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1;
                            Set(cr + dr, cc + dc, dark, true);
                        }
                }

            // ---- 格式信息预留区（实际值在选完 mask 后写入）
            for (int i = 0; i <= 8; i++)
            {
                if (i != 6) { Set(8, i, false, true); Set(i, 8, false, true); }  // (8,6) 属于 timing，跳过
            }
            for (int i = 0; i < 8; i++)
            {
                Set(8, size - 1 - i, false, true);
                Set(size - 1 - i, 8, false, true);
            }

            // ---- 版本信息预留区（ver ≥7）
            if (version >= 7)
            {
                for (int i = 0; i < 18; i++)
                {
                    int a = size - 11 + i % 3, b = i / 3;
                    Set(a, b, false, true);
                    Set(b, a, false, true);
                }
            }

            // ---- dark module 预留（罚分阶段保持全白，Encode 末尾才写 true）
            Set(size - 8, 8, false, true);

            return (m, f);
        }

        // ------------------------------------------------------------------ 数据放置

        private static void PlaceData(bool[,] m, bool[,] f, byte[] stream, int version)
        {
            int size = m.GetLength(0);
            int totalBits = stream.Length * 8 + RemainderBits[version];
            int bitIdx = 0;
            bool GetBit()
            {
                if (bitIdx >= stream.Length * 8) return false;   // remainder bits 补 0
                int b = stream[bitIdx / 8] & (1 << (7 - bitIdx % 8));
                bitIdx++;
                return b != 0;
            }

            bool upward = true;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;                       // 跳过 timing column
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int c = right - j;
                        int r = upward ? size - 1 - vert : vert;
                        if (f[r, c]) continue;
                        bool bit = bitIdx < totalBits ? GetBit() : false;
                        m[r, c] = bit;
                    }
                }
                upward = !upward;
            }
        }

        // ------------------------------------------------------------------ Mask

        private static bool MaskRule(int rule, int r, int c) => rule switch
        {
            0 => (r + c) % 2 == 0,
            1 => r % 2 == 0,
            2 => c % 3 == 0,
            3 => (r + c) % 3 == 0,
            4 => (r / 2 + c / 3) % 2 == 0,
            5 => (r * c) % 2 + (r * c) % 3 == 0,
            6 => ((r * c) % 2 + (r * c) % 3) % 2 == 0,
            7 => ((r + c) % 2 + (r * c) % 3) % 2 == 0,
            _ => false,
        };

        private static void ApplyMask(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (!f[r, c] && MaskRule(mask, r, c))
                        m[r, c] = !m[r, c];
        }

        private static int ChooseBestMask(bool[,] m, bool[,] f, int version)
        {
            int best = 0, bestScore = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(m, f, mask);
                int score = PenaltyScore(m);
                ApplyMask(m, f, mask); // 再异或一次还原
                if (score < bestScore) { bestScore = score; best = mask; }
            }
            return best;
        }

        /// <summary>标准罚分规则 1-4（ISO/IEC 18004 §8.8.2）。</summary>
        private static int PenaltyScore(bool[,] m)
        {
            int size = m.GetLength(0);
            int score = 0;

            // 规则 1：行/列连续同色 ≥5
            foreach (bool byRow in new[] { true, false })
            {
                for (int i = 0; i < size; i++)
                {
                    int run = 1;
                    for (int j = 1; j < size; j++)
                    {
                        bool cur = byRow ? m[i, j] : m[j, i];
                        bool prev = byRow ? m[i, j - 1] : m[j - 1, i];
                        if (cur == prev) run++;
                        else
                        {
                            if (run >= 5) score += 3 + run - 5;
                            run = 1;
                        }
                    }
                    if (run >= 5) score += 3 + run - 5;
                }
            }

            // 规则 2：2x2 同色块
            for (int r = 0; r < size - 1; r++)
                for (int c = 0; c < size - 1; c++)
                {
                    bool v = m[r, c];
                    if (m[r, c + 1] == v && m[r + 1, c] == v && m[r + 1, c + 1] == v) score += 3;
                }

            // 规则 3：1:1:3:1:1 比例 + 前后各 4 个白模块，即 11 位
            //   10111010000 或 00001011101（ISO/IEC 18004 §8.8.2，与 python qrcode 一致）
            int[] p1 = { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
            int[] p2 = { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };
            foreach (bool byRow in new[] { true, false })
            {
                for (int i = 0; i < size; i++)
                {
                    for (int j = 0; j + 11 <= size; j++)
                    {
                        bool eq1 = true, eq2 = true;
                        for (int k = 0; k < 11; k++)
                        {
                            bool v = byRow ? m[i, j + k] : m[j + k, i];
                            if (v != (p1[k] == 1)) eq1 = false;
                            if (v != (p2[k] == 1)) eq2 = false;
                        }
                        if (eq1 || eq2) score += 40;
                    }
                }
            }

            // 规则 4：暗模块比例（与 python qrcode 同为 double 运算，保证选 mask 一致）
            int dark = 0;
            for (int r = 0; r < size; r++) for (int c = 0; c < size; c++) if (m[r, c]) dark++;
            double percent = (double)dark / (size * size);
            int steps = (int)(Math.Abs(percent * 100 - 50) / 5);
            score += steps * 10;

            return score;
        }

        // ------------------------------------------------------------------ 格式 / 版本信息

        private static void WriteFormatInfo(bool[,] m, int mask)
        {
            // ECC M = 0b00
            int format = (0b00 << 3) | mask;
            int bch = BchFormat(format);
            bool Bit(int i) => ((bch >> i) & 1) != 0;   // i=0 是 LSB（标准放置顺序）

            int size = m.GetLength(0);
            // ★2026-10-14 踩坑：参考实现 nayuki 的 setFunctionModule(x, y) 是 (列, 行)，
            //   照抄成 m[y, x] 才对；当初按 m[x, y] 写导致整个格式区被转置，
            //   QR 扫不出。下面坐标均已是 C# 的 [行, 列]，与 python qrcode 对拍验证过。
            // 副本 1（绕左上 finder）
            for (int i = 0; i <= 5; i++) m[i, 8] = Bit(i);   // col 8, rows 0-5
            m[7, 8] = Bit(6);
            m[8, 8] = Bit(7);
            m[8, 7] = Bit(8);
            for (int i = 9; i < 15; i++) m[8, 14 - i] = Bit(i);  // row 8, cols 5..0
            // 副本 2（右上 + 左下）
            for (int i = 0; i < 8; i++) m[8, size - 1 - i] = Bit(i);      // row 8, cols size-1..size-8
            for (int i = 8; i < 15; i++) m[size - 15 + i, 8] = Bit(i);    // col 8, rows size-7..size-1
        }

        /// <summary>15 位 BCH(15,5)，生成多项式 0x537（11 位），结果 XOR 0x5412。</summary>
        private static int BchFormat(int data5)
        {
            int v = data5 << 10;
            // 0x537 最高位索引 10：v 的最高位索引 ≥10 时持续异或消元
            while (BitLength(v) - 1 >= 10)
                v ^= 0x537 << (BitLength(v) - 1 - 10);
            return ((data5 << 10) | v) ^ 0x5412;
        }

        private static int BitLength(int v)
        {
            int n = 0;
            while (v != 0) { n++; v >>= 1; }
            return n;
        }

        private static void WriteVersionInfo(bool[,] m, int version)
        {
            // 18 位 BCH(18,6)，生成多项式 0x1F25（13 位，最高位索引 12），不 XOR
            int v = version << 12;
            while (BitLength(v) - 1 >= 12)
                v ^= 0x1F25 << (BitLength(v) - 1 - 12);
            int bits = (version << 12) | v;
            bool Bit(int i) => ((bits >> i) & 1) != 0;

            int size = m.GetLength(0);
            for (int i = 0; i < 18; i++)
            {
                int a = size - 11 + i % 3, b = i / 3;
                m[a, b] = Bit(i);
                m[b, a] = Bit(i);
            }
        }
    }
}
