# TTS service crash diagnostics — 2026-10-14

## Scope

This diagnostic path follows the `audio.cpp` process when the plugin starts it. It records process health, exit status, bounded stdout/stderr tails, and Windows error events that can be tied to the exact service PID. It does not claim that the game itself can keep writing a report after Windows terminates the entire game process.

An externally started service is monitored through its `/health` endpoint only. Its process handle, exit code, and private logs are not available to the plugin.

## What the game records

- The in-game **诊断** tab shows service state, NPC reply hook compatibility and firings, stage counts, the latest categorized error, confidence, and evidence. The report can be copied or exported under `A1IndexTTSMod/.state/tts-diagnostics/`.
- For a plugin-owned service, `AudioCppLifecycle` checks the tracked process and supervisor about once per second, and probes `/health` every five seconds. Three failed health probes report a hung/unhealthy service even if the process remains alive.
- The launcher keeps separate per-session stdout/stderr files and writes a structured failure report with the service PID and exit code when available.
- Application Error events 1000/1001 are included only when the event identifies both the executable and matching PID. Resource Exhaustion event 2004 is included only when its memory-consumer list names the exact `audio.cpp` PID.
- The classifier recognizes Windows resource-exhaustion status codes, explicit CPU/GPU allocation failures, access violations, fast-fail, missing or uninitializable DLLs, model-load failures, and CUDA/Vulkan/device-loss errors. Windows exception code evidence takes priority over unrelated earlier warnings in the service log.
- Event 2004 is reported as system memory pressure, not proof that Windows killed the TTS service for OOM. Unknown evidence remains `unknown_process_exit` with low confidence.
- TTS connection/HTTP failures also inspect recent logs for a likely related cause. A request error alone is not treated as proof of a process crash.

If the whole game exits without its normal shutdown marker, the next session reports that the previous run ended unexpectedly. This is a clue only: it cannot, by itself, identify whether the game, another plugin, the OS, or user action caused the exit.

## Limits

An evidence category is not always the underlying root cause. An access violation identifies the crash class; a native dump is needed to locate the faulty component and instruction. GPU OOM can be identified only when the service log or Windows status contains allocation/resource evidence. External services remain health-only.

## Verification status

The source includes a focused classifier/compatibility harness and a PowerShell syntax check is part of the release validation. No TTS process crash or OOM is injected by this implementation; diagnosis of a real native failure must be confirmed from the resulting service and Windows evidence. Game launch testing is not part of this change.
