using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// A UI action the host executes through the same public vanilla API the client's prediction used (docs/history/ARCHITECTURE-original.md §5.1 item 3,
    /// §5.3). The meaning of the fields depends on <see cref="Kind"/> (documented per <see cref="IntentKind"/> member);
    /// the static factories build exactly the documented shape. Also used by <c>RollbackPlan.ManagedRestores</c>
    /// to describe the managed state a rollback restores.
    /// </summary>
    public sealed class SemanticIntent
    {
        public IntentKind Kind;

        /// <summary>The entity the intent acts on; <see cref="NetSlot.None"/> when it has none.</summary>
        public NetSlot Target;

        /// <summary>Revision of <see cref="Target"/> for intents that edit structure (<see cref="IntentKind.Delete"/>,
        /// <see cref="IntentKind.ReplaceObjectPrefab"/>); economy setters are last-writer-wins (docs/history/ARCHITECTURE-original.md §3.8).</summary>
        public ushort BaseRevision;

        public PrefabRank Prefab = PrefabRank.None;

        public int IntValue;

        public float FloatValue;

        public bool BoolValue;

        /// <summary>Free text of at most <see cref="ProposalFormat.MaxTextChars"/> characters (longer text is truncated when written).</summary>
        public string Text;

        public int[] IntArray;

        public byte[] Blob;

        public static SemanticIntent SetPolicy(NetSlot target, PrefabRank policy, bool active, float adjustment)
        {
            return new SemanticIntent { Kind = IntentKind.SetPolicy, Target = target, Prefab = policy, BoolValue = active, FloatValue = adjustment };
        }

        /// <summary>Changed entries of the 92-int tax array as (index, value) pairs.</summary>
        public static SemanticIntent SetTaxRates(IEnumerable<KeyValuePair<int, int>> changes)
        {
            if (changes == null) throw new ArgumentNullException(nameof(changes));
            var pairs = new List<int>();
            foreach (KeyValuePair<int, int> change in changes)
            {
                pairs.Add(change.Key);
                pairs.Add(change.Value);
            }
            return new SemanticIntent { Kind = IntentKind.SetTaxRates, IntArray = pairs.ToArray() };
        }

        public static SemanticIntent SetServiceBudget(PrefabRank service, int percent)
        {
            return new SemanticIntent { Kind = IntentKind.SetServiceBudget, Prefab = service, IntValue = percent };
        }

        /// <param name="playerResource">The <c>Game.City.PlayerResource</c> value.</param>
        /// <param name="fee">The new fee.</param>
        public static SemanticIntent SetServiceFee(int playerResource, float fee)
        {
            return new SemanticIntent { Kind = IntentKind.SetServiceFee, IntValue = playerResource, FloatValue = fee };
        }

        public static SemanticIntent ChangeLoan(int targetAmount)
        {
            return new SemanticIntent { Kind = IntentKind.ChangeLoan, IntValue = targetAmount };
        }

        public static SemanticIntent PurchaseDevNode(PrefabRank node)
        {
            return new SemanticIntent { Kind = IntentKind.PurchaseDevNode, Prefab = node };
        }

        public static SemanticIntent PurchaseMapTiles(IEnumerable<NetSlot> tiles, int observedCost)
        {
            return new SemanticIntent { Kind = IntentKind.PurchaseMapTiles, IntArray = SlotArray(tiles, nameof(tiles)), IntValue = observedCost };
        }

        public static SemanticIntent Delete(NetSlot target, ushort baseRevision)
        {
            return new SemanticIntent { Kind = IntentKind.Delete, Target = target, BaseRevision = baseRevision };
        }

        /// <summary>A custom name; null or empty clears it.</summary>
        public static SemanticIntent Rename(NetSlot target, string name)
        {
            return new SemanticIntent { Kind = IntentKind.Rename, Target = target, Text = name };
        }

        public static SemanticIntent SetCityName(string name)
        {
            return new SemanticIntent { Kind = IntentKind.SetCityName, Text = name ?? string.Empty };
        }

        /// <param name="route">The route slot.</param>
        /// <param name="rgba">The colour packed as <c>r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24</c>.</param>
        public static SemanticIntent SetRouteColor(NetSlot route, uint rgba)
        {
            return new SemanticIntent { Kind = IntentKind.SetRouteColor, Target = route, IntValue = unchecked((int)rgba) };
        }

        /// <summary>The whole <c>CustomMeshColor</c> buffer as canonical bytes; empty resets it.</summary>
        public static SemanticIntent SetMeshColor(NetSlot target, byte[] canonicalBuffer)
        {
            return new SemanticIntent { Kind = IntentKind.SetMeshColor, Target = target, Blob = canonicalBuffer ?? Array.Empty<byte>() };
        }

        public static SemanticIntent SetHistorical(NetSlot building, bool historical)
        {
            return new SemanticIntent { Kind = IntentKind.SetHistorical, Target = building, BoolValue = historical };
        }

        /// <summary>The whole <c>VehicleModel</c> buffer as canonical bytes.</summary>
        public static SemanticIntent SetVehicleModels(NetSlot target, byte[] canonicalBuffer)
        {
            return new SemanticIntent { Kind = IntentKind.SetVehicleModels, Target = target, Blob = canonicalBuffer ?? Array.Empty<byte>() };
        }

        /// <summary>The whole <c>ServiceDistrict</c> buffer as district slots (empty = none).</summary>
        public static SemanticIntent SetServiceDistricts(NetSlot building, IEnumerable<NetSlot> districts)
        {
            return new SemanticIntent { Kind = IntentKind.SetServiceDistricts, Target = building, IntArray = SlotArray(districts, nameof(districts)) };
        }

        /// <summary>The whole palette buffer as canonical bytes; <paramref name="palette"/> may be <see cref="NetSlot.None"/>.</summary>
        public static SemanticIntent EditPalette(NetSlot palette, byte[] canonicalBuffer)
        {
            return new SemanticIntent { Kind = IntentKind.EditPalette, Target = palette, Blob = canonicalBuffer ?? Array.Empty<byte>() };
        }

        public static SemanticIntent Follow(NetSlot citizen)
        {
            return new SemanticIntent { Kind = IntentKind.Follow, Target = citizen };
        }

        public static SemanticIntent Unfollow(NetSlot citizen)
        {
            return new SemanticIntent { Kind = IntentKind.Unfollow, Target = citizen };
        }

        public static SemanticIntent ReplaceObjectPrefab(NetSlot target, ushort baseRevision, PrefabRank newPrefab, int cost)
        {
            return new SemanticIntent { Kind = IntentKind.ReplaceObjectPrefab, Target = target, BaseRevision = baseRevision, Prefab = newPrefab, IntValue = cost };
        }

        /// <summary>True for the defined members of <see cref="IntentKind"/>.</summary>
        public static bool IsDefined(IntentKind kind) => kind >= IntentKind.SetPolicy && kind <= IntentKind.ReplaceObjectPrefab;

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            string text = Kind + (Target.IsNone ? string.Empty : " " + Target);
            if (!Prefab.IsNone) text += " " + Prefab;
            if (IntValue != 0) text += " int " + IntValue.ToString(ci);
            if (FloatValue != 0f) text += " float " + FloatValue.ToString("R", ci);
            if (BoolValue) text += " true";
            if (IntArray != null) text += " [" + IntArray.Length.ToString(ci) + " ints]";
            if (Blob != null) text += " [" + Blob.Length.ToString(ci) + " bytes]";
            return text;
        }

        private static int[] SlotArray(IEnumerable<NetSlot> slots, string name)
        {
            if (slots == null) throw new ArgumentNullException(name);
            var values = new List<int>();
            foreach (NetSlot slot in slots) values.Add((int)slot.Value);
            return values.ToArray();
        }
    }
}
