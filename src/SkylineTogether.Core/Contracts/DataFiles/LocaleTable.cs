using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// A string table, <c>locale.&lt;id&gt;.json</c> (ARCHITECTURE.md#ui-module): a flat JSON object of key → text, for example
    /// <c>{ "st.menu.coop": "Co-op", "st.toast.rejected.conflict": "Conflicts with {0}'s edit" }</c>. Keys are unique,
    /// non-empty and free of control characters; values are strings (nested objects are rejected); members starting
    /// with <c>_</c> are comments. English is the fallback for missing keys (game-bootstrap <c>LocaleSource</c>).
    /// Locale tables are not part of the handshake digest.
    /// </summary>
    public sealed class LocaleTable
    {
        public string LocaleId;
        public IReadOnlyDictionary<string, string> Entries;

        /// <summary>The file name of a locale, e.g. <c>locale.en-US.json</c>.</summary>
        public static string FileNameOf(string localeId) => "locale." + localeId + ".json";

        public static LocaleTable Parse(string localeId, JsonValue json)
        {
            var r = new ConfigReader(FileNameOf(localeId ?? "?"));
            r.CheckIdentifier(null, "localeId", localeId, "-_", 32);
            const string root = "$";
            r.RequireObject(json, root);
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in json.Keys)
            {
                if (ConfigReader.IsComment(key)) continue;
                JsonValue value = json[key];
                string path = ConfigReader.Child(root, key);
                if (key.Length == 0 || char.IsWhiteSpace(key[0]) || char.IsWhiteSpace(key[key.Length - 1]))
                {
                    throw r.Error(value, path, "a locale key must not be empty or start or end with whitespace");
                }
                foreach (char c in key)
                {
                    if (c < 0x20 || c == 0x7F) throw r.Error(value, path, "control character in a locale key");
                }
                if (value.Kind != JsonKind.String) throw r.Error(value, path, "expected the text as a string, found " + ConfigReader.KindName(value));
                entries.Add(key, value.AsString());   // keys are unique: the parser rejects duplicate members
            }
            return new LocaleTable { LocaleId = localeId, Entries = new ReadOnlyDictionary<string, string>(entries) };
        }

        /// <summary>The keys of <paramref name="keys"/> that this table lacks, in their given order and without duplicates.</summary>
        public IReadOnlyList<string> MissingKeys(IEnumerable<string> keys)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            var missing = new List<string>();
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                if (key == null) continue;
                if (Entries == null || !Entries.ContainsKey(key))
                {
                    if (reported.Add(key)) missing.Add(key);
                }
            }
            return missing.AsReadOnly();
        }
    }
}
