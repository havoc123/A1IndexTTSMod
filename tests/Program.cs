using A1IndexTTSMod;

var table = Path.Combine(BepInEx.Paths.GameRootPath, "A1IndexTTSMod", "references", "npcs", "npc_id_name.csv");
var rows = File.ReadLines(table).Skip(1)
    .Select(line => line.Split(','))
    .Where(parts => parts.Length == 4)
    .ToArray();

string Find(string audio, string gender) => rows.First(parts => parts[2] == audio && parts[3] == gender)[0];

void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var maleId = Find("0", "1");
var male = NpcVoiceResolver.Resolve("npc:" + maleId);
Assert(!male.Skip && male.Label == "default_male.wav" && File.Exists(male.Path), "male audio=0 fallback failed");

var femaleId = Find("0", "2");
var female = NpcVoiceResolver.Resolve("npc:" + femaleId);
Assert(!female.Skip && female.Label == "default_female.wav" && File.Exists(female.Path), "female audio=0 fallback failed");

var unknownId = Find("0", "3");
var unknown = NpcVoiceResolver.Resolve("npc:" + unknownId);
Assert(unknown.Skip && unknown.Path == null, "unknown audio=0 should be silent");

var dedicated = NpcVoiceResolver.Resolve("npc:100000");
Assert(!dedicated.Skip && dedicated.Label == "100000.wav" && File.Exists(dedicated.Path), "dedicated NPC WAV failed");

Console.WriteLine($"PASS male={maleId} female={femaleId} unknown={unknownId} dedicated=100000");

namespace BepInEx
{
    public static class Paths
    {
        public static string GameRootPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
