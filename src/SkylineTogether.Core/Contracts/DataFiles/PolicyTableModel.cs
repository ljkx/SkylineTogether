using System.Collections.Generic;
using System.Collections.ObjectModel;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// The policy table, <c>policies.json</c> (ARCH §4.2.7, Appendix F), parsed and validated in Core; core-replication's
    /// <c>PolicyTable</c> resolves it against the type catalog. Its version is part of the catalog fingerprint. Schema:
    /// <code>
    /// {
    ///   "version": 3,                                       required integer &gt;= 1
    ///   "familyDefaults": { "NetEdge": "S", "Agent": "A" }, optional: Family name -&gt; ReplicationClass name
    ///   "types": [
    ///     { "type": "Game.Pathfind.PathInformation",        required full type name
    ///       "family": "Segment",                            optional: the entry applies to this family only
    ///       "class": "S",                                   optional ReplicationClass (not "Excluded": use "excluded")
    ///       "codec": "Serializable",                        optional CodecKind
    ///       "source": true, "cacheReceived": true, "guarded": true,
    ///       "standInResolver": "SegmentPathEndpoints",      implies presenceSafe
    ///       "applyTags": [ "BatchesUpdated" ],              optional TouchFlags names
    ///       "topics": [ "Transport" ] }                     optional AttentionTopic names
    ///   ]
    /// }
    /// </code>
    /// Boolean flags (default false): <c>playerAuthorable</c>, <c>presenceAuthorable</c>, <c>hashAsSet</c>,
    /// <c>noReapply</c>, <c>onDemand</c>, <c>excluded</c>, <c>source</c>, <c>cacheReceived</c>, <c>guarded</c>,
    /// <c>presenceSafe</c>, <c>dataReplicated</c> (presence type replicated as data, ARCH §4.2.4), <c>forwardReference</c>.
    /// Names of hand-written game-layer functions: <c>mergePolicy</c> or <c>deferredFields</c> (one of them; both fill
    /// <see cref="TypePolicyEntry.MergePolicy"/>), <c>authorableMask</c>, <c>standInResolver</c>.
    /// <c>reverseBuffers</c>: reverse-buffer type names co-captured with a forward reference (implies forwardReference).
    /// A (type, family) pair may appear once. Members starting with <c>_</c> are comments.
    /// </summary>
    public sealed class PolicyTableModel
    {
        /// <summary>File name used in error messages and digests.</summary>
        public const string FileName = "policies.json";

        private static readonly string[] EntryKeys =
        {
            "type", "family", "class", "codec", "playerAuthorable", "presenceAuthorable", "hashAsSet", "noReapply", "onDemand",
            "excluded", "source", "cacheReceived", "guarded", "presenceSafe", "dataReplicated", "forwardReference",
            "mergePolicy", "deferredFields", "authorableMask", "standInResolver", "applyTags", "topics", "reverseBuffers"
        };

        public int Version;
        public IReadOnlyList<TypePolicyEntry> Types;
        public IReadOnlyDictionary<Family, ReplicationClass> FamilyDefaults;

        public static PolicyTableModel Parse(JsonValue json)
        {
            var r = new ConfigReader(FileName);
            const string root = "$";
            r.RequireObject(json, root);
            r.CheckKeys(json, root, "version", "familyDefaults", "types");
            int version = r.GetInt(json, root, "version", true, 0, 1, int.MaxValue);
            var defaults = ParseFamilyDefaults(r, json, root);

            var types = new List<TypePolicyEntry>();
            JsonValue array = r.GetArray(json, root, "types", false);
            if (array != null)
            {
                string arrayPath = ConfigReader.Child(root, "types");
                var seen = new HashSet<string>(System.StringComparer.Ordinal);
                for (int i = 0; i < array.Count; i++)
                {
                    string path = ConfigReader.Index(arrayPath, i);
                    TypePolicyEntry entry = ParseEntry(r, array[i], path);
                    string identity = entry.TypeName + "|" + (entry.Family.HasValue ? entry.Family.Value.ToString() : "*");
                    if (!seen.Add(identity))
                    {
                        throw r.Error(array[i], path, "\"" + entry.TypeName + "\" is listed twice"
                                                      + (entry.Family.HasValue ? " for the family " + entry.Family.Value : " without a family"));
                    }
                    types.Add(entry);
                }
            }
            return new PolicyTableModel
            {
                Version = version,
                Types = types.AsReadOnly(),
                FamilyDefaults = new ReadOnlyDictionary<Family, ReplicationClass>(defaults)
            };
        }

        private static Dictionary<Family, ReplicationClass> ParseFamilyDefaults(ConfigReader r, JsonValue json, string root)
        {
            var defaults = new Dictionary<Family, ReplicationClass>();
            if (!json.Has("familyDefaults")) return defaults;
            JsonValue map = json["familyDefaults"];
            string mapPath = ConfigReader.Child(root, "familyDefaults");
            r.RequireObject(map, mapPath);
            foreach (string key in map.Keys)
            {
                if (ConfigReader.IsComment(key)) continue;
                string path = ConfigReader.Child(mapPath, key);
                JsonValue value = map[key];
                Family family = r.ParseEnum<Family>(value, path, key);
                if (value.Kind != JsonKind.String) throw r.Error(value, path, "expected a ReplicationClass name, found " + ConfigReader.KindName(value));
                ReplicationClass cls = r.ParseEnum<ReplicationClass>(value, path, value.AsString());
                if (defaults.ContainsKey(family)) throw r.Error(value, path, "the family " + family + " is listed twice");
                defaults.Add(family, cls);
            }
            return defaults;
        }

        private static TypePolicyEntry ParseEntry(ConfigReader r, JsonValue item, string path)
        {
            r.RequireObject(item, path);
            r.CheckKeys(item, path, EntryKeys);
            var entry = new TypePolicyEntry
            {
                TypeName = r.GetTypeName(item, path, "type", true),
                Family = r.GetOptionalEnum<Family>(item, path, "family", false),
                Class = r.GetOptionalEnum<ReplicationClass>(item, path, "class", false),
                Codec = r.GetOptionalEnum<CodecKind>(item, path, "codec", false),
                ApplyTags = r.GetFlags<TouchFlags>(item, path, "applyTags"),
                Topics = r.GetFlags<AttentionTopic>(item, path, "topics"),
                AuthorableMask = GetFunctionName(r, item, path, "authorableMask"),
                StandInResolver = GetFunctionName(r, item, path, "standInResolver"),
                ReverseBuffers = r.GetStringList(item, path, "reverseBuffers", true)
            };

            TypeFlags flags = TypeFlags.None;
            if (r.GetBool(item, path, "playerAuthorable", false)) flags |= TypeFlags.PlayerAuthorable;
            if (r.GetBool(item, path, "presenceAuthorable", false)) flags |= TypeFlags.PresenceAuthorable;
            if (r.GetBool(item, path, "hashAsSet", false)) flags |= TypeFlags.HashAsSet;
            if (r.GetBool(item, path, "noReapply", false)) flags |= TypeFlags.NoReapply;
            if (r.GetBool(item, path, "source", false)) flags |= TypeFlags.Source;
            if (r.GetBool(item, path, "cacheReceived", false)) flags |= TypeFlags.CacheReceived;
            if (r.GetBool(item, path, "guarded", false)) flags |= TypeFlags.Guarded;
            if (r.GetBool(item, path, "dataReplicated", false)) flags |= TypeFlags.DataReplicatedPresence;

            if ((flags & TypeFlags.PlayerAuthorable) != 0 && (flags & TypeFlags.PresenceAuthorable) != 0)
            {
                throw r.Error(item, path, "playerAuthorable and presenceAuthorable exclude each other");
            }

            // class, exclusion and on-demand
            if (entry.Class == ReplicationClass.Excluded)
            {
                throw r.Error(item["class"], ConfigReader.Child(path, "class"), "use \"excluded\": true to exclude a type (class Excluded means \"no override\")");
            }
            if (r.GetBool(item, path, "excluded", false))
            {
                if (entry.Class.HasValue) throw r.Error(item["class"], ConfigReader.Child(path, "class"), "an excluded type has no class");
                flags |= TypeFlags.Excluded;
            }
            bool onDemand = r.GetBool(item, path, "onDemand", false);
            if (entry.Class == ReplicationClass.OnDemand) onDemand = true;
            if (onDemand)
            {
                if (entry.Class.HasValue && entry.Class.Value != ReplicationClass.OnDemand)
                {
                    throw r.Error(item["class"], ConfigReader.Child(path, "class"), "an onDemand type has the class OnDemand");
                }
                if ((flags & TypeFlags.Excluded) != 0) throw r.Error(item, path, "excluded and onDemand exclude each other");
                entry.Class = ReplicationClass.OnDemand;
                flags |= TypeFlags.OnDemand;
            }

            // merge or deferred-field policy
            string merge = GetFunctionName(r, item, path, "mergePolicy");
            string deferred = GetFunctionName(r, item, path, "deferredFields");
            if (merge != null && deferred != null) throw r.Error(item, path, "mergePolicy and deferredFields exclude each other");
            if (merge != null)
            {
                flags |= TypeFlags.Merge;
                entry.MergePolicy = merge;
            }
            if (deferred != null)
            {
                flags |= TypeFlags.DeferredFields;
                entry.MergePolicy = deferred;
            }
            if (entry.AuthorableMask != null) flags |= TypeFlags.AuthorableMask;

            // presence-safe references need a stand-in resolver
            bool? presenceSafe = r.GetOptionalBool(item, path, "presenceSafe");
            if (entry.StandInResolver != null)
            {
                if (presenceSafe == false) throw r.Error(item["presenceSafe"], ConfigReader.Child(path, "presenceSafe"), "a stand-in resolver makes the type presence-safe");
                flags |= TypeFlags.PresenceSafe;
            }
            else if (presenceSafe == true)
            {
                throw r.Error(item["presenceSafe"], ConfigReader.Child(path, "presenceSafe"), "a presence-safe type needs a \"standInResolver\"");
            }

            // forward references name the reverse buffers they co-capture
            bool? forward = r.GetOptionalBool(item, path, "forwardReference");
            if (entry.ReverseBuffers.Count > 0)
            {
                if (forward == false) throw r.Error(item["forwardReference"], ConfigReader.Child(path, "forwardReference"), "\"reverseBuffers\" make the type a forward reference");
                foreach (string reverse in entry.ReverseBuffers)
                {
                    if (reverse == entry.TypeName) throw r.Error(item["reverseBuffers"], ConfigReader.Child(path, "reverseBuffers"), "a type cannot be its own reverse buffer");
                }
                flags |= TypeFlags.ForwardReference;
            }
            else if (forward == true)
            {
                throw r.Error(item["forwardReference"], ConfigReader.Child(path, "forwardReference"), "a forward reference needs \"reverseBuffers\"");
            }

            entry.Flags = flags;
            return entry;
        }

        private static string GetFunctionName(ConfigReader r, JsonValue item, string path, string key)
        {
            string name = r.GetString(item, path, key, false);
            if (name != null) r.CheckIdentifier(item[key], ConfigReader.Child(path, key), name, "._", 128);
            return name;
        }
    }
}
