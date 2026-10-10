# ASR 公开测评选型：160M 与 Paraformer Streaming

更新时间：2026-10-10。本轮仅核验公开模型卡、论文与上游结果表，没有下载或执行模型，没有进行本机性能／准确率测试。本文替代此前以反复本地测试决定选型的计划，不包含本机硬件、资源记录或私人样本文字。

## 选型结论

撤回此前“Paraformer Streaming 是替换 160M 的首选”建议。公开同表测评显示：Paraformer Streaming 在部分中文集合略好，但在其他集合明显更差，字符输出等待也更长。现有公开证据不能支持它对本项目准确档的全面升级。因此保留 14M 和当前 160M 更合理，取消生产发送前整段音频复核，自动标点作为共享文本模块加入。

允许更换整个后端，并不意味着必须更换。在没有明确收益的情况下，增加 Python 环境或重做流式后端会增加安装、兼容和维护成本；这属于本项目的工程判断。

## 当前 160M 的上游身份

当前使用的 `csukuangfj/sherpa-onnx-streaming-zipformer-zh-fp16-2025-06-30` 模型卡明确指向 `yuekai/icefall-asr-multi-zh-hans-zipformer-large`，并链接 icefall 对应 large 训练结果。它是约 160M 的 large，不能混同另一个约 700M 的 XL／xlarge 模型。[当前转换模型卡](https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-zh-fp16-2025-06-30)、[官方不同规模对应关系](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-transducer/zipformer-transducer-models.html)

icefall 官方约 160M、epoch 18、BPE 2000、byte fallback 的结果中，`Transducer Greedy Streaming` 对应如下 CER。数值越低越好。

| 测试集 | 流式 CER（%） | 同 checkpoint 的离线 CER（%） |
|---|---:|---:|
| AISHELL-1 test | 1.91 | 1.38 |
| AISHELL-2 test | 4.12 | 3.23 |
| WenetSpeech test meeting | 7.91 | 6.26 |
| WenetSpeech test net | 8.54 | 7.07 |

来源：[icefall multi_zh-hans 官方 RESULTS.md 的 large 流式结果](https://github.com/k2-fsa/icefall/blob/master/egs/multi_zh-hans/ASR/RESULTS.md#multi-chinese-datasets-char-based-training-results-streaming-on-zipformer-large-model)。离线列用于说明测试口径差异，不能当流式成绩，也不能把上游 greedy 结果写成本 Mod 改动后的 modified beam search 成绩。

## 可直接同表比较的公开论文

X2Streaming-ASR 论文 v3（2026-10-08）Table 1 同时报告 Zipformer 与 Paraformer 的中文流式 CER 和字符级平均输出延迟：

| 测试集 | Zipformer CER（%） | Paraformer 流式 CER（%） | Zipformer 字符平均延迟（ms） | Paraformer 字符平均延迟（ms） |
|---|---:|---:|---:|---:|
| AISHELL-1 | 1.97 | 3.06 | 472 | 585 |
| AISHELL-2 | 4.16 | 3.91 | 450 | 568 |
| AISHELL-3 | 2.94 | 3.60 | 464 | 576 |
| WenetSpeech Meeting | 7.87 | 10.05 | 435 | 582 |
| WenetSpeech Net | 8.39 | 8.26 | 409 | 563 |

来源：[X2Streaming-ASR v3 Table 1](https://arxiv.org/html/2609.08672v3#S3.T1)。此处只使用 v3，早期版本的部分数值已变化。

在这五个中文集合中，Zipformer 的 CER 在三个集合较低，Paraformer 在两个集合较低；Zipformer 的字符平均延迟在全部五个集合较低。这直接否定了“换成 Paraformer 就整体更准更快”的预设。

比较边界必须保留：论文没有给出足以锁定其 Zipformer 的具体模型仓库／修订／规模的信息，不能标成“本项目 160M FP16 的精确测评”。其 Zipformer 数值与官方 large 流式结果接近，是同系列的辅助证据，不能由接近反推 checkpoint 完全一致。字符延迟相对强制对齐的字符声学结束位置计算，不是 GPU 推理耗时、首字时间或本 Mod 点击发送耗时。Paraformer 的离线列来自独立离线模型，也不能拿来证明其流式版本准确率。

## Paraformer 官方模型卡能证明什么

官方 `funasr/paraformer-zh-streaming` 模型卡确认：220M 参数、16 kHz、中文／英文、online 分块推理、CUDA 使用示例。当前模型卡未提供可用的 CER 测评表。因此可以确认原生流式能力，却不能凭该卡宣称精度高于当前 160M。[官方 Paraformer Streaming 模型卡](https://huggingface.co/funasr/paraformer-zh-streaming)

必须区分离线 Paraformer、离线 SeACo 热词模型与 online checkpoint。网上引用的离线 CER 或“FunASR 支持热词”不能自动归给这款 streaming 模型。sherpa-onnx 官方热词说明明确：现有热词只适用于 transducer 的 `modified_beam_search`，Paraformer 不在其内。[官方热词限制](https://k2-fsa.github.io/sherpa/onnx/hotwords/index.html)

FunASR 当前 websocket 示例默认 two-pass，加载多个任务模型；其协议接受 hotword 不足以证明 ParaformerStreaming 原生热词生效，后者 inference 未读取该参数。照搬示例还会恢复用户准备删除的第二遍识别。[官方 websocket 服务](https://github.com/modelscope/FunASR/blob/main/runtime/python/websocket/funasr_wss_server.py)、[ParaformerStreaming 实现](https://github.com/modelscope/FunASR/blob/main/funasr/models/paraformer_streaming/model.py)

## 更新后的落地范围

1. 保留 14M 轻量档与 160M 准确档，运行时只加载所选一个 ASR；保留现有热词适配和常驻预热规则。
2. 取消生产停止／发送链路的全音频 replay、音频复核缓存和“整句校验”状态。保留处理尚未解码的最后音频、流式 InputFinished／final 和取消门控，避免最后几个字丢失。
3. 两个档共享独立标点模块。标点输入文本，补逗号、句号、问号，不重识别音频、不改词，也不声称修正人名。
4. 后台文本处理按版本控制，限制上下文窗口；用户手改文本不得覆盖。标点异常或超时允许发送原识别文字，稳定部分避免反复跳动。
5. 后续实施验证仅围绕已经选定的改动：尾音收尾、发送一次、取消／重启、标点和手改保护、缺包与故障处理。撤销反复下载候选、在本机横向跑多个模型以重新决定选型的计划。

sherpa-onnx 已有中文 CT-Transformer 标点模型与接口，INT8 权重文件约 72 MB；其中文接口是离线文本推理，后台增量文本更新需要插件管理。FunASR 另有带文本 cache 的 CTTransformerStreaming，可以作为以后确有需要时的独立后端方案，不能与 sherpa 接口混同。[sherpa 标点模型](https://k2-fsa.github.io/sherpa/onnx/punctuation/pretrained_models.html)、[FunASR 流式标点代码](https://github.com/modelscope/FunASR/blob/main/funasr/models/ct_transformer_streaming/model.py)

## 未由公开数据解决的问题

上述公开结果不能预测特定游戏专名、单个用户口音或标点体验，也不能给出本机 GPU 资源消耗。这里明确保留这些证据边界，不以“再无限测试候选模型”作为回复或实施前置条件。调研阶段未修改程序或发布包；用户接受结论后的 0.7.8 实施与功能验证见 [共享自动标点](ASR-PUNCTUATION-20261010.md)。
