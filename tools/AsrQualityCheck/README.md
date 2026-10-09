# ASR 共享核心验证工具

本工具直接编译游戏使用的 `AsrDecoderSession`、`AsrModelProfiles` 和 `StreamingAsr`，不另写一套测试解码器。需要 Windows x64、.NET 6 运行时，以及游戏目录内已安装的模型和 CUDA 依赖。

```powershell
dotnet build tools/AsrQualityCheck/AsrQualityCheck.csproj -c Release
dotnet tools/AsrQualityCheck/bin/Release/net6.0/AsrQualityCheck.dll --game-root 'E:\你的游戏目录' --profile accurate160m --wav 'D:\录音.wav' --reference '人工核对的文字'
dotnet tools/AsrQualityCheck/bin/Release/net6.0/AsrQualityCheck.dll --game-root 'E:\你的游戏目录' --profile accurate160m --capture --cancel-restart
```

`--profile` 为 `lightweight14m` 或 `accurate160m`。文件测试支持单/双声道 PCM WAV，重采样至 16 kHz，每块最多 1,600 样本，立即解码而不按真实时间等待。JSON 包括 partial、final、样本计数、加载预热耗时、解码 RTF 与收尾耗时。模型构造时执行的空音频预热不计入用户样本。CER 去标点与空白后计算字符编辑距离，参考文本必须由人核实；不传参考则输出 null。

`--greedy`、`--no-hotwords`、`--no-tail` 仅供相同录音差分验证；玩家 UI 固定 beam-4。`--debug` 开启上游诊断。文件测试成功退出仍需检查 JSON，不以未崩溃代替质量验收。

`--tokenize-only --profile accurate160m` 不加载 CUDA，输出游戏热词和 BPE token 序列，可与开发机的官方 SentencePiece 做逐词差分。正式插件不依赖 Python。准确档必须安装匹配的 `bpe.model`；资源 SHA256、全部模型 token 的顺序和 ID、256 个回退字节都由共享核心检查。原生路径使用同 ID 的内部别名，JSON 的 `tokens` 应始终是原模型的正常输出，不应出现私用区字符；生产核心对此也有断言。

`--expected-hotwords 94` 要求启用 94 个且跳过为零；`--max-cer 0 --reference '人工核对的文字'` 要求 CER 不超阈值，失败时非零退出。`--hotword-score 1.5` 可覆盖词组每字预算做同录音差分，参数只存在于验证工具。未覆盖时轻量档为 1.5；准确档普通路径为 3，含字节回退的生僻字路径为 6，再按路径 token 数分摊。句首与句中路径分别计分，UI 计数显示词组数而非路径数。普通语音与包含专名的正例应分别比较开启/关闭热词的输出，不能只验证加载数量。

`--capture` 会实际打开系统默认麦克风，约 600 毫秒后连续调用两次停止，要求到达就绪状态。加 `--cancel-restart` 时先取消并立即重新开始，断言最终会话 ID 属于新会话。采集完整性由生产路径中的 queued/processed 样本计数断言验证。日志写 stderr，JSON 写 stdout。它不将录音写盘、不连接 NPC 对话。

首次 CUDA 解码存在准备开销，测速度时不要并行运行两个模型。冷启动与稳态耗时分别记录；未经算子 profiler 验证，不把 Provider 配置当作全图 GPU 放置证据。
