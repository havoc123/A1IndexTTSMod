using BepInEx;
using BepInEx.Logging;
using NAudio.Wave;
using System.Security.Cryptography;
using System.Text.Json;

namespace A1IndexTTSMod;

internal static class NpcVoiceResolver
{
    private static readonly string DirectoryPath = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", "references", "npcs");
    private static readonly string ReferencePath = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", "references");
    private static readonly string TablePath = Path.Combine(DirectoryPath, "npc_id_name.csv");
    private static readonly string ImportedPath = Path.Combine(ReferencePath, "imported");
    private static readonly string OverridesPath = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", "config", "npc-voice-overrides.json");
    private static readonly Dictionary<string, string> SavedOverrides = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> SessionOverrides = new(StringComparer.Ordinal);
    private static ReferenceChoice[]? _available;
    private static Dictionary<string, string>? _npcNames;
    private static readonly object Gate = new();

    internal readonly record struct Selection(string? Path, string Label, bool Skip);
    internal readonly record struct ReferenceChoice(string Path, string Label);

    internal static string GetNpcName(string? npcKey)
    {
        var id = (npcKey ?? "").Replace("npc:", "");
        lock (Gate)
        {
            if (_npcNames == null)
            {
                _npcNames = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    if (File.Exists(TablePath))
                        foreach (var line in File.ReadLines(TablePath).Skip(1))
                        {
                            var first = line.IndexOf(',');
                            var last = line.LastIndexOf(',');
                            var audio = last > 0 ? line.LastIndexOf(',', last - 1) : -1;
                            if (first <= 0 || audio <= first) continue;
                            _npcNames[line[..first].Trim()] = line[(first + 1)..audio].Trim().Trim('"').Replace("\"\"", "\"");
                        }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return _npcNames.GetValueOrDefault(id) ?? "未命名角色";
        }
    }

    internal static string GetReferenceLabel(string path)
    {
        var relative = Path.GetRelativePath(ReferencePath, path).Replace('\\', '/');
        if (string.Equals(Path.GetDirectoryName(path), DirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            var name = GetNpcName(Path.GetFileNameWithoutExtension(path));
            if (name != "未命名角色") return name + " · " + relative;
        }
        return relative;
    }

    internal static void Initialize(ManualLogSource log)
    {
        try
        {
            Directory.CreateDirectory(ImportedPath);
            if (!File.Exists(OverridesPath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(OverridesPath));
            if (loaded == null) return;
            lock (Gate)
                foreach (var (id, relative) in loaded)
                {
                    if (id.Length == 0 || id.Any(c => c is < '0' or > '9') || !TryResolveManagedPath(relative, out var path) || !File.Exists(path)) continue;
                    SavedOverrides[id] = path;
                }
        }
        catch (Exception e) { log.LogWarning("NPC voice settings could not be loaded: " + e.GetType().Name); }
    }

    internal static ReferenceChoice[] GetAvailableReferences()
    {
        lock (Gate)
        {
            if (_available != null) return _available.ToArray();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { ReferencePath, DirectoryPath, ImportedPath })
                if (Directory.Exists(folder))
                    foreach (var file in Directory.EnumerateFiles(folder, "*.wav", SearchOption.TopDirectoryOnly)) paths.Add(Path.GetFullPath(file));
            _available = paths.Select(path => new ReferenceChoice(path, GetReferenceLabel(path)))
                .OrderBy(choice => choice.Label.Contains("/imported/", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            return _available.ToArray();
        }
    }

    internal static string? GetOverride(string npcKey)
    {
        if (!TryNpcNumber(npcKey, out var id)) return null;
        lock (Gate) return SessionOverrides.TryGetValue(id, out var temp) ? temp : SavedOverrides.GetValueOrDefault(id);
    }

    internal static void ApplyOverride(string npcKey, string path, bool persist)
    {
        if (!TryNpcNumber(npcKey, out var id)) throw new InvalidOperationException("当前 NPC 身份不可用。");
        if (!TryResolveManagedPath(path, out var managed) || !File.Exists(managed)) throw new FileNotFoundException("参考音必须在 Mod 的 references 目录中。");
        var validation = ValidateWav(managed);
        if (validation != null) throw new InvalidDataException(validation);
        lock (Gate)
        {
            if (persist)
            {
                SavedOverrides[id] = managed;
                SessionOverrides.Remove(id);
                SaveOverrides();
            }
            else SessionOverrides[id] = managed;
        }
    }

    internal static void ResetOverride(string npcKey)
    {
        if (!TryNpcNumber(npcKey, out var id)) return;
        lock (Gate)
        {
            SessionOverrides.Remove(id);
            if (SavedOverrides.Remove(id)) SaveOverrides();
        }
    }

    internal static string ImportWav(string source)
    {
        var validation = ValidateWav(source);
        if (validation != null) throw new InvalidDataException(validation);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        Directory.CreateDirectory(ImportedPath);
        var target = Path.Combine(ImportedPath, hash + ".wav");
        if (!File.Exists(target)) File.Copy(source, target);
        lock (Gate) _available = null;
        return target;
    }

    internal static string? ValidateWav(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 44) return "文件太小或不存在，不是有效的 WAV 音频。";
            if (info.Length > 5L * 1024 * 1024) return "文件超过本地 audio.cpp 参考音 5 MiB 限制。";
            using var reader = new WaveFileReader(path);
            var format = reader.WaveFormat;
            if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)) return "只接受未压缩 PCM 或 IEEE Float WAV。";
            if (format.Channels is < 1 or > 2) return "声道数必须为 1 或 2。";
            if (format.SampleRate is < 16000 or > 96000) return "采样率需要在 16–96 kHz。";
            if (format.BitsPerSample is not (16 or 24 or 32)) return "位深需要为 16、24 或 32 bit。";
            if (reader.Length <= 0 || format.BlockAlign <= 0 || reader.TotalTime < TimeSpan.FromSeconds(1) || reader.TotalTime > TimeSpan.FromSeconds(30))
                return "音频时长需要为 1–30 秒且包含有效样本。";
            var buffer = new byte[64 * 1024];
            long sampleBytes = 0;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) sampleBytes += read;
            if (sampleBytes <= 0 || sampleBytes % format.BlockAlign != 0) return "WAV 样本数据不完整或未按完整音频帧对齐。";
            return null;
        }
        catch (Exception e) { return "无法解析 WAV 文件头或样本数据：" + e.GetType().Name; }
    }

    // NpcCfgId.Value is the key. CSV audio=0 selects a gender default or silence.
    public static Selection Resolve(string npcKey)
    {
        var managedOverride = GetOverride(npcKey);
        if (managedOverride != null)
            return File.Exists(managedOverride)
                ? new(managedOverride, Path.GetRelativePath(ReferencePath, managedOverride).Replace('\\', '/'), false)
                : new(null, "missing_override_fallback", false);
        if (!npcKey.StartsWith("npc:", StringComparison.Ordinal)) return new(null, "demo.wav", false);
        var id = npcKey[4..];
        if (id.Length == 0 || id.Any(c => c is < '0' or > '9')) return new(null, "demo.wav", false);

        try
        {
            if (TryReadAudioAndGender(id, out var audio, out var gender) && audio == "0")
            {
                if (gender == "3") return new(null, "unknown_gender", true);
                var filename = gender switch
                {
                    "1" => "default_male.wav",
                    "2" => "default_female.wav",
                    _ => null
                };
                if (filename == null) return new(null, "unrecognized_gender", true);
                var fallback = Path.Combine(ReferencePath, filename);
                return File.Exists(fallback)
                    ? new(fallback, filename, false)
                    : new(null, "missing_" + filename, true);
            }

            var path = Path.Combine(DirectoryPath, id + ".wav");
            return File.Exists(path)
                ? new(path, id + ".wav", false)
                : new(null, "demo.wav", false);
        }
        catch (IOException) { return new(null, "demo.wav", false); }
        catch (UnauthorizedAccessException) { return new(null, "demo.wav", false); }
    }

    private static bool TryNpcNumber(string npcKey, out string id)
    {
        id = "";
        if (!npcKey.StartsWith("npc:", StringComparison.Ordinal)) return false;
        id = npcKey[4..];
        return id.Length > 0 && id.All(c => c is >= '0' and <= '9');
    }

    private static bool TryResolveManagedPath(string? candidate, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var root = Path.GetFullPath(ReferencePath) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(ReferencePath, candidate));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = full;
            return true;
        }
        catch { return false; }
    }

    private static void SaveOverrides()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OverridesPath)!);
        var values = SavedOverrides.ToDictionary(pair => pair.Key,
            pair => Path.GetRelativePath(ReferencePath, pair.Value).Replace('\\', '/'), StringComparer.Ordinal);
        var temp = OverridesPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, OverridesPath, true);
    }

    private static bool TryReadAudioAndGender(string id, out string audio, out string gender)
    {
        audio = "";
        gender = "";
        if (!File.Exists(TablePath)) return false;
        var prefix = id + ",";
        foreach (var line in File.ReadLines(TablePath))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var genderSeparator = line.LastIndexOf(',');
            if (genderSeparator < prefix.Length) return false;
            var audioSeparator = line.LastIndexOf(',', genderSeparator - 1);
            if (audioSeparator < prefix.Length) return false;
            audio = line[(audioSeparator + 1)..genderSeparator].Trim();
            gender = line[(genderSeparator + 1)..].Trim();
            return true;
        }
        return false;
    }
}
