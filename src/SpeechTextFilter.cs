using System.Text;
using System.Text.RegularExpressions;

namespace A1IndexTTSMod;

internal static class SpeechTextFilter
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string RemoveParentheticals(string text)
    {
        var result = new StringBuilder(text.Length);
        var openAt = -1;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '(' or '（')
            {
                if (depth++ == 0) openAt = i;
            }
            else if ((ch is ')' or '）') && depth > 0)
            {
                if (--depth == 0) openAt = -1;
            }
            else if (depth == 0) result.Append(ch);
        }
        // Preserve malformed, unclosed text rather than discarding the rest of the dialogue.
        if (depth > 0 && openAt >= 0) result.Append(text, openAt, text.Length - openAt);
        return Whitespace.Replace(result.ToString(), " ").Trim();
    }
}
