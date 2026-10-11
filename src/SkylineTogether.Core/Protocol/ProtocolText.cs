using System.Text;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Free-text rules of the wire format (ARCH §14.3: "strings are stripped of control characters before they reach
    /// the UI"; chat ≤ 500 and names ≤ 64 characters). Writers truncate free text to its limit, so user input can never
    /// make an encoder throw; readers reject text above the limit (only a broken or hostile peer sends it) and strip
    /// control characters. Identifiers (mod names, prefab ids, setting keys) are not free text: they are compared
    /// exactly and returned verbatim, so the UI escapes them for display.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ProtocolText
    {
        /// <summary>UTF-8 bytes per UTF-16 code unit at most (a surrogate pair is 2 units and 4 bytes).</summary>
        public const int MaxUtf8BytesPerChar = 3;

        /// <summary>
        /// Removes characters that must never reach the UI: C0 and C1 control characters (including CR, LF and TAB),
        /// the bidirectional embedding, override and isolate controls (U+202A-U+202E, U+2066-U+2069; they let a name
        /// spoof the text around it) and the line and paragraph separators (U+2028, U+2029). Returns the input itself
        /// when nothing is removed; null becomes the empty string.
        /// </summary>
        public static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int i = 0;
            while (i < text.Length && !IsStripped(text[i])) i++;
            if (i == text.Length) return text;
            var sb = new StringBuilder(text.Length);
            sb.Append(text, 0, i);
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (!IsStripped(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Shortens <paramref name="text"/> to at most <paramref name="maxChars"/> UTF-16 code units without splitting a
        /// surrogate pair. Null becomes the empty string.
        /// </summary>
        public static string Truncate(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars <= 0) return string.Empty;
            if (text.Length <= maxChars) return text;
            int cut = maxChars;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut);
        }

        /// <summary>True for a character <see cref="Sanitize"/> removes.</summary>
        public static bool IsStripped(char c)
        {
            int code = c;
            return char.IsControl(c)
                   || (code >= 0x202A && code <= 0x202E)      // LRE, RLE, PDF, LRO, RLO
                   || (code >= 0x2066 && code <= 0x2069)      // LRI, RLI, FSI, PDI
                   || code == 0x2028 || code == 0x2029;       // line and paragraph separator
        }
    }
}
