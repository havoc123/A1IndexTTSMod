# ASR contextual pruning patch

This directory contains the local Apache-2.0 source patch against sherpa-onnx
v1.13.8, commit `11afbd009a7f8c08f4bcf2fc1b265d0df4670fbf`. Model weights and
tokenizer resources are unchanged. Their licensing remains separate.

The upstream modified beam decoder takes its acoustic top-k before adding
context scores. This patch includes context scores in that selection, applies
each score once, and preserves acoustic confidence traces. It also finalizes
incomplete context prefixes on a copy of the hypotheses after InputFinished
and drain, before selecting final text. Repeated GetResult calls remain stable.

To avoid querying every token through the context graph, the scorer initializes
absent transitions with the prefix cancellation score, then queries arcs on
the current state's failure chain through root. A token absent from that chain
necessarily returns to root. Blank and unknown tokens retain upstream behavior.
Dense and sparse implementations produced identical final texts, tokens, and
partial hypotheses on all 11 calibration recordings.

The DLL exports `A1SherpaOnnxHotwordRevision`, returning
`a1-context-before-topk-finalize-v2`. The managed integration accepts v1 and v2
for separately validated contextual hotword budgets; an upstream DLL keeps the
previous budgets. Streaming search remains 4 paths. In v2, the public stream
option `a1_final_paths` permits 8, 12 or 16 paths for a single-stream replay;
production final review uses 8. Each Decode resets the path limit from that
stream, so subsequent ordinary streams return to 4. The recognizer and CUDA
weights are reused. Managed selection preserves the original complete
hypothesis when replay loses an already completed hotword; it never rewrites
individual spellings. Final-review results and residency evidence are in
`docs/ASR-STARTUP-SEND-REVIEW-20261009.md`.

Build and install with the game closed, in a logged-in Windows user shell:

```powershell
.\scripts\Build-AsrNative.ps1
.\scripts\Install-AsrNative.ps1 -GameRoot 'E:\你的游戏目录'
```

Requires CMake, Git, Visual Studio 2022 or newer with C++ tools, and internet
access for the pinned upstream/dependency downloads. CUDA kernels come from
the same prebuilt ONNX Runtime 1.28.2 CUDA 12 package; nvcc is not required.
The build disables unrelated TTS, device, example, and server components.

Outputs are in `.state/asr-native`: the DLL, Apache license, and a manifest
with source, patch, and binary hashes. The installer checks the manifest and
current patch, preserves the previous DLL under runtime `.backups/<sha256>`,
and rolls it back on failure. ONNX Runtime/CUDA/cuDNN remain the existing
installed base runtime. `Install-ASR.ps1` installs that base and model resources;
it does not compile this patch or replace a complete existing runtime.

These are source-branch/local installation changes. Existing release archives
are not rebuilt or published by these scripts. Any future release carrying the
modified DLL must also carry its license, revision manifest, and this source
patch or a link to the corresponding source.

Offline evidence and rejected alternatives are in
`docs/ASR-CONTEXTUAL-PRUNING-VALIDATION-20261009.md`.
