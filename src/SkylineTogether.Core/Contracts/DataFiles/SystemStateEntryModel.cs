using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// One entry of the system-state allow-list, <c>sysstate.json</c>, or of an adapter's <c>systemStateSync</c>
    /// (ARCH §4.10.1). A SYS record names its entry by <see cref="StableId"/>, a hash of the string id, never by a
    /// position, so a different registry can never route a blob into the wrong system. Schema of one entry:
    /// <code>
    /// { "id": "TaxSystem",                        required: letters, digits and . _ - : (at most 128 characters)
    ///   "system": "Game.Simulation.TaxSystem",    required full type name of the system
    ///   "codec": "GameSerializable",              optional SystemStateCodecKind, default GameSerializable
    ///   "maxHz": 1,                               optional capture rate limit in Hz, 0.001-60, default 1
    ///   "requiresRoundTrip": true,                optional, default true: enabled only after the in-game round trip passed
    ///   "readerHandleMember": "Readers" }         optional: member holding manual reader handles completed before Deserialize
    /// </code>
    /// The file is <c>{ "version": 1, "entries": [ ... ] }</c> (<c>version</c> optional) or simply the array.
    /// </summary>
    public sealed class SystemStateEntryModel
    {
        /// <summary>File name used in error messages and digests.</summary>
        public const string FileName = "sysstate.json";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        public string Id;

        /// <summary>The wire identity: <c>HashMix.Fold32(XxHash64(UTF-8 bytes of Id, seed 0))</c>; 0 when Id is null.</summary>
        public uint StableId => ComputeStableId(Id);

        public string SystemTypeName;
        public SystemStateCodecKind Codec;
        /// <summary>Upper bound of the capture rate (ARCH §4.10.1 cadences).</summary>
        public double MaxHz;
        /// <summary>The entry is enabled only after its in-game round trip (serialise, deserialise, re-serialise) passed.</summary>
        public bool RequiresRoundTrip;
        /// <summary>Name of the member that holds manual reader handles to complete before <c>Deserialize</c>
        /// (e.g. <c>TaxSystem.Readers</c>, R11 §5.3); null if none.</summary>
        public string ReaderHandleMember;

        /// <summary>The stable id of a string id (see <see cref="StableId"/>).</summary>
        public static uint ComputeStableId(string id)
        {
            if (id == null) return 0;
            byte[] utf8 = Utf8.GetBytes(id);
            return HashMix.Fold32(XxHash64.Hash(utf8, 0, utf8.Length));
        }

        /// <summary>Parses <c>sysstate.json</c>; rejects duplicate ids and duplicate stable ids (32-bit hash collisions).</summary>
        public static IReadOnlyList<SystemStateEntryModel> ParseList(JsonValue json)
        {
            var r = new ConfigReader(FileName);
            const string root = "$";
            JsonValue array;
            string arrayPath;
            if (json != null && json.Kind == JsonKind.Array)
            {
                array = json;
                arrayPath = root;
            }
            else
            {
                r.RequireObject(json, root);
                r.CheckKeys(json, root, "version", "entries");
                r.GetInt(json, root, "version", false, 1, 1, int.MaxValue);
                array = r.GetArray(json, root, "entries", true);
                arrayPath = ConfigReader.Child(root, "entries");
            }
            return ParseEntries(r, array, arrayPath, new Dictionary<uint, string>()).AsReadOnly();
        }

        internal static List<SystemStateEntryModel> ParseEntries(ConfigReader r, JsonValue array, string arrayPath, Dictionary<uint, string> stableIds)
        {
            var result = new List<SystemStateEntryModel>();
            for (int i = 0; i < array.Count; i++)
            {
                JsonValue item = array[i];
                string path = ConfigReader.Index(arrayPath, i);
                r.RequireObject(item, path);
                r.CheckKeys(item, path, "id", "system", "codec", "maxHz", "requiresRoundTrip", "readerHandleMember");
                string id = r.GetString(item, path, "id", true);
                r.CheckIdentifier(item["id"], ConfigReader.Child(path, "id"), id, "._-:", 128);
                double maxHz = r.GetNumber(item, path, "maxHz", false, 1.0, 0.001, 60.0);
                string readerHandle = r.GetString(item, path, "readerHandleMember", false);
                if (readerHandle != null) r.CheckIdentifier(item["readerHandleMember"], ConfigReader.Child(path, "readerHandleMember"), readerHandle, "._", 128);
                var entry = new SystemStateEntryModel
                {
                    Id = id,
                    SystemTypeName = r.GetTypeName(item, path, "system", true),
                    Codec = r.GetEnum(item, path, "codec", false, SystemStateCodecKind.GameSerializable),
                    MaxHz = maxHz,
                    RequiresRoundTrip = r.GetBool(item, path, "requiresRoundTrip", true),
                    ReaderHandleMember = readerHandle
                };
                uint stableId = entry.StableId;
                if (stableId == 0) throw r.Error(item["id"], ConfigReader.Child(path, "id"), "the id \"" + id + "\" hashes to the reserved stable id 0; choose another id");
                if (stableIds.TryGetValue(stableId, out string other))
                {
                    string message = other == id
                        ? "the id \"" + id + "\" is listed twice"
                        : "the ids \"" + other + "\" and \"" + id + "\" have the same stable id 0x" + stableId.ToString("x8", CultureInfo.InvariantCulture) + "; rename one";
                    throw r.Error(item["id"], ConfigReader.Child(path, "id"), message);
                }
                stableIds.Add(stableId, id);
                result.Add(entry);
            }
            return result;
        }

        public override string ToString() => (Id ?? "?") + " (" + (SystemTypeName ?? "?") + ", 0x" + StableId.ToString("x8", CultureInfo.InvariantCulture) + ")";
    }
}
