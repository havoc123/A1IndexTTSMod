using BepInEx;

namespace A1IndexTTSMod;

internal static class NpcVoiceResolver
{
    private static readonly string DirectoryPath = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", "references", "npcs");
    private static readonly string ReferencePath = Path.Combine(Paths.GameRootPath, "A1IndexTTSMod", "references");
    private static readonly string TablePath = Path.Combine(DirectoryPath, "npc_id_name.csv");

    internal readonly record struct Selection(string? Path, string Label, bool Skip);

    // NpcCfgId.Value is the key. CSV audio=0 selects a gender default or silence.
    public static Selection Resolve(string npcKey)
    {
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
