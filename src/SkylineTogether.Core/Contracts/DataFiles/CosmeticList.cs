using System.Collections.Generic;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// The client's cosmetic allow-list, <c>cosmetic.json</c> (ARCH §6.1): the only simulation systems the follower
    /// clock runs, none of which writes replicated state. Schema: <c>{ "version": 1, "systems": [ "Game.Simulation.TimeSystem", ... ] }</c>
    /// (<c>version</c> optional), or simply the array. Entries are unique full type names. <c>StreetLightSystem</c> is
    /// rejected: it writes the replicated light flags from client-local inputs (ARCH §6.1, <c>StreetLightSystem.cs:83-151</c>).
    /// </summary>
    public sealed class CosmeticList
    {
        /// <summary>File name used in error messages and digests.</summary>
        public const string FileName = "cosmetic.json";

        /// <summary>Systems proven to write replicated state; they can never be cosmetic.</summary>
        internal static readonly string[] NeverCosmetic = { "Game.Simulation.StreetLightSystem" };

        public IReadOnlyList<string> SystemTypeNames;

        public static CosmeticList Parse(JsonValue json)
        {
            var r = new ConfigReader(FileName);
            const string root = "$";
            IReadOnlyList<string> systems;
            JsonValue list;
            string listPath;
            if (json != null && json.Kind == JsonKind.Array)
            {
                systems = r.ReadStringArray(json, root, true, true);
                list = json;
                listPath = root;
            }
            else
            {
                r.RequireObject(json, root);
                r.CheckKeys(json, root, "version", "systems");
                r.GetInt(json, root, "version", false, 1, 1, int.MaxValue);
                systems = r.GetStringList(json, root, "systems", true, true);
                list = json["systems"];
                listPath = ConfigReader.Child(root, "systems");
            }
            for (int i = 0; i < systems.Count; i++)
            {
                if (System.Array.IndexOf(NeverCosmetic, systems[i]) >= 0)
                {
                    throw r.Error(list[i], ConfigReader.Index(listPath, i),
                                  "\"" + systems[i] + "\" writes replicated state and can never be cosmetic (ARCH §6.1)");
                }
            }
            return new CosmeticList { SystemTypeNames = systems };
        }
    }
}
