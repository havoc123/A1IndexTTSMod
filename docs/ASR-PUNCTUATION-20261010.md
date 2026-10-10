# 0.7.8：流式 ASR 收尾与共享自动标点

## 实施范围

保留 sherpa-onnx 1.13.8、自定义 v3 原生库、Zipformer 14M 与 160M。
NVIDIA 使用 CUDA；AMD/Intel DirectML 仅允许 14M。
本次不替换声学后端，不做本地模型选型或私有语音样本对比。
公开选型依据见 [Paraformer Streaming 调研](ASR-FUNASR-SELECTION-20261010.md)。

生产录音默认关闭 replay：不保留整段音频用于第二遍识别，不启动宽搜索复核。
停止时仍排空采集队列、检查样本完整性、补齐流式尾部并通知 InputFinished，
避免末尾几个字因直接截断而丢失。离线诊断工具显式 `--review` 才能保留原重解码路径。

## 标点处理

- 使用 sherpa-onnx 官方 CT-Transformer 中英 INT8 ONNX，14M / 160M 共用一份。
- 与 ASR 一起异步预热，在独立后台线程以 CPU 单线程处理文字；不增加 Python 服务。
- 最新请求覆盖排队中的旧临时请求，临时处理最短间隔 600 ms。
- 最近 128 个 UTF-16 单元作为活动窗口，保护代理对边界。ASR 向前修订时
  对改变的文本前缀按有限长度分块重建，不重新提交音频。
- 仅接受插入标点的结果。去除允许的标点后必须与原识别文字完全一致；
  改字、删字、大小写或空格改动一律退回原文。已有标点或小数点的原文保留。
- 临时草稿保留内部标点，去掉句尾句号／问号／感叹号；停止后允许句尾标点。
- 最终文字处理最多等待 500 ms，超时返回原文字。缺包、校验失败或推理故障
  只影响标点，不阻止语音输入、停止或发送。
- SessionId、原文字与版本共同控制回写；取消录音、手改文字、切换 NPC 后
  旧结果没有回写权限。完成发送后的迟到结果不发布。

标点按文本预测，不能声称理解全部语调或确保每个问号正确，亦不承担错字纠正。
保留启动预热、模型常驻及单次发送保护；关闭 ASR 时释放声学和标点模型。

## 安装与升级

已有 0.7.7 ASR：安装 0.7.7 → 0.7.8 主程序补丁，再安装独立标点包。
已有 0.7.6 分体程序包：安装对应 0.7.6 → 0.7.8 补丁，再按需安装 ASR 包。
已有有效 v3 原生库和声学模型可复用。新构建的 ASR 可选包包含共享标点模型。
未安装整个 ASR 包时继续隐藏麦克风／第四页；仅缺标点包时 ASR 仍可使用。
设置页分别显示 ASR 与标点状态。所有安装需关闭游戏，安装后重启生效。

`scripts/Install-AsrPunctuation.ps1` 支持固定哈希的官方下载及 `-ModelFile` 离线安装，
先校验、再暂存和替换，旧目录保留备份。独立标点包不包含声学模型／GPU 运行库。
模型文件 75,519,198 字节；官方压缩包 64,717,756 字节。这些为发行文件大小，
不是本机内存／显存用量。

来源与许可证见 `licenses/asr-punctuation/NOTICE.txt` 和第三方说明。

## 功能验证

30 项离线功能检查通过，覆盖插入标点、字词保护、向前修订、最新请求、
取消／手改保护、超时和故障回退、单次发送。现有 CUDA 与 DirectML 两套
原生运行库的标点接口均通过人工合成文本的逗号、句号、问号与字词保留检查，
主程序 Release 构建无警告／错误。真实 0.7.6、0.7.7 插件 DLL 的升级补丁
和独立标点包已在离线安装夹具中验证，确认版本、配置保留、旧 DLL／模型备份，
以及损坏模型在替换前被拒绝；未执行夹具中的游戏入口。
验证仅使用人工合成文本和受控假标点引擎，
不读取私人语音样本，不启动游戏，不收集或发布本机硬件及资源计数。

```powershell
dotnet run --project tests/AsrPunctuation -c Release --no-restore
dotnet run --project tools/AsrQualityCheck -c Release --no-restore -- --game-root '<游戏目录>' --provider cuda --punctuation-only
dotnet run --project tools/AsrQualityCheck -c Release --no-restore -- --game-root '<游戏目录>' --provider directml --punctuation-only
.\scripts\Test-AsrPunctuationRelease.ps1
```

原生标点检查只加载文本模型，不加载声学模型；两个运行库在分开的进程中验证。
这证明代码、队列与接口能工作，不构成真实对话识别正确率或游戏运行验收。
