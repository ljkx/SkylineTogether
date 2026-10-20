using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Display names in a session: the host makes every joiner's name unique and never equal to its own, so no one can
    /// pose as the host or another player in the chat or the player list (names are not identities, but they are what
    /// players read). Invisible and direction-changing characters are removed, and names that differ only in case,
    /// width or spacing count as equal. Pure.
    /// </summary>
    public static class PlayerNames
    {
        /// <summary>The name used when a joiner sends none (or nothing visible).</summary>
        public const string Fallback = "Player";

        /// <summary>
        /// <paramref name="wanted"/>, cleaned, or with " (2)", " (3)", ... appended when another name in
        /// <paramref name="taken"/> (the host's and the other players') reads the same.
        /// </summary>
        public static string Unique(string wanted, IEnumerable<string> taken)
        {
            string name = Clean(wanted);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (taken != null)
            {
                foreach (string t in taken) keys.Add(Key(t));
            }
            if (!keys.Contains(Key(name))) return name;
            string stem = name.Length > Limits.MaxNameChars - 5 ? name.Substring(0, Limits.MaxNameChars - 5).TrimEnd() : name;
            for (int n = 2; n < 1000; n++)
            {
                string candidate = stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
                if (!keys.Contains(Key(candidate))) return candidate;
            }
            return stem + " (" + Guid.NewGuid().ToString("N").Substring(0, 4) + ")";
        }

        /// <summary>The name without control, invisible or direction-changing characters, spaces collapsed, at most 64 characters.</summary>
        public static string Clean(string name)
        {
            string s = ProtocolText.Sanitize(name ?? "");
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.Format || cat == UnicodeCategory.Control || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator) continue;
                if (char.IsWhiteSpace(c))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            string clean = ProtocolText.Truncate(sb.ToString(), Limits.MaxNameChars).Trim();
            return clean.Length == 0 ? Fallback : clean;
        }

        /// <summary>The comparison key: compatibility-normalized, lower case, without spaces.</summary>
        public static string Key(string name)
        {
            string s = Clean(name);
            try
            {
                s = s.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                // an invalid surrogate sequence: compare as it is
            }
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                // full-width forms read like ASCII (also where the runtime cannot normalize)
                char k = c >= (char)0xFF01 && c <= (char)0xFF5E ? (char)(c - 0xFEE0) : c;
                sb.Append(char.ToLowerInvariant(k));
            }
            return sb.ToString();
        }
    }
}
