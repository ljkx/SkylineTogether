using System;
using System.Collections.Generic;
using System.Text;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// Keeps the Windows user name out of what the mod writes (its log, the diagnostics file, notifications;
    /// ARCHITECTURE.md#logs). A path inside the user's profile folder is written with the variable instead
    /// (<c>C:\Users\you\AppData\LocalLow\…</c> becomes <c>%USERPROFILE%\AppData\LocalLow\…</c>, which Explorer's address
    /// bar still opens), and any other path segment that equals the user name becomes <c>%USERNAME%</c>. Both separators
    /// count (Unity writes <c>/</c>), letters are compared case-insensitively, and a path in a <c>file://</c> URL counts
    /// with its characters percent-encoded (<c>Alexandra%20Smith</c>). A root whose last folder has an 8.3 short name
    /// (<c>ALEXAN~1</c> for "Alexandra Smith", which Windows puts into TEMP and into exception messages) is matched in that form
    /// too: by the pattern Windows derives the short name with (<c>ALEXAN~&lt;n&gt;</c>), and exactly once the game layer
    /// resolved it (<see cref="AddShortForms"/>). Text without the user name is returned unchanged (the same instance),
    /// so the check is cheap for ordinary log lines. Immutable; thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PathRedactor
    {
        public const string UserProfileToken = "%USERPROFILE%";
        public const string UserNameToken = "%USERNAME%";

        private static PathRedactor _current;

        // longest first, so a folder outside the profile (redirected AppData) wins over a shorter root
        private readonly string[] _roots;
        private readonly string[] _rootTokens;
        // path segments replaced wherever they appear (the user name and the profile folder's own name)
        private readonly string[] _names;
        // the quick check: a text that contains none of these (the names, every root's last folder) has nothing to replace
        private readonly string[] _probes;
        // the 8.3 short names Windows derives from the roots' last folders (ALEXAN~1 for "Alexandra Smith")
        private readonly ShortForm[] _shortForms;
        // what the redactor was made of (AddShortForms adds roots to it)
        private readonly List<KeyValuePair<string, string>> _given = new List<KeyValuePair<string, string>>();
        private readonly string _userName;

        private sealed class ShortForm
        {
            public string Parent;    // the root without its last folder (C:\Users)
            public string Base;      // the short name before "~" (ALEXAN)
            public string Extension; // the short extension ("", or SMI for "anna.smith")
            public string Token;
        }

        /// <summary>
        /// <paramref name="roots"/>: folder → replacement (for example the profile folder → <see cref="UserProfileToken"/>);
        /// <paramref name="userName"/>: replaced as a whole path segment anywhere. Roots shorter than a folder below a
        /// drive (<c>C:\x</c>) and names shorter than two characters are ignored, so nothing redacts every path.
        /// </summary>
        public PathRedactor(IEnumerable<KeyValuePair<string, string>> roots, string userName)
        {
            var list = new List<KeyValuePair<string, string>>();
            var names = new List<string>();
            var probes = new List<string>();
            var shortForms = new List<ShortForm>();
            _userName = userName;
            if (roots != null)
            {
                foreach (KeyValuePair<string, string> r in roots)
                {
                    string root = TrimSeparators(r.Key);
                    if (!IsUsableRoot(root) || string.IsNullOrEmpty(r.Value)) continue;
                    if (list.Exists(x => string.Equals(Normalize(x.Key), Normalize(root), StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new KeyValuePair<string, string>(root, r.Value));
                    _given.Add(new KeyValuePair<string, string>(root, r.Value));
                    // the profile folder's own name may differ from the user name (name_000, name.DOMAIN)
                    if (r.Value == UserProfileToken) AddName(names, LastSegment(root));
                    AddName(probes, LastSegment(root), 1);
                    string parent = TrimSeparators(root.Substring(0, root.Length - LastSegment(root).Length));
                    if (IsUsableRoot(parent) && ShortNameOf(LastSegment(root), out string shortBase, out string shortExtension))
                    {
                        shortForms.Add(new ShortForm { Parent = parent, Base = shortBase, Extension = shortExtension, Token = r.Value });
                        AddName(probes, shortBase + "~", 1);
                    }
                }
            }
            AddName(names, userName);
            list.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            _roots = new string[list.Count];
            _rootTokens = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                _roots[i] = list[i].Key;
                _rootTokens[i] = list[i].Value;
            }
            _names = names.ToArray();
            _shortForms = shortForms.ToArray();
            foreach (string n in names) AddName(probes, n, 1);
            // the same names percent-encoded, as a file:// URL writes them
            foreach (string probe in probes.ToArray()) AddName(probes, Uri.EscapeDataString(probe), 1);
            _probes = _roots.Length + _names.Length == 0 ? new string[0] : probes.ToArray();
        }

        /// <summary>
        /// Adds the exact short forms of the roots of <see cref="Current"/>, as <paramref name="shortPath"/> resolves them
        /// (the game layer passes Windows' GetShortPathName; null, an exception or the long form: none). Windows can name
        /// the profile folder in 8.3 form in TEMP and in the paths of exception messages, also as a hashed name
        /// (<c>LE1A2B~1</c>) that the pattern of <see cref="ShortNameOf"/> does not predict.
        /// </summary>
        public static void AddShortForms(Func<string, string> shortPath)
        {
            if (shortPath == null) return;
            PathRedactor current = Current;
            var roots = new List<KeyValuePair<string, string>>(current._given);
            foreach (KeyValuePair<string, string> r in current._given)
            {
                string s;
                try
                {
                    s = TrimSeparators(shortPath(r.Key));
                }
                catch (Exception)
                {
                    continue; // no short form
                }
                if (s.Length > 0 && !string.Equals(Normalize(s), Normalize(r.Key), StringComparison.OrdinalIgnoreCase)) roots.Add(new KeyValuePair<string, string>(s, r.Value));
            }
            if (roots.Count > current._given.Count) _current = new PathRedactor(roots, current._userName);
        }

        /// <summary>The redactor for this PC: the profile folder, AppData folders outside it, and the user name.</summary>
        public static PathRedactor Current => _current ?? (_current = FromEnvironment());

        /// <summary><see cref="Apply"/> with <see cref="Current"/>.</summary>
        public static string Redact(string text) => Current.Apply(text);

        /// <summary>True when nothing would ever be replaced (no profile folder and no user name known).</summary>
        public bool IsEmpty => _roots.Length == 0 && _names.Length == 0;

        /// <summary>The text with every path inside the profile, and every path segment equal to the user name, replaced.</summary>
        public string Apply(string text)
        {
            if (string.IsNullOrEmpty(text) || IsEmpty || !MentionsProbe(text)) return text;
            StringBuilder sb = null;
            int copied = 0;
            int i = 0;
            while (i < text.Length)
            {
                int length = 0;
                string token = null;
                if (i == 0 || !IsNameChar(text[i - 1]))
                {
                    for (int r = 0; r < _roots.Length; r++)
                    {
                        int matched = MatchLength(text, i, _roots[r]);
                        if (matched > 0 && EndsSegment(text, i + matched))
                        {
                            length = matched;
                            token = _rootTokens[r];
                            break;
                        }
                    }
                    for (int r = 0; length == 0 && r < _shortForms.Length; r++)
                    {
                        length = MatchShortForm(text, i, _shortForms[r]);
                        if (length > 0) token = _shortForms[r].Token;
                    }
                }
                if (length == 0 && i > 0 && IsSeparator(text[i - 1]))
                {
                    foreach (string name in _names)
                    {
                        int matched = MatchLength(text, i, name);
                        if (matched > 0 && EndsSegment(text, i + matched))
                        {
                            length = matched;
                            token = UserNameToken;
                            break;
                        }
                    }
                }
                if (length == 0)
                {
                    i++;
                    continue;
                }
                if (sb == null) sb = new StringBuilder(text.Length + 16);
                sb.Append(text, copied, i - copied).Append(token);
                i += length;
                copied = i;
            }
            if (sb == null) return text;
            sb.Append(text, copied, text.Length - copied);
            return sb.ToString();
        }

        /// <summary>
        /// The 8.3 short form of <paramref name="profile"/> (<c>USERNA~1</c> for <c>username</c>) when <paramref name="tempPath"/> uses
        /// it, else null. Windows can set TEMP to the short form of the profile, and a path built from it would carry
        /// the user name past the long root.
        /// </summary>
        public static string ShortProfileFrom(string profile, string tempPath)
        {
            if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(tempPath) || tempPath.IndexOf('~') < 0) return null;
            char[] separators = { '\\', '/' };
            string[] p = TrimSeparators(profile).Split(separators);
            string[] t = tempPath.Split(separators);
            if (p.Length < 2 || t.Length <= p.Length) return null;
            for (int i = 0; i < p.Length - 1; i++)
            {
                if (!string.Equals(p[i], t[i], StringComparison.OrdinalIgnoreCase)) return null;
            }
            string last = t[p.Length - 1];
            if (last.IndexOf('~') <= 0 || string.Equals(last, p[p.Length - 1], StringComparison.OrdinalIgnoreCase)) return null;
            return string.Join(tempPath.IndexOf('\\') >= 0 ? "\\" : "/", t, 0, p.Length);
        }

        private static PathRedactor FromEnvironment()
        {
            try
            {
                string profile = Folder(Environment.SpecialFolder.UserProfile, "USERPROFILE");
                var roots = new List<KeyValuePair<string, string>>();
                if (!string.IsNullOrEmpty(profile)) roots.Add(new KeyValuePair<string, string>(profile, UserProfileToken));
                // the temp folder can name the profile in its 8.3 short form (USERNA~1 for a profile folder "username")
                string tempPath = null;
                try
                {
                    tempPath = System.IO.Path.GetTempPath();
                }
                catch (Exception)
                {
                    // no temp folder
                }
                string shortProfile = ShortProfileFrom(profile, tempPath);
                if (shortProfile != null) roots.Add(new KeyValuePair<string, string>(shortProfile, UserProfileToken));
                // AppData can be redirected outside the profile (roaming profiles); inside it, the profile root covers it
                AddOutside(roots, profile, Folder(Environment.SpecialFolder.LocalApplicationData, "LOCALAPPDATA"), "%LOCALAPPDATA%");
                AddOutside(roots, profile, Folder(Environment.SpecialFolder.ApplicationData, "APPDATA"), "%APPDATA%");
                string user = null;
                try
                {
                    user = Environment.UserName;
                }
                catch (Exception)
                {
                    // not available on this platform
                }
                return new PathRedactor(roots, user);
            }
            catch (Exception)
            {
                return new PathRedactor(null, null);
            }
        }

        private static string Folder(Environment.SpecialFolder folder, string variable)
        {
            string path = null;
            try
            {
                path = Environment.GetFolderPath(folder);
            }
            catch (Exception)
            {
                // not available on this platform
            }
            if (string.IsNullOrEmpty(path))
            {
                try
                {
                    path = Environment.GetEnvironmentVariable(variable);
                }
                catch (Exception)
                {
                    path = null;
                }
            }
            return path;
        }

        private static void AddOutside(List<KeyValuePair<string, string>> roots, string profile, string folder, string token)
        {
            if (string.IsNullOrEmpty(folder)) return;
            if (!string.IsNullOrEmpty(profile) && Normalize(folder).StartsWith(Normalize(TrimSeparators(profile)) + "/", StringComparison.OrdinalIgnoreCase)) return;
            roots.Add(new KeyValuePair<string, string>(folder, token));
        }

        private bool MentionsProbe(string text)
        {
            foreach (string n in _probes)
            {
                if (text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static void AddName(List<string> names, string name, int minLength = 2)
        {
            name = (name ?? "").Trim();
            if (name.Length < minLength || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return;
            if (names.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return;
            names.Add(name);
        }

        // a root names a folder below a drive or share at least ("C:\Users" is fine, "C:\" or "\" is not)
        private static bool IsUsableRoot(string root)
        {
            if (string.IsNullOrEmpty(root) || root.Length < 4) return false;
            string n = Normalize(root).TrimStart('/');
            int separator = n.IndexOf('/');
            return separator > 0 && separator < n.Length - 1;
        }

        private static string TrimSeparators(string path) => (path ?? "").Trim().TrimEnd('\\', '/');

        private static string Normalize(string path) => (path ?? "").Replace('\\', '/');

        private static string LastSegment(string path)
        {
            string n = Normalize(path);
            int last = n.LastIndexOf('/');
            return last >= 0 ? n.Substring(last + 1) : n;
        }

        // the length of the text at start that spells pattern (separators interchangeable, letters in any case, a
        // character other than an ASCII letter or digit also percent-encoded as in a file:// URL), or -1
        private static int MatchLength(string text, int start, string pattern)
        {
            int t = start;
            for (int k = 0; k < pattern.Length; k++)
            {
                if (t >= text.Length) return -1;
                char a = text[t];
                char b = pattern[k];
                if (a == '%' && !IsAsciiLetterOrDigit(b))
                {
                    int encoded = MatchPercentEncoded(text, t, b);
                    if (encoded > 0)
                    {
                        t += encoded;
                        continue;
                    }
                }
                if (!(IsSeparator(a) && IsSeparator(b)) && a != b && char.ToUpperInvariant(a) != char.ToUpperInvariant(b)) return -1;
                t++;
            }
            return t - start;
        }

        // the length of c's UTF-8 bytes written as %XX at text[start], or 0
        private static int MatchPercentEncoded(string text, int start, char c)
        {
            if (char.IsSurrogate(c)) return 0;
            byte[] bytes = Encoding.UTF8.GetBytes(new[] { c });
            if (start + bytes.Length * 3 > text.Length) return 0;
            for (int b = 0; b < bytes.Length; b++)
            {
                int at = start + b * 3;
                if (text[at] != '%' || HexValue(text[at + 1]) != bytes[b] >> 4 || HexValue(text[at + 2]) != (bytes[b] & 15)) return 0;
            }
            return bytes.Length * 3;
        }

        private static int HexValue(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

        private static bool IsAsciiLetterOrDigit(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        // "<parent><separator><BASE>~<n>[.<EXT>]" at start, ending a segment: its length, or 0
        private static int MatchShortForm(string text, int start, ShortForm form)
        {
            int matched = MatchLength(text, start, form.Parent);
            if (matched <= 0) return 0;
            int t = start + matched;
            if (t >= text.Length || !IsSeparator(text[t])) return 0;
            matched = MatchLength(text, ++t, form.Base);
            if (matched <= 0) return 0;
            t += matched;
            if (t >= text.Length || text[t] != '~') return 0;
            int digitsAt = ++t;
            while (t < text.Length && t - digitsAt < 6 && text[t] >= '0' && text[t] <= '9') t++;
            if (t == digitsAt || text[digitsAt] == '0') return 0;
            if (form.Extension.Length > 0)
            {
                if (t >= text.Length || text[t] != '.') return 0;
                matched = MatchLength(text, t + 1, form.Extension);
                if (matched <= 0) return 0;
                t += 1 + matched;
            }
            return EndsSegment(text, t) ? t - start : 0;
        }

        /// <summary>
        /// The 8.3 short name Windows generates first for a folder named <paramref name="name"/>, as <c>BASE~n.EXT</c>
        /// ("Alexandra Smith": ALEXAN~n; "anna.smith": ANNA~n.SMI); false when the name needs none (it is a valid 8.3 name
        /// already) or its short name cannot be predicted (characters outside printable ASCII).
        /// </summary>
        internal static bool ShortNameOf(string name, out string shortBase, out string shortExtension)
        {
            shortBase = shortExtension = null;
            name = (name ?? "").Trim();
            if (name.Length == 0) return false;
            foreach (char c in name)
            {
                if (c > '~' || c < ' ') return false;
            }
            int dot = name.LastIndexOf('.');
            string stem = dot > 0 ? name.Substring(0, dot) : name;
            string extension = dot > 0 ? name.Substring(dot + 1) : "";
            bool valid83 = stem.Length <= 8 && extension.Length <= 3 && stem.IndexOf('.') < 0 && name.IndexOfAny(Not83) < 0;
            if (valid83) return false;
            shortBase = ShortPart(stem, 6);
            shortExtension = ShortPart(extension, 3);
            return shortBase.Length > 0;
        }

        private static readonly char[] Not83 = { ' ', '+', ',', ';', '=', '[', ']' };

        // spaces and dots left out, + , ; = [ ] as _, upper case, at most max characters
        private static string ShortPart(string part, int max)
        {
            var sb = new StringBuilder(max);
            foreach (char c in part)
            {
                if (c == ' ' || c == '.') continue;
                sb.Append(Array.IndexOf(Not83, c) > 0 ? '_' : char.ToUpperInvariant(c));
                if (sb.Length == max) break;
            }
            return sb.ToString();
        }

        // the match ends a path segment: the end of the text, a separator, or anything that cannot be part of a name
        private static bool EndsSegment(string text, int end) => end >= text.Length || !IsNameChar(text[end]);

        private static bool IsSeparator(char c) => c == '\\' || c == '/';

        private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
    }
}
