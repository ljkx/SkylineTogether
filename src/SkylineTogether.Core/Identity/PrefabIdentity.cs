using System.Text;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// The cross-machine identity of one prefab entity, taken from its <c>PrefabID</c> (type name, prefab name and
    /// <c>Hash128</c>; <c>Game.Prefabs/PrefabID.cs:15-36</c>, R04 §1.3) by game-ecs-schema: <see cref="TypeName"/> =
    /// <c>prefab.GetType().Name</c>, <see cref="Name"/> = <c>prefab.name</c>, <see cref="Hash"/> = the hash as text,
    /// empty when the hash is not valid (prefabs created in code have no asset and therefore no hash). The hash is the
    /// hash of the asset's platform id, else the asset guid; <c>PrefabID</c> keeps all three fields private, but its
    /// <c>ToString()</c> prints <c>"Type:Name (hash)"</c> when the hash is valid and <c>"Type:Name"</c> otherwise.
    /// Every peer must derive the text the same way. <see cref="EntityIndex"/> is the machine-local prefab entity index
    /// (-1 when unknown); <see cref="SourceHint"/> names the mod or asset pack for reports and is not part of the
    /// identity.
    /// </summary>
    public struct PrefabIdentity
    {
        public string TypeName;
        public string Name;
        public string Hash;
        public int EntityIndex;
        public string SourceHint;

        public PrefabIdentity(string typeName, string name, string hash, int entityIndex, string sourceHint = null)
        {
            TypeName = typeName;
            Name = name;
            Hash = hash;
            EntityIndex = entityIndex;
            SourceHint = sourceHint;
        }

        /// <summary>
        /// <c>"type|name|hash"</c>, the sort key of the prefab ranks (ARCH §3.5). Null parts are empty. A <c>|</c> or
        /// <c>\</c> inside a part is escaped with a backslash, so two different identities never share a key.
        /// </summary>
        public string Key => MakeKey(TypeName, Name, Hash);

        /// <summary>The key of a (type, name, hash) triple (see <see cref="Key"/>).</summary>
        public static string MakeKey(string typeName, string name, string hash)
        {
            if (!NeedsEscape(typeName) && !NeedsEscape(name) && !NeedsEscape(hash))
            {
                return (typeName ?? string.Empty) + "|" + (name ?? string.Empty) + "|" + (hash ?? string.Empty);
            }
            var sb = new StringBuilder();
            AppendEscaped(sb, typeName);
            sb.Append('|');
            AppendEscaped(sb, name);
            sb.Append('|');
            AppendEscaped(sb, hash);
            return sb.ToString();
        }

        public override string ToString() => string.IsNullOrEmpty(SourceHint) ? Key : Key + " (" + SourceHint + ")";

        private static bool NeedsEscape(string part)
        {
            return part != null && (part.IndexOf('|') >= 0 || part.IndexOf('\\') >= 0);
        }

        private static void AppendEscaped(StringBuilder sb, string part)
        {
            if (part == null) return;
            foreach (char c in part)
            {
                if (c == '|' || c == '\\') sb.Append('\\');
                sb.Append(c);
            }
        }
    }
}
