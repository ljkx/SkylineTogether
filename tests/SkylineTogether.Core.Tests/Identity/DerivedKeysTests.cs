using System;
using System.Collections.Generic;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Identity
{
    /// <summary>Derived-key construction, quantisation, re-encoding and geometric matching (ARCH §3.7, R13 §2.4-2.5, §3.1, §4.3).</summary>
    public sealed class DerivedKeysTests
    {
        private static readonly NetSlot Owner = new NetSlot(42);
        private static readonly NetSlot OtherOwner = new NetSlot(43);
        private static readonly PrefabRank Prefab = new PrefabRank(7);
        private static readonly PrefabRank OtherPrefab = new PrefabRank(8);

        /// <summary>A <c>PathNode</c> search key as the game packs it (<c>Game.Pathfind/PathNode.cs</c>): owner index in the
        /// high 32 bits, curve position (0..32767) in bits 16-30, segment in bits 8-15, lane index in bits 0-7.</summary>
        private static ulong PathNode(int ownerIndexOrSlot, byte lane, byte segment, float curvePosition = 0f, bool secondary = false)
        {
            ulong key = ((ulong)(uint)ownerIndexOrSlot << 32) | ((ulong)(curvePosition * 32767f) << 16) | ((ulong)segment << 8) | lane;
            return secondary ? key | 0x80000000UL : key;
        }

        // ------------------------------------------------------------------ quantisation

        [Test]
        public void PositionsAreQuantisedTo1Over64Metre()
        {
            DerivedKey key = DerivedKeys.Secondary(Owner, Prefab, 1.0f, -2.5f, 1000.015625f);
            Assert.Equal(64, key.QX);
            Assert.Equal(-160, key.QY);
            Assert.Equal(64001, key.QZ, "1000 m + 1/64 m");
            Assert.Equal(DerivedKey.Quantize(0.0078125f), DerivedKeys.Secondary(Owner, Prefab, 0.0078125f, 0, 0).QX, "half a unit uses the shared rounding");
            Assert.Equal(0, DerivedKey.Quantize(0.0078125f), "banker's rounding: 0.5 units round to the even 0");
            Assert.Equal(2, DerivedKey.Quantize(0.0234375f), "1.5 units round to the even 2");

            // tiny float noise (different CPUs) stays inside one quantum
            Assert.Equal(DerivedKeys.Secondary(Owner, Prefab, 123.4567f, 5f, -77.25f), DerivedKeys.Secondary(Owner, Prefab, 123.4567f + 1e-5f, 5f - 1e-5f, -77.25f));
            Assert.NotEqual(DerivedKeys.Secondary(Owner, Prefab, 10f, 0, 0), DerivedKeys.Secondary(Owner, Prefab, 10f + 1f / 64f, 0, 0));
        }

        [Test]
        public void NonFiniteAndHugePositionsAreSafe()
        {
            DerivedKey key = DerivedKeys.Secondary(Owner, Prefab, float.NaN, float.PositiveInfinity, float.NegativeInfinity);
            Assert.Equal(0, key.QX);
            Assert.Equal(int.MaxValue, key.QY);
            Assert.Equal(int.MinValue, key.QZ);
            Assert.Equal(int.MaxValue, DerivedKeys.Secondary(Owner, Prefab, 1e30f, 0, 0).QX);
        }

        // ------------------------------------------------------------------ construction per kind

        [Test]
        public void LaneKeysCarryFlagsAndPathNodes()
        {
            ulong start = PathNode(42, 1, 0), middle = PathNode(42, 5, 1), end = PathNode(42, 1, 2);
            DerivedKey edgeLane = DerivedKeys.Lane(Owner, Prefab, 3, start, middle, end, nodeLane: false);
            Assert.Equal(DerivedKeyKind.Lane, edgeLane.Kind);
            Assert.Equal(Owner, edgeLane.Owner);
            Assert.Equal(Prefab, edgeLane.Prefab);
            Assert.Equal(3, edgeLane.Index);
            Assert.Equal(start, edgeLane.A);
            Assert.Equal(middle, edgeLane.B);
            Assert.Equal(end, edgeLane.C);
            Assert.Equal(0, edgeLane.QX + edgeLane.QY + edgeLane.QZ, "lane keys carry no positions");
            Assert.False(DerivedKeys.IsNodeLane(edgeLane));

            DerivedKey nodeLane = DerivedKeys.Lane(Owner, Prefab, 3, start, middle, end, nodeLane: true);
            Assert.True(DerivedKeys.IsNodeLane(nodeLane));
            Assert.Equal(3 | DerivedKeys.NodeLaneFlag, nodeLane.Index);
            Assert.Equal(0UL, nodeLane.B, "the middle counter of node lanes is not peer-stable and is not stored");
            Assert.Equal(nodeLane, DerivedKeys.Lane(Owner, Prefab, 3, start, PathNode(42, 9, 4), end, nodeLane: true), "node lanes ignore the middle node");
            Assert.NotEqual(edgeLane, nodeLane);
            Assert.False(DerivedKeys.IsNodeLane(DerivedKeys.Secondary(Owner, Prefab, 0, 0, 0)));

            Assert.Throws<ArgumentOutOfRangeException>(() => DerivedKeys.Lane(Owner, Prefab, -1, start, middle, end, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => DerivedKeys.Lane(Owner, Prefab, DerivedKeys.NodeLaneFlag, start, middle, end, false));
            Assert.Throws<ArgumentException>(() => DerivedKeys.Lane(NetSlot.None, Prefab, 0, start, middle, end, false));
        }

        [Test]
        public void SubObjectKeysUseTheAlignedIndexElseTheOwnerSpacePosition()
        {
            DerivedKey aligned = DerivedKeys.SubObject(Owner, Prefab, 4, 1.5f, 2.5f, 3.5f);
            Assert.Equal(DerivedKeyKind.SubObject, aligned.Kind);
            Assert.Equal(4, aligned.Index);
            Assert.Equal(0, aligned.QX);
            Assert.Equal(0, aligned.QY);
            Assert.Equal(0, aligned.QZ);
            Assert.Equal(aligned, DerivedKeys.SubObject(Owner, Prefab, 4, -9f, 0f, 9f), "an aligned sub-object is identified by its index (FindOldSubObject)");
            Assert.Equal(0, DerivedKeys.SubObject(Owner, Prefab, 0, 1, 1, 1).Index, "index 0 is a valid aligned index");

            DerivedKey free = DerivedKeys.SubObject(Owner, Prefab, -1, 1.5f, 2.5f, 3.5f);
            Assert.Equal(-1, free.Index);
            Assert.Equal(96, free.QX);
            Assert.Equal(160, free.QY);
            Assert.Equal(224, free.QZ);
            Assert.Equal(free, DerivedKeys.SubObject(Owner, Prefab, -7, 1.5f, 2.5f, 3.5f), "every negative index means not aligned");
            Assert.NotEqual(free, DerivedKeys.SubObject(Owner, OtherPrefab, -1, 1.5f, 2.5f, 3.5f));
            Assert.NotEqual(free, DerivedKeys.SubObject(OtherOwner, Prefab, -1, 1.5f, 2.5f, 3.5f));
            Assert.Throws<ArgumentException>(() => DerivedKeys.SubObject(NetSlot.None, Prefab, 0, 0, 0, 0));
        }

        [Test]
        public void SecondaryKeysAreCanonical()
        {
            DerivedKey key = DerivedKeys.Secondary(Owner, Prefab, 1, 2, 3);
            Assert.Equal(DerivedKeyKind.SecondaryObject, key.Kind);
            Assert.Equal(-1, key.Index, "unused index");
            Assert.Equal(0UL, key.A | key.B | key.C, "unused payload");
            Assert.Throws<ArgumentException>(() => DerivedKeys.Secondary(NetSlot.None, Prefab, 0, 0, 0));
        }

        [Test]
        public void BlockKeysCarrySideQuantisedPositionAndSize()
        {
            DerivedKey key = DerivedKeys.Block(Owner, -1, 10.25f, -3.5f, 6, 4);
            Assert.Equal(DerivedKeyKind.Block, key.Kind);
            Assert.Equal(Owner, key.Owner);
            Assert.Equal(PrefabRank.None, key.Prefab);
            Assert.Equal(0, key.Index, "negative side");
            Assert.Equal(656, key.QX);
            Assert.Equal(0, key.QY, "blocks are keyed in the ground plane");
            Assert.Equal(-224, key.QZ);
            Assert.Equal(6UL, key.A);
            Assert.Equal(4UL, key.B);
            Assert.Equal(0UL, key.C);
            Assert.Equal(1, DerivedKeys.Block(Owner, 1, 0, 0, 1, 1).Index, "positive side");
            Assert.Equal(1, DerivedKeys.Block(Owner, 5, 0, 0, 1, 1).Index, "any positive value is the positive side");
            Assert.Equal(0, DerivedKeys.Block(Owner, -5, 0, 0, 1, 1).Index);
            Assert.Equal(2, DerivedKeys.Block(Owner, 0, 0, 0, 1, 1).Index, "zero side");
            Assert.Throws<ArgumentOutOfRangeException>(() => DerivedKeys.Block(Owner, 1, 0, 0, -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => DerivedKeys.Block(Owner, 1, 0, 0, 1, -1));
            Assert.Throws<ArgumentException>(() => DerivedKeys.Block(NetSlot.None, 1, 0, 0, 1, 1));
        }

        [Test]
        public void OwnedRouteKeysAreUniquePerOwnerAndPrefab()
        {
            DerivedKey key = DerivedKeys.OwnedRoute(Owner, Prefab);
            Assert.Equal(DerivedKeyKind.OwnedRoute, key.Kind);
            Assert.Equal(-1, key.Index);
            Assert.Equal(key, DerivedKeys.OwnedRoute(Owner, Prefab));
            Assert.NotEqual(key, DerivedKeys.OwnedRoute(OtherOwner, Prefab));
            Assert.NotEqual(key, DerivedKeys.OwnedRoute(Owner, OtherPrefab));
            Assert.Throws<ArgumentException>(() => DerivedKeys.OwnedRoute(NetSlot.None, Prefab));
        }

        [Test]
        public void OwnedRouteElementKeysAreUniquePerOwnerPrefabKindAndIndex()
        {
            var seen = new Dictionary<DerivedKey, string>();
            var owners = new[] { new NetSlot(1), new NetSlot(2), new NetSlot(NetSlot.MaxValue) };
            var prefabs = new[] { new PrefabRank(0), new PrefabRank(1), PrefabRank.None };
            foreach (NetSlot owner in owners)
            {
                foreach (PrefabRank prefab in prefabs)
                {
                    foreach (bool segment in new[] { false, true })
                    {
                        for (int index = 0; index < 6; index++)
                        {
                            DerivedKey key = DerivedKeys.OwnedRouteElement(owner, prefab, segment, index);
                            string identity = owner + "/" + prefab + "/" + (segment ? "segment" : "waypoint") + "/" + index;
                            Assert.False(seen.ContainsKey(key), identity + " collides with " + (seen.ContainsKey(key) ? seen[key] : null));
                            seen.Add(key, identity);
                            Assert.Equal(key, DerivedKeys.OwnedRouteElement(owner, prefab, segment, index), "deterministic");
                            Assert.Equal(DerivedKeyKind.OwnedRouteElement, key.Kind);
                            Assert.Equal(segment ? 1UL : 0UL, key.A);
                            Assert.Equal(index, key.Index);
                        }
                    }
                }
            }
            Assert.NotEqual(DerivedKeys.OwnedRoute(Owner, Prefab), DerivedKeys.OwnedRouteElement(Owner, Prefab, false, 0), "a route and its first waypoint differ");
            Assert.Throws<ArgumentOutOfRangeException>(() => DerivedKeys.OwnedRouteElement(Owner, Prefab, true, -1));
            Assert.Throws<ArgumentException>(() => DerivedKeys.OwnedRouteElement(NetSlot.None, Prefab, true, 0));
        }

        [Test]
        public void FlowEdgeKeysCarryBothOwnersAndTheRole()
        {
            DerivedKey key = DerivedKeys.FlowEdge(Owner, OtherOwner, 2);
            Assert.Equal(DerivedKeyKind.FlowEdge, key.Kind);
            Assert.Equal(Owner, key.Owner);
            Assert.Equal((ulong)OtherOwner.Value, key.A);
            Assert.Equal(2UL, key.B);
            Assert.Equal(PrefabRank.None, key.Prefab);
            Assert.NotEqual(key, DerivedKeys.FlowEdge(OtherOwner, Owner, 2), "direction matters for the exact key");
            Assert.NotEqual(key, DerivedKeys.FlowEdge(Owner, OtherOwner, 3));
            Assert.Equal((ulong)uint.MaxValue, DerivedKeys.FlowEdge(Owner, OtherOwner, -1).B, "negative roles are kept as their 32-bit pattern");
            Assert.Throws<ArgumentException>(() => DerivedKeys.FlowEdge(NetSlot.None, OtherOwner, 0));
            Assert.Throws<ArgumentException>(() => DerivedKeys.FlowEdge(Owner, NetSlot.None, 0));
        }

        [Test]
        public void KeysOfDifferentKindsNeverCollide()
        {
            var keys = new[]
            {
                DerivedKeys.Lane(Owner, Prefab, 0, 0, 0, 0, false),
                DerivedKeys.SubObject(Owner, Prefab, -1, 0, 0, 0),
                DerivedKeys.Secondary(Owner, Prefab, 0, 0, 0),
                DerivedKeys.Block(Owner, 0, 0, 0, 0, 0),
                DerivedKeys.OwnedRoute(Owner, Prefab),
                DerivedKeys.FlowEdge(Owner, Owner, 0),
                DerivedKeys.OwnedRouteElement(Owner, Prefab, false, 0)
            };
            var set = new HashSet<DerivedKey>(keys);
            Assert.Equal(keys.Length, set.Count);
            foreach (DerivedKey key in keys) Assert.True(DerivedKey.IsValidKind((byte)key.Kind), key.ToString());
        }

        // ------------------------------------------------------------------ PathNode re-encoding

        [Test]
        public void PathNodesAreReencodedWithTheOwnerSlot()
        {
            ulong original = PathNode(123456, lane: 7, segment: 2, curvePosition: 0.5f);
            var slots = new Dictionary<int, NetSlot> { { 123456, new NetSlot(42) } };
            ulong wire = DerivedKeys.ReencodePathNode(original, index => slots.TryGetValue(index, out NetSlot s) ? s : NetSlot.None);
            Assert.Equal(PathNode(42, 7, 2, 0.5f), wire, "the owner index in the high 32 bits becomes the slot; the low 32 bits stay");
            Assert.Equal(new NetSlot(42), DerivedKeys.OwnerSlotOf(wire));
            Assert.Equal(original & 0xFFFFFFFFUL, wire & 0xFFFFFFFFUL);

            // the receiving peer maps the slot back to its own owner entity index
            Assert.True(DerivedKeys.TryRestorePathNode(wire, slot => slot == new NetSlot(42) ? 900 : -1, out ulong local));
            Assert.Equal(PathNode(900, 7, 2, 0.5f), local);
            Assert.False(DerivedKeys.TryRestorePathNode(wire, slot => -1, out ulong none), "the owner has no local entity");
            Assert.Equal(0UL, none);
        }

        [Test]
        public void ReencodingKeepsTheSecondaryBitAndHandlesUnboundOwners()
        {
            ulong secondary = PathNode(5, 3, 1, 1.0f, secondary: true);
            ulong wire = DerivedKeys.ReencodePathNode(secondary, index => new NetSlot(77));
            Assert.Equal(0x80000000UL, wire & 0x80000000UL, "the secondary-node bit survives");
            Assert.Equal(PathNode(77, 3, 1, 1.0f, secondary: true), wire);

            ulong unbound = DerivedKeys.ReencodePathNode(PathNode(5, 3, 1), index => NetSlot.None);
            Assert.Equal(0UL, unbound >> 32, "an owner without a slot gives 0 in the high bits");
            Assert.Equal(NetSlot.None, DerivedKeys.OwnerSlotOf(unbound));
            Assert.False(DerivedKeys.TryRestorePathNode(unbound, slot => 1, out _), "slot none never resolves");

            // an owner index with the top bit set reaches the mapping as a negative int, as PathNode.GetOwnerIndex returns it
            int seen = 0;
            DerivedKeys.ReencodePathNode(0xFFFFFFFE_00000000UL, index => { seen = index; return NetSlot.None; });
            Assert.Equal(-2, seen);

            Assert.Equal(NetSlot.None, DerivedKeys.OwnerSlotOf(0x0100_0000_0000_0000UL), "a high part above 24 bits is not a slot");
            Assert.False(DerivedKeys.TryRestorePathNode(0x0100_0000_0000_0000UL, slot => 1, out _));
            Assert.Throws<ArgumentNullException>(() => DerivedKeys.ReencodePathNode(1, null));
            Assert.Throws<ArgumentNullException>(() => DerivedKeys.TryRestorePathNode(1, null, out _));
        }

        // ------------------------------------------------------------------ geometric matching

        [Test]
        public void LanesMatchIgnoringCurvePositionsAndReversedOnlyWhenAllowed()
        {
            ulong a = PathNode(42, 1, 0, 0.25f), b = PathNode(42, 2, 1, 0.5f), c = PathNode(43, 1, 0, 0.75f);
            DerivedKey host = DerivedKeys.Lane(Owner, Prefab, 1, a, b, c, false);
            Assert.True(DerivedKeys.GeometricMatch(host, host, 0, false));

            DerivedKey otherCurvePositions = DerivedKeys.Lane(Owner, Prefab, 1, PathNode(42, 1, 0, 0.3f), PathNode(42, 2, 1, 0.1f), PathNode(43, 1, 0, 0.9f), false);
            Assert.NotEqual(host, otherCurvePositions);
            Assert.True(DerivedKeys.GeometricMatch(host, otherCurvePositions, 0, false), "curve positions are ignored (PathNode.EqualsIgnoreCurvePos)");

            DerivedKey reversed = DerivedKeys.Lane(Owner, Prefab, 1, c, b, a, false);
            Assert.False(DerivedKeys.GeometricMatch(host, reversed, 0, false));
            Assert.True(DerivedKeys.GeometricMatch(host, reversed, 0, true), "a two-way lane generated in the other direction");

            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Lane(Owner, Prefab, 1, a, PathNode(42, 3, 1), c, false), 64, true), "edge lanes must match the middle node");
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Lane(OtherOwner, Prefab, 1, a, b, c, false), 64, true), "owner");
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Lane(Owner, OtherPrefab, 1, a, b, c, false), 64, true), "prefab");
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Lane(Owner, Prefab, 2, a, b, c, false), 64, true), "master/slave flags");
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Lane(Owner, Prefab, 1, PathNode(42, 1, 0, 0.25f, secondary: true), b, c, false), 64, true),
                         "the secondary-node bit is part of the identity");
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Lane(Owner, Prefab, 1, a, b, PathNode(44, 1, 0), false), 64, true), "end owner");
        }

        [Test]
        public void NodeLanesIgnoreTheMiddleNode()
        {
            ulong a = PathNode(42, 1, 0, 0.25f), c = PathNode(43, 4, 0, 0.75f);
            DerivedKey host = DerivedKeys.Lane(Owner, Prefab, 0, a, PathNode(42, 9, 0), c, nodeLane: true);
            DerivedKey client = DerivedKeys.Lane(Owner, Prefab, 0, PathNode(42, 1, 0, 0.5f), PathNode(42, 3, 0), PathNode(43, 4, 0, 0.5f), nodeLane: true);
            Assert.True(DerivedKeys.GeometricMatch(host, client, 0, false));
            DerivedKey clientReversed = DerivedKeys.Lane(Owner, Prefab, 0, c, PathNode(42, 3, 0), a, nodeLane: true);
            Assert.False(DerivedKeys.GeometricMatch(host, clientReversed, 0, false));
            Assert.True(DerivedKeys.GeometricMatch(host, clientReversed, 0, true), "a two-way node lane created from the other owner (R13 §2.4)");
            DerivedKey edgeLane = DerivedKeys.Lane(Owner, Prefab, 0, a, PathNode(42, 9, 0), c, nodeLane: false);
            Assert.True(DerivedKeys.GeometricMatch(host, edgeLane, 0, false), "one node-lane side is enough to skip the middle node");
        }

        [Test]
        public void ObjectKeysMatchWithinTheTolerance()
        {
            DerivedKey host = DerivedKeys.Secondary(Owner, Prefab, 10f, 0f, 20f);
            DerivedKey near = DerivedKeys.Secondary(Owner, Prefab, 10.9f, 0.5f, 19.2f);
            DerivedKey far = DerivedKeys.Secondary(Owner, Prefab, 11.1f, 0f, 20f);
            Assert.True(DerivedKeys.GeometricMatch(host, near, DerivedKeys.DefaultToleranceQ, false));
            Assert.False(DerivedKeys.GeometricMatch(host, far, DerivedKeys.DefaultToleranceQ, false), "beyond 1 m");
            Assert.True(DerivedKeys.GeometricMatch(host, far, 2 * DerivedKey.UnitsPerMetre, false));
            Assert.False(DerivedKeys.GeometricMatch(host, near, 0, false), "tolerance 0 is exact");
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Secondary(Owner, OtherPrefab, 10f, 0f, 20f), 1000, false));
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Secondary(OtherOwner, Prefab, 10f, 0f, 20f), 1000, false));

            DerivedKey free = DerivedKeys.SubObject(Owner, Prefab, -1, 1f, 2f, 3f);
            Assert.True(DerivedKeys.GeometricMatch(free, DerivedKeys.SubObject(Owner, Prefab, -1, 1.5f, 2f, 2.6f), 64, false), "nearest position within tolerance");
            Assert.False(DerivedKeys.GeometricMatch(free, DerivedKeys.SubObject(Owner, Prefab, -1, 1f, 3.5f, 3f), 64, false));
            DerivedKey aligned = DerivedKeys.SubObject(Owner, Prefab, 3, 0, 0, 0);
            Assert.True(DerivedKeys.GeometricMatch(aligned, DerivedKeys.SubObject(Owner, Prefab, 3, 0, 0, 0), 0, false));
            Assert.False(DerivedKeys.GeometricMatch(aligned, DerivedKeys.SubObject(Owner, Prefab, 4, 0, 0, 0), 1000, false), "aligned indices match exactly");
            Assert.False(DerivedKeys.GeometricMatch(aligned, free, 1000, false), "aligned and unaligned never match");
        }

        [Test]
        public void BlocksMatchOnTheSameSideAndSizeWithinTheTolerance()
        {
            DerivedKey host = DerivedKeys.Block(Owner, 1, 100f, 50f, 6, 4);
            Assert.True(DerivedKeys.GeometricMatch(host, DerivedKeys.Block(Owner, 1, 100.6f, 49.5f, 6, 4), DerivedKeys.DefaultToleranceQ, false));
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Block(Owner, 1, 101.5f, 50f, 6, 4), DerivedKeys.DefaultToleranceQ, false), "beyond 1 m");
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Block(Owner, -1, 100f, 50f, 6, 4), 1000, false), "other side");
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Block(Owner, 1, 100f, 50f, 5, 4), 1000, false), "other size");
            Assert.False(DerivedKeys.GeometricMatch(host, DerivedKeys.Block(OtherOwner, 1, 100f, 50f, 6, 4), 1000, false), "other edge");
        }

        [Test]
        public void RoutesMatchExactlyAndFlowEdgesMayBeReversed()
        {
            DerivedKey route = DerivedKeys.OwnedRoute(Owner, Prefab);
            Assert.True(DerivedKeys.GeometricMatch(route, DerivedKeys.OwnedRoute(Owner, Prefab), 1000, true));
            Assert.False(DerivedKeys.GeometricMatch(route, DerivedKeys.OwnedRoute(Owner, OtherPrefab), 1000, true));
            DerivedKey element = DerivedKeys.OwnedRouteElement(Owner, Prefab, true, 1);
            Assert.True(DerivedKeys.GeometricMatch(element, DerivedKeys.OwnedRouteElement(Owner, Prefab, true, 1), 0, false));
            Assert.False(DerivedKeys.GeometricMatch(element, DerivedKeys.OwnedRouteElement(Owner, Prefab, true, 0), 1000, true));
            Assert.False(DerivedKeys.GeometricMatch(element, DerivedKeys.OwnedRouteElement(Owner, Prefab, false, 1), 1000, true));

            DerivedKey flow = DerivedKeys.FlowEdge(Owner, OtherOwner, 1);
            DerivedKey swapped = DerivedKeys.FlowEdge(OtherOwner, Owner, 1);
            Assert.True(DerivedKeys.GeometricMatch(flow, DerivedKeys.FlowEdge(Owner, OtherOwner, 1), 0, false));
            Assert.False(DerivedKeys.GeometricMatch(flow, swapped, 0, false));
            Assert.True(DerivedKeys.GeometricMatch(flow, swapped, 0, true));
            Assert.False(DerivedKeys.GeometricMatch(flow, DerivedKeys.FlowEdge(OtherOwner, Owner, 2), 0, true), "the role must match");
        }

        [Test]
        public void DifferentKindsNeverMatchAndToleranceMustNotBeNegative()
        {
            DerivedKey secondary = DerivedKeys.Secondary(Owner, Prefab, 0, 0, 0);
            DerivedKey sub = DerivedKeys.SubObject(Owner, Prefab, -1, 0, 0, 0);
            Assert.False(DerivedKeys.GeometricMatch(secondary, sub, 1000, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => DerivedKeys.GeometricMatch(secondary, secondary, -1, false));
            Assert.False(DerivedKeys.GeometricMatch(default, secondary, 0, false), "a default key matches nothing real");
        }

        [Test]
        public void ExtremeCoordinatesDoNotOverflowTheTolerance()
        {
            DerivedKey low = DerivedKeys.Secondary(Owner, Prefab, float.NegativeInfinity, 0, 0);
            DerivedKey high = DerivedKeys.Secondary(Owner, Prefab, float.PositiveInfinity, 0, 0);
            Assert.False(DerivedKeys.GeometricMatch(low, high, int.MaxValue, false), "the distance is computed in 64 bits");
            Assert.True(DerivedKeys.GeometricMatch(low, low, 0, false));
        }
    }
}
