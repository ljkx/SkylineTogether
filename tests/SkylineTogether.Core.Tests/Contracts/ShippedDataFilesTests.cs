using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>
    /// Validates the data files the game layer ships (<c>src/SkylineTogether/Data</c>) with the Core parsers, and the
    /// locale-key coverage (IMPLEMENTATION-PLAN §3 rule 11): every key referenced by the game layer exists in
    /// <c>locale.en-US.json</c>. Referenced keys come from the generated list <c>Data/locale-keys.generated.json</c>
    /// (a JSON array of strings, or <c>{ "keys": [ ... ] }</c>) and from string literals passed directly to
    /// <c>LocaleSource.Resolve("...")</c> in the game sources. The tests skip while the files do not exist yet.
    /// </summary>
    public sealed class ShippedDataFilesTests
    {
        private const string DataFolder = "src/SkylineTogether/Data";
        private const string EnglishLocale = "en-US";
        private const string KeyListFile = "locale-keys.generated.json";

        private static string DataDirectory()
        {
            string root = TestPaths.RepoRoot;
            return root == null ? null : Path.Combine(root, DataFolder.Replace('/', Path.DirectorySeparatorChar));
        }

        private static JsonValue ParseFile(string path) => MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8), Path.GetFileName(path));

        [Test]
        public void ShippedDataFilesParse()
        {
            string dir = DataDirectory();
            if (dir == null || !Directory.Exists(dir)) Assert.Skip("no " + DataFolder + " folder yet");
            int parsed = 0;
            foreach (string path in Directory.GetFiles(dir, "locale.*.json"))
            {
                string name = Path.GetFileName(path);
                string localeId = name.Substring("locale.".Length, name.Length - "locale.".Length - ".json".Length);
                LocaleTable.Parse(localeId, ParseFile(path));
                parsed++;
            }
            if (parsed == 0) Assert.Skip("no data files in " + DataFolder + " yet");
        }

        [Test]
        public void EveryReferencedLocaleKeyExistsInEnglish()
        {
            string dir = DataDirectory();
            var referenced = new List<string>();
            var sources = new List<string>();
            if (dir != null && File.Exists(Path.Combine(dir, KeyListFile)))
            {
                referenced.AddRange(ReadKeyList(Path.Combine(dir, KeyListFile)));
                sources.Add(KeyListFile);
            }
            string gameSources = TestPaths.RepoRoot == null ? null : Path.Combine(TestPaths.RepoRoot, "src", "SkylineTogether");
            if (gameSources != null && Directory.Exists(gameSources))
            {
                int before = referenced.Count;
                referenced.AddRange(ScanResolveLiterals(gameSources));
                if (referenced.Count > before) sources.Add("LocaleSource.Resolve literals");
            }

            string englishPath = dir == null ? null : Path.Combine(dir, LocaleTable.FileNameOf(EnglishLocale));
            bool haveEnglish = englishPath != null && File.Exists(englishPath);
            if (!haveEnglish)
            {
                if (referenced.Count > 0) Assert.Fail(referenced.Count + " locale key(s) are referenced but " + DataFolder + "/" + LocaleTable.FileNameOf(EnglishLocale) + " does not exist");
                Assert.Skip("no English locale table yet");
            }
            LocaleTable english = LocaleTable.Parse(EnglishLocale, ParseFile(englishPath));
            IReadOnlyList<string> missing = english.MissingKeys(referenced);
            Assert.Equal(0, missing.Count, "keys referenced by " + string.Join(" and ", sources) + " but missing in English: " + string.Join(", ", missing));

            // a translation must not invent keys the English table does not know (usually a renamed key)
            foreach (string path in Directory.GetFiles(dir, "locale.*.json"))
            {
                string name = Path.GetFileName(path);
                string localeId = name.Substring("locale.".Length, name.Length - "locale.".Length - ".json".Length);
                if (localeId == EnglishLocale) continue;
                LocaleTable translation = LocaleTable.Parse(localeId, ParseFile(path));
                IReadOnlyList<string> unknown = english.MissingKeys(translation.Entries.Keys);
                Assert.Equal(0, unknown.Count, name + " has keys that English lacks: " + string.Join(", ", unknown));
            }
        }

        private static IEnumerable<string> ReadKeyList(string path)
        {
            JsonValue json = ParseFile(path);
            JsonValue array = json.Kind == JsonKind.Object ? json["keys"] : json;
            if (array.Kind != JsonKind.Array) Assert.Fail(KeyListFile + " must be an array of strings or { \"keys\": [ ... ] }");
            for (int i = 0; i < array.Count; i++)
            {
                string key = array[i].AsString();
                if (key == null) Assert.Fail(KeyListFile + "[" + i + "] is not a string");
                yield return key;
            }
        }

        private static IEnumerable<string> ScanResolveLiterals(string folder)
        {
            var pattern = new Regex(@"LocaleSource\.Resolve\(\s*""([^""\\]+)""", RegexOptions.CultureInvariant);
            foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/obj/") || normalized.Contains("/bin/")) continue;
                foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                {
                    yield return match.Groups[1].Value;
                }
            }
        }
    }
}
