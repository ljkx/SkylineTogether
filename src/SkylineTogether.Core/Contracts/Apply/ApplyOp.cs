using System.Globalization;
using System.Text;

namespace SkylineTogether.Core
{
    /// <summary>
    /// One operation of an <see cref="ApplyProgram"/>; which fields an op uses is documented on <see cref="ApplyOpKind"/>.
    /// Use <see cref="Create"/> to start from the neutral defaults (GroupIndex -1, Prefab none).
    /// </summary>
    public struct ApplyOp
    {
        public ApplyOpKind Kind;
        public NetSlot Slot;
        public byte Generation;
        public Family Family;
        public PrefabRank Prefab;
        public TypeSetId TypeSet;
        /// <summary>Creation group of CreateGroup, Bind and Finalize; -1 when unused.</summary>
        public int GroupIndex;
        /// <summary>Number of entities of a CreateGroup.</summary>
        public int Count;
        public TypeRank Type;
        /// <summary>Canonical bytes in the source plan's arena (WriteValue, Park, Hold).</summary>
        public PayloadRef Payload;
        /// <summary>SetEnabled: the enabled value; CreateGroup and Finalize: the group consists of movers.</summary>
        public bool Flag;
        public TouchFlags Tags;
        /// <summary>The host <c>UpdateFrame</c> of a mover group.</summary>
        public byte UpdateFrame;
        /// <summary>Park: the missing slot; Bind with GroupIndex &lt; 0: the prediction's <c>LocalId</c>; SynthesizeEvent: the event kind.</summary>
        public uint Aux;

        /// <summary>An op of the given kind with neutral defaults: GroupIndex -1, <see cref="PrefabRank.None"/>, everything else zero.</summary>
        public static ApplyOp Create(ApplyOpKind kind)
        {
            return new ApplyOp { Kind = kind, GroupIndex = -1, Prefab = PrefabRank.None };
        }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(Kind.ToString());
            if (!Slot.IsNone) sb.Append(' ').Append(Slot).Append("/g").Append(Generation.ToString(ci));
            if (GroupIndex >= 0) sb.Append(" group=").Append(GroupIndex.ToString(ci));
            switch (Kind)
            {
                case ApplyOpKind.CreateGroup:
                    sb.Append(" n=").Append(Count.ToString(ci)).Append(' ').Append(Family).Append(' ').Append(Prefab).Append(' ').Append(TypeSet);
                    if (Flag) sb.Append(" movers uf=").Append(UpdateFrame.ToString(ci));
                    break;
                case ApplyOpKind.Bind:
                    sb.Append(' ').Append(Family).Append(' ').Append(Prefab).Append(' ').Append(TypeSet);
                    if (GroupIndex < 0) sb.Append(" local:").Append(Aux.ToString(ci));
                    break;
                case ApplyOpKind.ReconcileSignature:
                    sb.Append(' ').Append(TypeSet);
                    break;
                case ApplyOpKind.WriteValue:
                case ApplyOpKind.Hold:
                    sb.Append(' ').Append(Type).Append(' ').Append(Payload);
                    break;
                case ApplyOpKind.Park:
                    sb.Append(' ').Append(Type).Append(' ').Append(Payload).Append(" missing=").Append(Aux.ToString(ci));
                    break;
                case ApplyOpKind.SetEnabled:
                    sb.Append(' ').Append(Type).Append(Flag ? " on" : " off");
                    break;
                case ApplyOpKind.AddTags:
                case ApplyOpKind.ClosureTag:
                    sb.Append(' ').Append(Tags);
                    break;
                case ApplyOpKind.Finalize:
                    if (Flag) sb.Append(" movers");
                    break;
                case ApplyOpKind.SynthesizeEvent:
                    sb.Append(' ').Append(((SynthesizedEventKind)Aux).ToString());
                    break;
            }
            return sb.ToString();
        }
    }
}
