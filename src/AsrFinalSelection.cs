namespace A1IndexTTSMod;

/// <summary>Selects complete decoder hypotheses; never substitutes spelling into text.</summary>
internal static class AsrFinalSelection
{
    internal static bool KeepStreaming(string streaming, string reviewed, IEnumerable<string> hotwords)
    {
        if (streaming == reviewed) return false;
        // A wider search can discard a correctly completed contextual word. Keep
        // the first hypothesis if the second loses any such occurrence. This
        // conservative choice cannot determine whether either spelling is true.
        return hotwords.Any(word => word.Length > 0 && Count(streaming, word) > Count(reviewed, word));
    }

    private static int Count(string text, string word)
    {
        var count = 0;
        for (var offset = 0; offset <= text.Length - word.Length;)
        {
            var index = text.IndexOf(word, offset, StringComparison.Ordinal);
            if (index < 0) break;
            count++; offset = index + word.Length;
        }
        return count;
    }
}
