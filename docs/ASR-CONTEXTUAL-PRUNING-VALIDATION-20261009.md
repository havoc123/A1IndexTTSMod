# ASR 专名正确率改进：原生热词筛选与结束结算

本轮继续提高正确率，修复 sherpa-onnx 的候选筛选顺序，并分别适配两档模型的热词权重。全程离线，不启动 WorldApart，不改模型权重，不增加第二个识别模型，生产候选数仍为 4。

## 原因与实现

固定检查 sherpa-onnx v1.13.8、提交 `11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf` 的实际实现：

- [modified beam decoder](https://github.com/k2-fsa/sherpa-onnx/blob/11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf/sherpa-onnx/csrc/online-transducer-modified-beam-search-decoder.cc) 先按声学分数 TopkIndex，再给入选 token 加热词分数。正确 token 如果提前被淘汰，随后调大热词权重也无法找回来。
- [GetResult](https://github.com/k2-fsa/sherpa-onnx/blob/11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf/sherpa-onnx/csrc/online-recognizer-transducer-impl.h) 没有在录音结束并排空后撤回未完成热词前缀的加分。前缀奖金可能影响最终选句。
- 原生源码明确说明 `temperature_scale` 只调整返回的置信度，不参与选词，本轮不使用它调正确率。

维护的 [原生补丁](../native/asr/sherpa-onnx-1.13.8-hotwords.patch) 将热词分数纳入 Top-k，避免入选后重复加分，保留声学置信度的原有记录。结束时在候选的副本上调用 ContextGraph::Finalize，再选择最终句子；不改变 stream 的原分数，因此重复读取最终结果不会再次扣分。这是对保留候选的结算，不是第二个模型对整句重新识别。

评分使用失败链上的稀疏转移。没有匹配转移的 token 统一撤回未完成前缀分数；只有当前状态至 root 的转移需要查询热词图。稀疏版本与全词表逐 token 查询版本在 11 条校准录音上的最终文字、token 序列、全部 partial 完全相同。

原生 DLL 导出 `A1SherpaOnnxHotwordRevision`，标记为 `a1-context-before-topk-finalize-v1`。仅检测到此版本才采用新的默认预算：轻量档每字 2.5；准确档普通词每字 3、包含 byte fallback 的词每字 5。旧官方 DLL 保留以前的 1.5 / 3 / 6 预算。每条编码路径仍按其 token 数归一化，游戏热词仍是 94 个，准确档句首和句中共 188 条路径，跳过 0 个。

流式草稿隐藏尚未拼完整的 UTF-8 替代符，最终文字保持原生输出。没有按错词替换人名，也没有把词表文字直接填入草稿。

## 数据与结果

共 19 条录音：18 条 Windows Microsoft Huihui Desktop 中文合成语音、1 条此前下载的官方自然语音样例。11 条用于校准，8 条预先保留，配置定下来后才运行保留集，未根据保留集再调参。专名重复出现按次数统计。合成参考是请求合成的文字，不是人工听辨转录。

字符错误率（CER）忽略标点和空白，严格比字，包括“他/她”等同音字差异。以下是该小型离线集的回归数据，不能当作真实玩家语音正确率。

| 模型与数据集 | 专名完整命中，原来 → 改进后 | 专名句 CER，原来 → 改进后 | 普通句 CER，原来 → 改进后 |
| --- | --- | --- | --- |
| 14M，11 条校准 | 6/16 → 10/16 | 19.35% → 15.05% | 6.41% → 6.41% |
| 14M，8 条保留 | 4/9 → 6/9 | 21.15% → 15.38% | 0% → 0% |
| 160M，11 条校准 | 6/16 → 13/16 | 32.26% → 19.35% | 1.28% → 1.28% |
| 160M，8 条保留 | 6/9 → 7/9 | 11.54% → 9.62% | 9.68% → 9.68% |

普通句未出现额外完整游戏热词。两档的最终输出均无 UTF-8 替代符。旧模型和新模型之间没有更换录音、参考、词表或模型权重。每条检查输入 sample 数；工具在结束时重复读取结果，确认文字和 tokens 不变。

具体改善包括：160M 句首“苏倾盏，你在这里吗”由“苏青产柴这里吗”变为完整正确文字；保留集“请苏倾盏过来，我们一起去焚天宗”也完整正确。14M 的“我想请纪小蝶和江楚弦帮忙”和“莲心和陆浩元去了镇岳宗”完整正确。

仍有错误：14M 多处把“苏倾盏”识别成“苏青展”；160M 的保留句“你见过苏倾盏吗”仍错误，部分校准句出现“共享张”“杜”等误识或额外文字。修复提高了总体专名命中，不能保证每个词、每句话都改善。该数据里 14M 对部分普通句和个别人名更好，160M 的专名总命中更多，模型大小不保证逐句准确率更高。

## 被拒绝的调参

下表均为 160M 的 11 条校准集；“普通错误”每项都是 1/78 个字符。

| 原生库与配置 | 专名命中 | 专名句 CER | 决策 |
| --- | --- | --- | --- |
| 官方库，4 路，原权重 | 6/16 | 32.26% | 基线 |
| 官方库，6 路 | 8/16 | 27.96% | 不采用，额外候选开销，出现尾字 |
| 官方库，8 路 | 9/16 | 26.88% | 不采用，先前空闲对照解码耗时接近翻倍 |
| 官方库，4 路，前补 400ms 静音 | 6/16 | 37.63% | 不采用，错误率上升；生产代码移除该实验路径 |
| 官方库，6 路，生僻词预算 9 | 8/16 | 41.94% | 不采用，幻觉尾字及残缺字符 |
| 修复库，4 路，普通 3 / 生僻 6 | 12/16 | 22.58% | 优于基线，但继续降低生僻权重 |
| 修复库，4 路，普通 2.5 / 生僻 4 | 11/16 | 23.66% | 不采用，句首专名退步 |
| 修复库，4 路，普通 3 / 生僻 5 | 13/16 | 19.35% | 采用，并通过保留集 |

14M 若只替换原生库、仍用旧预算 1.5，校准 CER 由 18/93 增至 19/93；`compare.py` 对这个配置实际返回失败。适配为 2.5 后，错误降至 14/93，专名命中 10/16，保留集也通过。因此两档分别适配，未共用同一权重。

## 资源与测试边界

生产仍为单模型、CUDA 请求、4 路候选、1 个推理 CPU 线程；不增加整句第二遍识别、自动词语替换或前置静音。原生库只替换 C API DLL，ONNX Runtime 1.28.2 / CUDA 12 / cuDNN 9 保持原安装。

原生实验期间检测到另一款游戏同时运行，GPU 使用率达到 83%–98%，显存总占用约 22GiB。因此此前空闲基线的耗时与本轮耗时不能直接比较，也不能据此声称内存或延迟降低。同一阶段的准确档保留集旧 / 新 RTF 为 0.771 / 0.880；轻量档旧 / 新为 0.254 / 0.725，这些顺序测量仍受变化中的 GPU 争用影响，不是独占资源性能结论。耗时原始值保留在本地 JSONL。

未启动 WorldApart，未做游戏帧率、真实玩家口音、噪声或长时间麦克风测试。没有将其它游戏或应用退出。构建及共享解码模块均使用实际运行库，离线测试没有 mock 识别结果。

## 复现与安装

生成录音需要 Windows 中文 SAPI 语音，在登录用户上下文运行。自然语音和此前混合样例若未存在于本地 fixtures，生成器会省略它们，并在 provenance 中记录实际数量。

```powershell
.\tools\AsrQualityCheck\Generate-SapiCases.ps1
dotnet build tools/AsrQualityCheck/AsrQualityCheck.csproj -c Release
# 替换原生库之前，先运行官方库，保存同一批录音的基线。
dotnet tools/AsrQualityCheck/bin/Release/net6.0/AsrQualityCheck.dll --game-root 'E:\你的游戏目录' --profile accurate160m --cases .state/asr-quality/accuracy-sweep/calibration.json --expected-hotwords 94 > .state/asr-quality/baseline.jsonl
.\scripts\Build-AsrNative.ps1
.\scripts\Install-AsrNative.ps1 -GameRoot 'E:\你的游戏目录'
dotnet tools/AsrQualityCheck/bin/Release/net6.0/AsrQualityCheck.dll --game-root 'E:\你的游戏目录' --profile accurate160m --cases .state/asr-quality/accuracy-sweep/calibration.json --expected-hotwords 94 --expected-native-revision a1-context-before-topk-finalize-v1 > .state/asr-quality/candidate.jsonl
python tools/AsrQualityCheck/compare.py .state/asr-quality/baseline.jsonl .state/asr-quality/candidate.jsonl
```

轻量档将 profile 换成 `lightweight14m`，保留集将 cases 换成 `heldout.json`。`compare.py` 检查录音 sample 数、参考、词表覆盖、4 路候选和原生版本，要求专名句错误下降、完整专名命中增加、普通句不退步、最终文字无替代符。`summarize.py` 输出聚合指标。版本、样本哈希和各句结果见 [JSON 记录](ASR-CONTEXTUAL-PRUNING-RESULTS-20261009.json)。

本地新 DLL SHA256：`d68b199b3d0747e65897e1e51a99f3b0083982512224646421316b6e230dc9aa`。原官方 DLL：`5332afa35b8dc7cb432df015a3664c82b12a056d69b68dc9db6e2d84426a4f73`。补丁 SHA256：`02b82e809354a1af36f5fc636b31e3456aa0a976bb315aaf5a65d6cee72faf93`。安装器校验来源和 DLL、备份旧库，并在失败时回退。此改进属于当前源码分支与本地安装，现有 Release 未重新打包或发布。

本地插件与新原生库均已通过安装器安装并校验；插件 SHA256 为 `3198d5fd2651ce4d3d33360d79dc74243163cb9a4dd251d849f6a4d19c7162dc`。安装后直接使用游戏目录内运行库，两个模型各重跑 2 条已知完整正确的专名录音，均通过 `--max-cer 0`、原生版本与 94 个热词覆盖检查。插件、共享离线工具 Release 构建均为 0 警告、0 错误。全过程仍未启动 WorldApart。
