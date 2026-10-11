using System;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// Construction and comparison of derived keys (ARCH §3.7): the portable identity of entities every peer
    /// regenerates (lanes, sub-objects, secondary objects, zone blocks, owned routes and their elements, flow edges).
    /// These constructors are the authority on how a kind fills the fields of <see cref="DerivedKey"/>; unused fields are
    /// canonical (<see cref="PrefabRank.None"/>, <c>Index = -1</c>, zero positions and payloads), so equal identities
    /// give equal keys on every peer. Positions are quantised to 1/64 m (<see cref="DerivedKey.Quantize"/>), because
    /// Burst float results may differ between CPUs (R13 §13).
    /// <list type="table">
    /// <item><term>Lane</term><description>Owner, Prefab = lane prefab, Index = master/slave flags (+ <see cref="NodeLaneFlag"/>),
    /// A/B/C = start/middle/end <c>PathNode</c> with the owner index re-encoded as a slot (<see cref="ReencodePathNode"/>);
    /// node lanes store B = 0, because their middle counter is not peer-stable (R13 §2.4).</description></item>
    /// <item><term>SubObject</term><description>Owner, Prefab, Index = <c>Aligned.m_SubObjectIndex</c>; positions (owner
    /// space) only when not aligned, mirroring <c>FindOldSubObject</c> (aligned index first, else nearest position; R13 §3.1).</description></item>
    /// <item><term>SecondaryObject</term><description>Owner, Prefab, quantised position.</description></item>
    /// <item><term>Block</term><description>Owner = edge, Index = side (0 for a negative side, 1 positive, 2 zero),
    /// QX/QZ = quantised <c>m_Position</c> x/z, A/B = <c>m_Size</c> x/y (R13 §4.3).</description></item>
    /// <item><term>OwnedRoute</term><description>Owner, Prefab = route prefab (unique per owner).</description></item>
    /// <item><term>OwnedRouteElement</term><description>Owner, Prefab = route prefab, Index = element index, A = 0
    /// waypoint, 1 segment: unique per (owner, prefab, kind, index).</description></item>
    /// <item><term>FlowEdge</term><description>Owner = owner of the start node, A = owner of the end node (slot value),
    /// B = role.</description></item>
    /// </list>
    /// Pure functions; any thread.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class DerivedKeys
    {
        /// <summary>Bit set in a lane key's <see cref="DerivedKey.Index"/> for node (intersection) lanes.</summary>
        public const int NodeLaneFlag = 1 << 30;

        /// <summary>Default tolerance of geometric matches: 1 m (64 units of 1/64 m), the zone-block fallback of R13 §4.3.</summary>
        public const int DefaultToleranceQ = DerivedKey.UnitsPerMetre;

        /// <summary>The bits of a <c>PathNode</c> that hold the curve position (<c>Game.Pathfind/PathNode.cs</c>: bits 16-30).</summary>
        private const ulong PathNodeCurvePositionBits = 0x7FFF0000UL;

        /// <summary>
        /// A lane key. <paramref name="start"/>, <paramref name="middle"/> and <paramref name="end"/> are the lane's
        /// <c>PathNode</c> search keys already re-encoded with <see cref="ReencodePathNode"/>. For a node lane
        /// (<paramref name="nodeLane"/>) the middle node is not stored and <see cref="NodeLaneFlag"/> is set.
        /// <paramref name="masterSlaveFlags"/> = <c>(int)(laneFlags &amp; (LaneFlags.Slave | LaneFlags.Master))</c>, the
        /// flags vanilla's own lane identity compares (<c>LaneSystem.LaneKey</c>, <c>Game.Net/LaneSystem.cs:40-45</c>);
        /// the prefab <c>LaneFlags</c> end at bit 26, so they never reach <see cref="NodeLaneFlag"/>.
        /// </summary>
        public static DerivedKey Lane(NetSlot owner, PrefabRank lanePrefab, int masterSlaveFlags, ulong start, ulong middle, ulong end, bool nodeLane)
        {
            RequireOwner(owner, nameof(owner));
            if (masterSlaveFlags < 0 || (masterSlaveFlags & NodeLaneFlag) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(masterSlaveFlags), masterSlaveFlags, "lane flags must be non-negative and below 2^30");
            }
            int index = nodeLane ? masterSlaveFlags | NodeLaneFlag : masterSlaveFlags;
            return new DerivedKey(DerivedKeyKind.Lane, owner, lanePrefab, index, 0, 0, 0, start, nodeLane ? 0UL : middle, end);
        }

        /// <summary>
        /// A prefab sub-object key. With an aligned index (≥ 0) the position is not part of the key; without one
        /// (any negative index) the position in owner space identifies the sub-object.
        /// </summary>
        public static DerivedKey SubObject(NetSlot owner, PrefabRank prefab, int alignedIndex, float localX, float localY, float localZ)
        {
            RequireOwner(owner, nameof(owner));
            if (alignedIndex >= 0)
            {
                return new DerivedKey(DerivedKeyKind.SubObject, owner, prefab, alignedIndex, 0, 0, 0, 0, 0, 0);
            }
            return new DerivedKey(DerivedKeyKind.SubObject, owner, prefab, -1,
                                  DerivedKey.Quantize(localX), DerivedKey.Quantize(localY), DerivedKey.Quantize(localZ), 0, 0, 0);
        }

        /// <summary>A secondary-object key (owner, prefab, quantised position).</summary>
        public static DerivedKey Secondary(NetSlot owner, PrefabRank prefab, float x, float y, float z)
        {
            RequireOwner(owner, nameof(owner));
            return new DerivedKey(DerivedKeyKind.SecondaryObject, owner, prefab, -1,
                                  DerivedKey.Quantize(x), DerivedKey.Quantize(y), DerivedKey.Quantize(z), 0, 0, 0);
        }

        /// <summary>
        /// A zone-block key: owner edge, side (<c>sign(dot(m_Direction, edge right))</c>, stored as 0 negative,
        /// 1 positive, 2 zero), quantised <c>m_Position</c> x/z and <c>m_Size</c> (cells, not negative).
        /// </summary>
        public static DerivedKey Block(NetSlot edge, int side, float positionX, float positionZ, int sizeX, int sizeY)
        {
            RequireOwner(edge, nameof(edge));
            if (sizeX < 0) throw new ArgumentOutOfRangeException(nameof(sizeX), sizeX, "a block size is not negative");
            if (sizeY < 0) throw new ArgumentOutOfRangeException(nameof(sizeY), sizeY, "a block size is not negative");
            int canonicalSide = side < 0 ? 0 : side > 0 ? 1 : 2;
            return new DerivedKey(DerivedKeyKind.Block, edge, PrefabRank.None, canonicalSide,
                                  DerivedKey.Quantize(positionX), 0, DerivedKey.Quantize(positionZ), (ulong)sizeX, (ulong)sizeY, 0);
        }

        /// <summary>An owned-route key (owner slot, route prefab; unique per owner).</summary>
        public static DerivedKey OwnedRoute(NetSlot owner, PrefabRank prefab)
        {
            RequireOwner(owner, nameof(owner));
            return new DerivedKey(DerivedKeyKind.OwnedRoute, owner, prefab, -1, 0, 0, 0, 0, 0, 0);
        }

        /// <summary>
        /// A key of a waypoint (<paramref name="segment"/> false) or segment of an owned route, by its index in the
        /// route's <c>RouteWaypoint</c>/<c>RouteSegment</c> buffer (gate-bypass path state, ARCH §3.7, §4.11.3).
        /// </summary>
        public static DerivedKey OwnedRouteElement(NetSlot owner, PrefabRank routePrefab, bool segment, int index)
        {
            RequireOwner(owner, nameof(owner));
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), index, "an element index is not negative");
            return new DerivedKey(DerivedKeyKind.OwnedRouteElement, owner, routePrefab, index, 0, 0, 0, segment ? 1UL : 0UL, 0, 0);
        }

        /// <summary>A flow-edge key: the owners of the start and end flow nodes and the edge's role (ARCH §4.11.5).</summary>
        public static DerivedKey FlowEdge(NetSlot startOwner, NetSlot endOwner, int role)
        {
            RequireOwner(startOwner, nameof(startOwner));
            RequireOwner(endOwner, nameof(endOwner));
            return new DerivedKey(DerivedKeyKind.FlowEdge, startOwner, PrefabRank.None, -1, 0, 0, 0, endOwner.Value, unchecked((uint)role), 0);
        }

        /// <summary>
        /// Re-encodes a <c>PathNode</c> search key for the wire: the owner entity index in the high 32 bits
        /// (<c>owner.Index &lt;&lt; 32 | curvePos &lt;&lt; 16 | segment &lt;&lt; 8 | laneIndex</c>, <c>Game.Pathfind/PathNode.cs:20-49</c>)
        /// is replaced by the owner's slot from <paramref name="ownerIndexToSlot"/>; the low 32 bits stay (R13 §2.5). An
        /// owner without a slot gives 0 in the high bits (<see cref="OwnerSlotOf"/> returns <see cref="NetSlot.None"/>).
        /// </summary>
        public static ulong ReencodePathNode(ulong pathNodeKey, Func<int, NetSlot> ownerIndexToSlot)
        {
            if (ownerIndexToSlot == null) throw new ArgumentNullException(nameof(ownerIndexToSlot));
            int ownerIndex = unchecked((int)(pathNodeKey >> 32));
            NetSlot slot = ownerIndexToSlot(ownerIndex);
            return ((ulong)slot.Value << 32) | (pathNodeKey & 0xFFFFFFFFUL);
        }

        /// <summary>
        /// The inverse of <see cref="ReencodePathNode"/> on the receiving peer: the slot in the high 32 bits is replaced
        /// by the local owner entity index from <paramref name="slotToOwnerIndex"/>. False (and 0) when the slot is none,
        /// out of range or has no local entity (a negative index).
        /// </summary>
        public static bool TryRestorePathNode(ulong reencodedKey, Func<NetSlot, int> slotToOwnerIndex, out ulong pathNodeKey)
        {
            if (slotToOwnerIndex == null) throw new ArgumentNullException(nameof(slotToOwnerIndex));
            NetSlot slot = OwnerSlotOf(reencodedKey);
            if (!slot.IsNone)
            {
                int ownerIndex = slotToOwnerIndex(slot);
                if (ownerIndex >= 0)
                {
                    pathNodeKey = ((ulong)(uint)ownerIndex << 32) | (reencodedKey & 0xFFFFFFFFUL);
                    return true;
                }
            }
            pathNodeKey = 0;
            return false;
        }

        /// <summary>The owner slot of a re-encoded <c>PathNode</c>; <see cref="NetSlot.None"/> for 0 or an invalid value.</summary>
        public static NetSlot OwnerSlotOf(ulong reencodedKey)
        {
            return NetSlot.TryCreate((uint)(reencodedKey >> 32), out NetSlot slot) ? slot : NetSlot.None;
        }

        /// <summary>True for a lane key built for a node (intersection) lane.</summary>
        public static bool IsNodeLane(in DerivedKey key) => key.Kind == DerivedKeyKind.Lane && (key.Index & NodeLaneFlag) != 0;

        /// <summary>
        /// The relaxed match a resolver falls back to when no exact key exists (ARCH §3.7): same kind and owner, then per
        /// kind —
        /// <b>Lane</b>: same prefab and flags; start and end path nodes equal ignoring their curve-position bits (as
        /// <c>PathNode.EqualsIgnoreCurvePos</c>), or swapped when <paramref name="allowReversed"/> (a two-way node lane
        /// generated in the other direction, R13 §2.4); the middle node must also match unless either key is a node lane;
        /// positions are not part of lane keys, so the tolerance does not apply (curve end points are compared by the game
        /// layer). <b>SubObject</b>: same prefab; equal aligned indices, or both unaligned with every coordinate within
        /// <paramref name="toleranceQ"/>. <b>SecondaryObject</b>: same prefab, coordinates within tolerance.
        /// <b>Block</b>: same side and size, x/z within tolerance. <b>OwnedRoute</b>, <b>OwnedRouteElement</b>: exact.
        /// <b>FlowEdge</b>: same role and owners, or swapped owners when <paramref name="allowReversed"/>.
        /// <paramref name="toleranceQ"/> is in 1/64 m (64 = 1 m) and must not be negative.
        /// </summary>
        public static bool GeometricMatch(in DerivedKey a, in DerivedKey b, int toleranceQ, bool allowReversed)
        {
            if (toleranceQ < 0) throw new ArgumentOutOfRangeException(nameof(toleranceQ), toleranceQ, "the tolerance must not be negative");
            if (a.Kind != b.Kind) return false;
            switch (a.Kind)
            {
                case DerivedKeyKind.Lane:
                {
                    if (a.Owner != b.Owner || a.Prefab != b.Prefab) return false;
                    if ((a.Index & ~NodeLaneFlag) != (b.Index & ~NodeLaneFlag)) return false;
                    bool nodeLane = ((a.Index | b.Index) & NodeLaneFlag) != 0;
                    if (!nodeLane && !SamePathNode(a.B, b.B)) return false;
                    if (SamePathNode(a.A, b.A) && SamePathNode(a.C, b.C)) return true;
                    return allowReversed && SamePathNode(a.A, b.C) && SamePathNode(a.C, b.A);
                }
                case DerivedKeyKind.SubObject:
                    if (a.Owner != b.Owner || a.Prefab != b.Prefab) return false;
                    if (a.Index >= 0 || b.Index >= 0) return a.Index == b.Index;
                    return Within(a.QX, b.QX, toleranceQ) && Within(a.QY, b.QY, toleranceQ) && Within(a.QZ, b.QZ, toleranceQ);
                case DerivedKeyKind.SecondaryObject:
                    return a.Owner == b.Owner && a.Prefab == b.Prefab
                           && Within(a.QX, b.QX, toleranceQ) && Within(a.QY, b.QY, toleranceQ) && Within(a.QZ, b.QZ, toleranceQ);
                case DerivedKeyKind.Block:
                    return a.Owner == b.Owner && a.Index == b.Index && a.A == b.A && a.B == b.B
                           && Within(a.QX, b.QX, toleranceQ) && Within(a.QZ, b.QZ, toleranceQ);
                case DerivedKeyKind.FlowEdge:
                    if (a.B != b.B) return false;
                    if (a.Owner.Value == b.Owner.Value && a.A == b.A) return true;
                    return allowReversed && a.Owner.Value == b.A && a.A == b.Owner.Value;
                default:
                    return a.Equals(b);
            }
        }

        private static bool SamePathNode(ulong a, ulong b) => ((a ^ b) & ~PathNodeCurvePositionBits) == 0;

        private static bool Within(int a, int b, int tolerance) => Math.Abs((long)a - b) <= tolerance;

        private static void RequireOwner(NetSlot owner, string parameter)
        {
            if (owner.IsNone) throw new ArgumentException("a derived key needs a bound owner slot", parameter);
        }
    }
}
