using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Colossal;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Settings
{
    /// <summary>
    /// The mod's texts for the game's localization (ARCHITECTURE.md#ui-module): one embedded table per language,
    /// <c>Data/locale.&lt;id&gt;.json</c>, English as the fallback for missing keys. The same keys serve the Options page
    /// (mapped onto the ids the settings page generates), the notifications and the UI module (cs2/l10n translate).
    /// </summary>
    public sealed class LocaleSource : IDictionarySource
    {
        public const string EnglishLocale = "en-US";
        private const string ResourcePrefix = "SkylineTogether.Data.locale.";

        private static LocaleTable _english;
        private static readonly Dictionary<string, LocaleTable> Tables = new Dictionary<string, LocaleTable>(StringComparer.OrdinalIgnoreCase);

        private readonly string _localeId;
        private readonly SkylineTogetherSettings _settings;

        public LocaleSource(string localeId, SkylineTogetherSettings settings)
        {
            _localeId = localeId;
            _settings = settings;
        }

        /// <summary>The locales with an embedded table (always including English).</summary>
        public static IEnumerable<string> AvailableLocales()
        {
            LoadTables();
            return new List<string>(Tables.Keys);
        }

        /// <summary>Registers a source for every embedded locale with the game's localization manager.</summary>
        public static void RegisterAll(SkylineTogetherSettings settings)
        {
            foreach (string locale in AvailableLocales())
            {
                GameManager.instance.localizationManager.AddSource(locale, new LocaleSource(locale, settings));
            }
        }

        /// <summary>
        /// The text of <paramref name="key"/> in the active language (English when missing), with <c>{NAME}</c>
        /// placeholders replaced by <paramref name="args"/> (name, value, name, value, ...).
        /// </summary>
        public static string Resolve(string key, params string[] args)
        {
            string text;
            try
            {
                var dictionary = GameManager.instance?.localizationManager?.activeDictionary;
                if (dictionary == null || !dictionary.TryGetValue(key, out text)) text = null;
            }
            catch (Exception)
            {
                text = null;
            }
            if (string.IsNullOrEmpty(text))
            {
                LoadTables();
                if (_english == null || _english.Entries == null || !_english.Entries.TryGetValue(key, out text)) text = key;
            }
            return Format(text, args);
        }

        /// <summary>Replaces <c>{NAME}</c> placeholders (pairs of name and value).</summary>
        public static string Format(string text, params string[] args)
        {
            if (text == null || args == null || args.Length < 2) return text;
            var sb = new StringBuilder(text);
            for (int i = 0; i + 1 < args.Length; i += 2) sb.Replace("{" + args[i] + "}", args[i + 1] ?? "");
            return sb.ToString();
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            LoadTables();
            Tables.TryGetValue(_localeId, out LocaleTable table);
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_english != null)
            {
                foreach (KeyValuePair<string, string> e in _english.Entries)
                {
                    d[e.Key] = table != null && table.Entries.TryGetValue(e.Key, out string translated) && !string.IsNullOrEmpty(translated) ? translated : e.Value;
                }
            }
            if (_settings != null) AddOptionTexts(d);
            return d;
        }

        public void Unload()
        {
        }

        /// <summary>The Options page asks for generated ids; they map onto the <c>st.options.*</c> keys.</summary>
        private void AddOptionTexts(Dictionary<string, string> d)
        {
            d[_settings.GetSettingsLocaleID()] = Get(d, "st.options.title");
            d[_settings.GetOptionTabLocaleID(SkylineTogetherSettings.MainSection)] = Get(d, "st.options.tab");
            foreach (string group in new[] { SkylineTogetherSettings.PlayerGroup, SkylineTogetherSettings.HostGroup, SkylineTogetherSettings.JoinGroup, SkylineTogetherSettings.SessionGroup, SkylineTogetherSettings.DiagnosticsGroup })
            {
                d[_settings.GetOptionGroupLocaleID(group)] = Get(d, "st.options.group." + group);
            }
            foreach (string option in SkylineTogetherSettings.OptionNames)
            {
                d[_settings.GetOptionLabelLocaleID(option)] = Get(d, "st.options." + option + ".label");
                d[_settings.GetOptionDescLocaleID(option)] = Get(d, "st.options." + option + ".desc");
            }
        }

        private static string Get(Dictionary<string, string> d, string key) => d.TryGetValue(key, out string v) ? v : key;

        private static void LoadTables()
        {
            if (_english != null) return;
            lock (Tables)
            {
                if (_english != null) return;
                Assembly assembly = typeof(LocaleSource).Assembly;
                foreach (string name in assembly.GetManifestResourceNames())
                {
                    if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
                    string locale = name.Substring(ResourcePrefix.Length, name.Length - ResourcePrefix.Length - ".json".Length);
                    try
                    {
                        using (Stream stream = assembly.GetManifestResourceStream(name))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            Tables[locale] = LocaleTable.Parse(locale, MiniJson.Parse(reader.ReadToEnd(), name));
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.Log?.Warn(LogCategory.Ui, "locale table " + name + " is unreadable", ex);
                    }
                }
                if (!Tables.TryGetValue(EnglishLocale, out LocaleTable english))
                {
                    english = new LocaleTable { LocaleId = EnglishLocale, Entries = new Dictionary<string, string>() };
                    Tables[EnglishLocale] = english;
                }
                _english = english;
            }
        }
    }
}
