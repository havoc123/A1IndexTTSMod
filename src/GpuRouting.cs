using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace A1IndexTTSMod;

internal sealed record GpuAdapter(int Index, uint Vendor, string Name, ulong VideoMemory, bool SupportsDirectML = true);
internal sealed record GpuRoute(string Provider, int Device, string Name);

internal static class GpuRouting
{
    internal static readonly Lazy<GpuAdapter[]> Adapters = new(EnumerateAdapters);
    internal static GpuRoute? SelectAsr(IEnumerable<GpuAdapter> adapters)
    {
        var gpu = adapters.FirstOrDefault(g => g.Vendor == 0x10de)
            ?? adapters.FirstOrDefault(g => g.Vendor == 0x1002 && g.SupportsDirectML)
            ?? adapters.FirstOrDefault(g => g.Vendor == 0x8086 && g.SupportsDirectML);
        return gpu == null ? null : new(gpu.Vendor == 0x10de ? "cuda" : "directml", gpu.Vendor == 0x10de ? 0 : gpu.Index, gpu.Name);
    }
    internal static bool AllowsModel(string provider, AsrModelProfile model) => provider == "cuda" || model.Id == AsrModelProfiles.Lightweight.Id;
    internal static string RuntimeDirectory(string root, string provider) => Path.Combine(root, "A1IndexTTSMod", "asr", provider == "directml" ? "runtime-directml" : "runtime");
    internal static bool RuntimeInstalled(string root, string provider)
    {
        var directory = RuntimeDirectory(root, provider);
        var files = provider == "directml"
            ? new[] { "sherpa-onnx-c-api.dll", "onnxruntime.dll", "DirectML.dll", "a1-native-manifest.json" }
            : new[] { "sherpa-onnx-c-api.dll", "onnxruntime.dll", "onnxruntime_providers_cuda.dll", "onnxruntime_providers_shared.dll", "a1-native-manifest.json" };
        if (!files.All(name => File.Exists(Path.Combine(directory, name)) && new FileInfo(Path.Combine(directory, name)).Length > 0)) return false;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "a1-native-manifest.json")).TrimStart('\ufeff'), new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true });
            if (manifest.RootElement.GetProperty("revision").GetString() != "a1-context-before-topk-finalize-v3" ||
                !manifest.RootElement.GetProperty("provider").GetString()!.Equals(provider, StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { return false; }
        if (provider != "cuda") return true;
        var paths = new[] { directory, root }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return new[] { "cudnn64_9.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll" }
            .All(name => paths.Any(path => path.Length > 0 && File.Exists(Path.Combine(path, name))));
    }

    internal static GpuRoute ParseTtsDevices(string output, string provider, GpuAdapter? preferred = null)
    {
        var devices = Regex.Matches(output, @"(?m)^\s*(cuda|vulkan):(\d+)\s+""([^""]+)""\s+\[(?:GPU|IGPU)\]\s*$", RegexOptions.IgnoreCase)
            .Cast<Match>().Where(m => m.Groups[1].Value.Equals(provider, StringComparison.OrdinalIgnoreCase))
            .Select(m => new GpuRoute(provider, int.Parse(m.Groups[2].Value), m.Groups[3].Value)).ToArray();
        var selected = preferred == null ? devices.FirstOrDefault() : devices.FirstOrDefault(d => d.Name.Equals(preferred.Name, StringComparison.OrdinalIgnoreCase));
        // Different APIs occasionally spell the same adapter differently. Match
        // the vendor only when it identifies exactly one backend device.
        if (selected == null && preferred != null)
        {
            var vendor = preferred.Vendor == 0x1002 ? @"AMD|Radeon" : preferred.Vendor == 0x10de ? @"NVIDIA|GeForce|Quadro" : @"Intel";
            var matches = devices.Where(d => Regex.IsMatch(d.Name, vendor, RegexOptions.IgnoreCase)).ToArray();
            if (matches.Length == 1) selected = matches[0];
        }
        return selected ?? throw new InvalidOperationException("无法从 audio.cpp 设备列表确定目标显卡，请明确配置 GpuBackend 和 GpuDevice。");
    }

    internal static async Task<GpuRoute> ResolveTtsAsync(string project, CancellationToken token)
    {
        var gpu = Adapters.Value.FirstOrDefault(g => g.Vendor == 0x10de) ?? Adapters.Value.FirstOrDefault(g => g.Vendor == 0x1002);
        if (gpu == null) throw new NotSupportedException("TTS 自动选卡未发现 NVIDIA 或 AMD 显卡。");
        var provider = gpu.Vendor == 0x10de ? "cuda" : "vulkan";
        var runtime = Path.Combine(project, ".cache", "audiocpp", "runtime");
        var executable = Path.Combine(runtime, provider == "cuda" ? "audiocpp_server.exe" : "audiocpp_server-vulkan.exe");
        using var process = new Process { StartInfo = new(executable, "--list-devices") { WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("audio.cpp 显卡枚举失败：" + error);
        return ParseTtsDevices(output, provider, gpu);
    }

    [DllImport("d3d12.dll")] private static extern int D3D12CreateDevice(IntPtr adapter, uint minimumFeatureLevel, ref Guid iid, IntPtr device);
    private static bool SupportsDirectML(IntPtr adapter)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return false;
        var iid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");
        try { return D3D12CreateDevice(adapter, 0xb000, ref iid, IntPtr.Zero) >= 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr adapter, out AdapterDescription description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Release(IntPtr instance);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Vendor, Device, Subsystem, Revision;
        public UIntPtr VideoMemory, SystemMemory, SharedMemory;
        public uint LuidLow; public int LuidHigh; public uint Flags;
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static GpuAdapter[] EnumerateAdapters()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        var result = new List<GpuAdapter>();
        try
        {
            var enumerate = Method<EnumAdapters>(factory, 12);
            for (uint index = 0; ; index++)
            {
                var hr = enumerate(factory, index, out var adapter);
                if (hr == unchecked((int)0x887a0002)) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetDescription>(adapter, 10)(adapter, out var description));
                    if ((description.Flags & 2) == 0) result.Add(new((int)index, description.Vendor, description.Description.Trim(), description.VideoMemory.ToUInt64(), SupportsDirectML(adapter)));
                }
                finally { Method<Release>(adapter, 2)(adapter); }
            }
        }
        finally { Method<Release>(factory, 2)(factory); }
        return result.ToArray();
    }
}
