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
    /// count (Unity writes <c>/</c>), letters are compared case-insensitively. Text without the user name is returned
    /// unchanged (the same instance), so the check is cheap for ordinary log lines. Immutable; thread-safe.
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
            if (roots != null)
            {
                foreach (KeyValuePair<string, string> r in roots)
                {
                    string root = TrimSeparators(r.Key);
                    if (!IsUsableRoot(root) || string.IsNullOrEmpty(r.Value)) continue;
                    if (list.Exists(x => string.Equals(Normalize(x.Key), Normalize(root), StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new KeyValuePair<string, string>(root, r.Value));
                    // the profile folder's own name may differ from the user name (name_000, name.DOMAIN)
                    if (r.Value == UserProfileToken) AddName(names, LastSegment(root));
                    AddName(probes, LastSegment(root), 1);
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
            foreach (string n in names) AddName(probes, n, 1);
            _probes = _roots.Length + _names.Length == 0 ? new string[0] : probes.ToArray();
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
                        if (MatchesAt(text, i, _roots[r]) && EndsSegment(text, i + _roots[r].Length))
                        {
                            length = _roots[r].Length;
                            token = _rootTokens[r];
                            break;
                        }
                    }
                }
                if (length == 0 && i > 0 && IsSeparator(text[i - 1]))
                {
                    foreach (string name in _names)
                    {
                        if (MatchesAt(text, i, name) && EndsSegment(text, i + name.Length))
                        {
                            length = name.Length;
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

        private static PathRedactor FromEnvironment()
        {
            try
            {
                string profile = Folder(Environment.SpecialFolder.UserProfile, "USERPROFILE");
                var roots = new List<KeyValuePair<string, string>>();
                if (!string.IsNullOrEmpty(profile)) roots.Add(new KeyValuePair<string, string>(profile, UserProfileToken));
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

        private static bool MatchesAt(string text, int start, string pattern)
        {
            if (start + pattern.Length > text.Length) return false;
            for (int k = 0; k < pattern.Length; k++)
            {
                char a = text[start + k];
                char b = pattern[k];
                if (IsSeparator(a) && IsSeparator(b)) continue;
                if (a != b && char.ToUpperInvariant(a) != char.ToUpperInvariant(b)) return false;
            }
            return true;
        }

        // the match ends a path segment: the end of the text, a separator, or anything that cannot be part of a name
        private static bool EndsSegment(string text, int end) => end >= text.Length || !IsNameChar(text[end]);

        private static bool IsSeparator(char c) => c == '\\' || c == '/';

        private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
    }
}
