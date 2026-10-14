using System.Text.RegularExpressions;

namespace A1IndexTTSMod;

internal sealed record TtsServiceCrashDiagnosis(string Category, string Confidence, string Summary, string Evidence);

internal static class TtsServiceCrashClassifier
{
    private static readonly Regex OutOfMemory = new(
        "(?i)(out of memory|std::bad_alloc|bad alloc|cuda_error_out_of_memory|cudaerror_memoryallocation|cuda error.*memory|vk_error_(?:out_of_device|out_of_host)_memory|failed to allocate|allocation failed|not enough memory|显存不足|内存不足)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LowSystemMemory = new(
        "(?i)(event\\s*(?:id\\s*)?2004|resource-exhaustion-detector|low virtual memory|commit(?:ment)? limit|系统提交限制|低虚拟内存)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GpuFailure = new(
        "(?i)(cuda.*(error|failed|failure)|vulkan.*(error|failed|failure)|vk_error_device_lost|device lost|gpu.*(error|failed|failure))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ModelFailure = new(
        "(?i)(failed to load model|model load.*(failed|error)|invalid gguf|unsupported model|model file.*(missing|invalid))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static TtsServiceCrashDiagnosis Classify(int? exitCode, string? standardError,
        string? standardOutput, string? eventLogEvidence, string? supervisorMessage)
    {
        var source = string.Join("\n", new[] { standardError, standardOutput, eventLogEvidence, supervisorMessage }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var memoryLog = OutOfMemory.Match(string.Join("\n", standardError, standardOutput, supervisorMessage));
        var hasLowMemoryEvent = !string.IsNullOrWhiteSpace(eventLogEvidence) && LowSystemMemory.IsMatch(eventLogEvidence);

        // Application Error 1000 can carry the exception code even when the
        // process handle only exposes a generic exit code. Prefer that direct
        // crash evidence over unrelated warnings earlier in the service log.
        var exceptionCode = ReadExceptionCode(eventLogEvidence ?? "");
        if (exceptionCode == 0) exceptionCode = ReadExceptionCode(standardError ?? "");
        if (exceptionCode == 0) exceptionCode = ReadExceptionCode(standardOutput ?? "");
        var processCode = exitCode.HasValue ? unchecked((uint)exitCode.Value) : 0u;
        var code = exceptionCode != 0 ? exceptionCode : processCode;
        if (code == 0) code = ReadExceptionCode(source);

        if (code is 0xC0000017 or 0xC000009A or 0xC000012C or 0xC000012D)
        {
            var evidence = ExitEvidence(exitCode, code, source);
            if (hasLowMemoryEvent) evidence = AppendEvidence(evidence, "同时匹配到 Windows 低虚拟内存事件");
            return new("windows_resource_exhaustion", "high",
                $"Windows 报告虚拟内存或系统资源不足 (0x{code:X8})，与 TTS 进程退出直接相关",
                evidence);
        }

        var failureClass = ClassifyWindowsFailureCode(code);
        if (failureClass != null)
        {
            var evidence = ExitEvidence(exitCode, code, source);
            var faultingModule = ReadFaultingModule(eventLogEvidence ?? "");
            var summary = failureClass.Value.Summary + (faultingModule.Length == 0 ? "" : "；Windows 记录的故障模块=" + faultingModule);
            if (memoryLog.Success) evidence = AppendEvidence(evidence, "日志另有分配失败线索：" + memoryLog.Value);
            if (hasLowMemoryEvent) evidence = AppendEvidence(evidence, "退出前后另有 Windows 低虚拟内存事件；它是相关线索，不单独证明崩溃由 OOM 引起");
            return new(failureClass.Value.Category, "high", summary, evidence);
        }

        if (memoryLog.Success)
            return new("probable_oom", "high", "服务日志明确报告内存或显存分配失败", SafeEvidence(memoryLog.Value, source));

        if (hasLowMemoryEvent)
        {
            var memoryEvidence = SafeEvidence(LowSystemMemory.Match(eventLogEvidence!).Value, eventLogEvidence!);
            return new("system_memory_pressure", "medium",
                "Windows 在 TTS 进程退出前后记录了低虚拟内存/提交量事件；这表示系统内存压力，但不能单独证明 Windows 因 OOM 杀死了 TTS",
                memoryEvidence);
        }

        var gpuMatch = GpuFailure.Match(source);
        if (gpuMatch.Success)
            return new("gpu_or_driver_failure", "medium", "日志包含 GPU、CUDA 或 Vulkan 错误；具体根因未确认", SafeEvidence(gpuMatch.Value, source));
        var modelMatch = ModelFailure.Match(source);
        if (modelMatch.Success)
            return new("model_load_failure", "medium", "日志包含模型加载或模型格式错误", SafeEvidence(modelMatch.Value, source));

        var evidenceLine = FindUsefulLogLine(source);
        var exitSummary = exitCode.HasValue
            ? $"退出码 0x{unchecked((uint)exitCode.Value):X8} ({exitCode.Value})"
            : "没有取得进程退出码";
        var normalExit = processCode == 0;
        return new("unknown_process_exit", "low",
            normalExit ? $"服务进程意外结束，{exitSummary}；现有证据不能确定原因" : $"服务进程异常结束，{exitSummary}；现有证据不能确定原因",
            evidenceLine.Length == 0 ? exitSummary : evidenceLine);
    }

    private static (string Category, string Summary)? ClassifyWindowsFailureCode(uint code) => code switch
    {
        0xC0000005 => ("access_violation", "Windows 报告访问冲突 (0xC0000005)；触发访问冲突的根因仍需转储分析"),
        0xC0000409 => ("runtime_fast_fail", "Windows 检测到进程快速失败 (0xC0000409)；需崩溃转储确认根因"),
        0xC0000135 => ("missing_dependency", "Windows 报告启动所需 DLL 缺失 (0xC0000135)"),
        0xC0000142 => ("dependency_initialization_failure", "Windows 报告 DLL 初始化失败 (0xC0000142)"),
        _ => null
    };

    private static string AppendEvidence(string evidence, string addition) =>
        TtsDiagnostics.Sanitize(evidence + "；" + addition, 240);

    private static string SafeEvidence(string match, string source)
    {
        var line = source.Split('\n').LastOrDefault(candidate => candidate.Contains(match, StringComparison.OrdinalIgnoreCase)) ?? match;
        return TtsDiagnostics.Sanitize(line, 240);
    }

    private static string ExitEvidence(int? exitCode, uint diagnosticCode, string source)
    {
        var processText = exitCode.HasValue
            ? $"进程退出码 0x{unchecked((uint)exitCode.Value):X8}"
            : "没有取得进程退出码";
        var codeText = diagnosticCode == (exitCode.HasValue ? unchecked((uint)exitCode.Value) : 0u)
            ? ""
            : $"；Windows 异常码 0x{diagnosticCode:X8}";
        var line = FindUsefulLogLine(source);
        return TtsDiagnostics.Sanitize(processText + codeText + (line.Length == 0 ? "" : "；" + line), 240);
    }

    private static string FindUsefulLogLine(string source)
    {
        var lines = source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return TtsDiagnostics.Sanitize(lines.LastOrDefault(line =>
            Regex.IsMatch(line, "(?i)(error|fatal|failed|exception|panic|memory|alloc|cuda|vulkan|device lost|resource exhaustion|low virtual memory)"))
            ?? lines.LastOrDefault() ?? "", 240);
    }

    private static uint ReadExceptionCode(string source)
    {
        var match = Regex.Match(source, "(?i)(?:exception[\\s_-]*code|exit code)\\s*[:=]?\\s*(0x[0-9a-f]{8})");
        if (!match.Success) return 0;
        return uint.TryParse(match.Groups[1].Value[2..], System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var code) ? code : 0;
    }

    private static string ReadFaultingModule(string source)
    {
        var match = Regex.Match(source, "(?i)(?:faulting\\s+)?module(?:[\\s_-]*name)?\\s*[:=]\\s*([^\\s,;|]+)");
        return match.Success ? TtsDiagnostics.Sanitize(match.Groups[1].Value, 120) : "";
    }
}
