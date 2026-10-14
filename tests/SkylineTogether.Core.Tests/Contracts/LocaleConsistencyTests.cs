using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>
    /// Consistency of the shipped locale tables with each other and with the Options page (complements
    /// <see cref="ShippedDataFilesTests"/>, which checks that referenced keys exist in English): every translation uses
    /// the same <c>{PLACEHOLDER}</c> tokens as English and no empty text, and every option of
    /// <c>SkylineTogetherSettings</c> is listed in its <c>OptionNames</c> and has a label and a description (and every
    /// group a title) in every locale, so the Options page never shows a raw id.
    /// </summary>
    public sealed class LocaleConsistencyTests
    {
        private static readonly Regex Placeholder = new Regex(@"\{[A-Z0-9_]+\}", RegexOptions.CultureInvariant);

        private static string GameFolder(params string[] parts)
        {
            if (TestPaths.RepoRoot == null) return null;
            return Path.Combine(new[] { TestPaths.RepoRoot, "src", "SkylineTogether" }.Concat(parts).ToArray());
        }

        private static Dictionary<string, LocaleTable> Tables()
        {
            string dir = GameFolder("Data");
            if (dir == null || !Directory.Exists(dir)) Assert.Skip("no game data folder");
            var tables = new Dictionary<string, LocaleTable>();
            foreach (string path in Directory.GetFiles(dir, "locale.*.json"))
            {
                string name = Path.GetFileName(path);
                string id = name.Substring("locale.".Length, name.Length - "locale.".Length - ".json".Length);
                tables[id] = LocaleTable.Parse(id, MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8), name));
            }
            if (!tables.ContainsKey("en-US")) Assert.Skip("no English locale table");
            return tables;
        }

        private static string Tokens(string text) => string.Join(" ", Placeholder.Matches(text ?? "").Cast<Match>().Select(m => m.Value).OrderBy(s => s, StringComparer.Ordinal));

        [Test]
        public void TranslationsKeepThePlaceholdersAndHaveNoEmptyTexts()
        {
            Dictionary<string, LocaleTable> tables = Tables();
            LocaleTable english = tables["en-US"];
            var problems = new List<string>();
            foreach (KeyValuePair<string, LocaleTable> t in tables)
            {
                foreach (KeyValuePair<string, string> entry in t.Value.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Value)) problems.Add(t.Key + " " + entry.Key + ": empty text");
                    if (t.Key == "en-US" || !english.Entries.TryGetValue(entry.Key, out string en)) continue;
                    if (Tokens(en) != Tokens(entry.Value)) problems.Add(t.Key + " " + entry.Key + ": placeholders [" + Tokens(entry.Value) + "], English [" + Tokens(en) + "]");
                }
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine + "     ", problems));
        }

        [Test]
        public void EveryOptionIsListedAndLabelledInEveryLocale()
        {
            string file = GameFolder("Settings", "SkylineTogetherSettings.cs");
            if (file == null || !File.Exists(file)) Assert.Skip("no settings source");
            string source = File.ReadAllText(file);

            // options: properties carrying [SettingsUISection(...)]
            var options = new List<string>();
            var property = new Regex(@"\[SettingsUISection\([^\]]*\)\](?:\s*\[[^\]]*\])*\s*public\s+[\w<>\[\]]+\s+(\w+)", RegexOptions.CultureInvariant);
            foreach (Match m in property.Matches(source)) options.Add(m.Groups[1].Value);
            Assert.True(options.Count >= 5, "found the options (" + options.Count + ")");

            Match list = Regex.Match(source, @"OptionNames\s*=\s*\{(.*?)\};", RegexOptions.Singleline);
            Assert.True(list.Success, "OptionNames found");
            var listed = Regex.Matches(list.Groups[1].Value, @"nameof\((\w+)\)").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(string.Join(",", options.OrderBy(s => s, StringComparer.Ordinal)), string.Join(",", listed.OrderBy(s => s, StringComparer.Ordinal)),
                         "OptionNames lists exactly the options of the Options page");
            Assert.Equal(listed.Count, listed.Distinct().Count(), "no option listed twice");

            Match order = Regex.Match(source, @"\[SettingsUIGroupOrder\(([^)]*)\)\]");
            Assert.True(order.Success, "group order found");
            var groups = new List<string>();
            foreach (string name in order.Groups[1].Value.Split(','))
            {
                Match constant = Regex.Match(source, @"const\s+string\s+" + Regex.Escape(name.Trim()) + @"\s*=\s*""([^""]+)""");
                Assert.True(constant.Success, "group constant " + name.Trim());
                groups.Add(constant.Groups[1].Value);
            }

            var keys = new List<string> { "st.options.title", "st.options.tab" };
            keys.AddRange(groups.Select(g => "st.options.group." + g));
            foreach (string option in listed)
            {
                keys.Add("st.options." + option + ".label");
                keys.Add("st.options." + option + ".desc");
            }
            foreach (KeyValuePair<string, LocaleTable> t in Tables())
            {
                IReadOnlyList<string> missing = t.Value.MissingKeys(keys);
                Assert.Equal(0, missing.Count, t.Key + " lacks Options page texts: " + string.Join(", ", missing));
            }
        }
    }
}
