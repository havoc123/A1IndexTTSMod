# 0.7.7：可选 ASR、自动显卡路由与 DirectML 14M

ASR 仍由游戏进程内的 sherpa-onnx 工作线程运行，不启动独立识别服务。模型权重与原生运行库独立于主程序安装，已有 CosyVoice GGUF 不需要重新下载。

## 安装状态与界面

| 状态 | 麦克风 | 第四页 | 预热 |
| --- | --- | --- | --- |
| 可选包缺失、模型不完整、运行库不是 v3、硬件不支持 | 隐藏，并恢复原输入框宽度 | 隐藏；保存的第四页回到第一页 | 不启动 |
| 完整安装但关闭语音输入 | 隐藏 | 保留，可重新开启 | 不启动，释放识别器 |
| 完整安装且启用 | 显示 | 显示 | 游戏启动即预热；一直常驻 |

首次配置缓存硬件与安装状态，避免每帧重做显卡和运行库检测。安装资源、改变 GPU 运行库之后需要重启游戏。录音中点击发送继续使用已有的会话代际和单次发送门控：先停止采集、排空尾帧、整段校验，再发送一次。

## GPU 路由

| 硬件 | TTS 自动路由 | ASR 自动路由 | ASR 模型 |
| --- | --- | --- | --- |
| NVIDIA | CUDA | CUDA | 14M / 已安装的 160M |
| AMD | Vulkan | DirectML | 固定 14M |
| Intel，支持 DirectX 12 | 现有手动 Vulkan 配置 | DirectML | 固定 14M |

混合显卡优先 NVIDIA。仅安装 DirectML 包的 NVIDIA 机器可使用该显卡的 DirectML 14M。AMD/Intel 的 DirectML 能力通过 Windows 10 1903 以上版本与 D3D12 设备能力检查；实际初始化仍可能因驱动失败，并会明确报错。

TTS 新装配置为 `Stage3Mvp.GpuBackend=Auto`。AMD 单显卡机器的旧 `Nvidia` 默认值在启动服务时自动解析为 Vulkan；可行的手动配置保留。执行服务器 `--list-devices` 获取实际 CUDA/Vulkan 序号，支持 `[GPU]` 和 `[IGPU]`，不直接复用 DXGI 序号。名称无法确定、同厂商存在多个含糊设备时报告错误。已有外部 TTS 服务继续沿用原来的连接和所有权规则。

ASR 的 DirectML 使用 DXGI 序号。UI、`SetProfile` 和共享解码器均限制 DirectML 为 14M，旧 160M 配置在加载前改为已安装的 14M。GPU 运行库有相同的 DLL 名称，进程内只能加载其中一种。

## 原生运行库与失败处理

继续固定 sherpa-onnx 1.13.8、上游提交 `11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf`，保留热词在 top-k 前评分、完整前缀收尾及 4 路流式 / 8 路终稿复核补丁。原生标记升级为 `a1-context-before-topk-finalize-v3`。

- CUDA 固定 ONNX Runtime 1.28.2、CUDA 12、cuDNN 9。
- DirectML 沿用该上游版本固定的 ONNX Runtime DirectML 1.14.1 / DirectML 1.15.0 包及 SHA256。
- DirectML 禁用内存模式，使用顺序执行，GPU 序号从受控原生接口传入。
- 三个图的 GPU provider 初始化都成功才允许预热与识别。初始化失败直接停止，C API 捕获初始化异常，托管层报告错误，避免静默整套退回 CPU。
- 这不表示全部辅助算子都在 GPU 执行；ORT 对部分形状、复制等节点仍使用 CPU。实际 GPU 执行另以 ORT profiling 验证。
- 构建输出记录上游、补丁、DLL 和运行库文件 SHA256，并保留运行库许可和第三方声明。安装器保留旧文件备份。

## 离线验证

离线检查覆盖 CUDA 与 DirectML 原生运行库初始化、实际 GPU provider 执行、流式解码及完整音频终稿复核。回归对照未发现相对于对应模型旧版基线的文本变化；此结果不代表识别准确率提升，也不能代表所有口音与游戏背景声。

其他检查包括：缺包不预热；安装后关闭仍有设置入口；启用恢复麦克风；不支持 DirectX 12 的设备被排除；DirectML 160M 在加载前拒绝；无效 DirectML 设备明确失败；后台保持模型；取消与快速切换只有一套权重；冷加载期间快速关开后，排队的旧释放任务会按最新开关状态补回预热；录音发送只发生一次。

插件与质量工具 Release 构建成功。安装器在离线目录中使用真实 0.7.6 程序集验证升级、旧 DLL 备份、热词配置保留及损坏包拒绝。包内暂存文件在负向测试后按原始 hash 恢复。全程没有启动游戏。

## 交付与升级

0.7.6 → 0.7.7 主程序补丁包含插件、托管依赖、按钮资源与 TTS 启停脚本，不覆盖已有配置、CosyVoice 模型与参考音。ASR 单独提供 CUDA 14M 包和 DirectML 14M 包；CUDA 包附固定版本的 160M 下载脚本，现有已安装 160M 保留。160M 权重及 BPE 的独立许可状态沿用 `THIRD_PARTY_NOTICES.md`，不随可分发包重打。

先运行主程序补丁，再安装对应 ASR 包；已安装本分支旧版 ASR 原生库的用户也需要更新至 v3。原版 0.7.6 安装包保留，可使用补丁升级。

构建资源包：

```powershell
.\scripts\Build-AsrNative.ps1 -Provider DirectML
.\scripts\Build-AsrNative.ps1 -Provider CUDA
.\scripts\Build-AsrBundle.ps1 -Provider DirectML
.\scripts\Build-AsrBundle.ps1 -Provider CUDA
```
