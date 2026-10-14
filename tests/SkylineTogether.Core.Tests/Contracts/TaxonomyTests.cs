using System;
using System.Collections.Generic;
using System.Reflection;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>
    /// Pins the numeric values of every enum that travels on the wire, is stored in side tables or feeds a fingerprint:
    /// changing one would break compatibility between builds (IMPLEMENTATION-PLAN §3 rule 12).
    /// </summary>
    public sealed class TaxonomyTests
    {
        private static void Pin<TEnum>(params (TEnum value, int expected)[] pins) where TEnum : struct, Enum
        {
            foreach ((TEnum value, int expected) in pins)
            {
                Assert.Equal(expected, Convert.ToInt32(value), typeof(TEnum).Name + "." + value);
            }
        }

        private static void SequentialFrom<TEnum>(int first, params string[] names) where TEnum : struct, Enum
        {
            Assert.SequenceEqual(names, Enum.GetNames(typeof(TEnum)), typeof(TEnum).Name + " members");
            for (int i = 0; i < names.Length; i++)
            {
                var value = (TEnum)Enum.Parse(typeof(TEnum), names[i]);
                Assert.Equal(first + i, Convert.ToInt32(value), typeof(TEnum).Name + "." + names[i]);
            }
        }

        [Test]
        public void ReplicationClassOrder()
        {
            SequentialFrom<ReplicationClass>(0, "Excluded", "Derived", "S", "G", "V", "A", "ALow", "M", "K", "Channel", "OnDemand");
        }

        [Test]
        public void FamilyOrder()
        {
            SequentialFrom<Family>(0, "ToolLocal", "Prefab", "Lane", "ZoneBlock", "Icon", "SecondaryObject", "PrefabSubObject", "FlowGraph",
                                   "OwnedRoute", "NetNode", "NetEdge", "Aggregate", "Mover", "Object", "Area", "Route", "Waypoint", "Segment",
                                   "Agent", "TransientAgent", "Event", "Statistic", "Singleton", "WaterSource", "ModEntity", "Generic");
        }

        [Test]
        public void MessageKindsStartAtOne()
        {
            string[] sequential =
            {
                "Hello", "Welcome", "Refuse", "Control", "Epoch", "Ack", "Proposal", "Motion", "MotionControl", "Clock",
                "View", "Presence", "HashDetail", "RepairRequest", "BlobManifest", "BlobChunk", "BlobHave", "Ping", "Chat",
                "Terrain", "Water", // feat/terrain
            };
            for (int i = 0; i < sequential.Length; i++)
            {
                Assert.Equal(1 + i, Convert.ToInt32((MessageKind)Enum.Parse(typeof(MessageKind), sequential[i])), "MessageKind." + sequential[i]);
            }
            // branch-owned ranges (merge coordination): w2/ids owns 60-64, w2/simvals owns 65-69
            Pin((MessageKind.ApplyAck, 60), (MessageKind.SimValues, 65), (MessageKind.CellMaps, 66), (MessageKind.Statistics, 67));
            // w3/agents owns 90-94
            Pin((MessageKind.AgentValues, 90), (MessageKind.AgentRefresh, 91));
            // w4/flow owns 100-104
            Pin((MessageKind.FlowStatus, 100));
            // w8/mods owns 110-114
            Pin((MessageKind.ModChannel, 110));
            Assert.Equal(sequential.Length + 8, Enum.GetNames(typeof(MessageKind)).Length, "every MessageKind is pinned");
        }

        [Test]
        public void SectionTypesStartAtOne()
        {
            SequentialFrom<SectionType>(1, "Tx", "Values", "SysState", "PrefabState", "Icons", "Events", "Names", "CellMaps", "Clock", "Hash",
                                        "Session", "Inspection", "TypeSets");
        }

        [Test]
        public void RejectReasonsAreStable()
        {
            SequentialFrom<RejectReason>(0, "None", "Conflict", "NoPermission", "NotEnoughMoney", "Locked", "OutsideTiles", "Invalid", "RateLimited",
                                         "NotLive", "HostError", "Timeout", "HostOnly", "PrefabClass", "Geometry");
            Assert.Equal(typeof(ushort), Enum.GetUnderlyingType(typeof(RejectReason)));
        }

        [Test]
        public void SmallEnumsAreStable()
        {
            Pin((Priority.P0, 0), (Priority.P1, 1), (Priority.P2, 2), (Priority.P3, 3), (Priority.Bulk, 4));
            Pin((Lane.Reliable, 0), (Lane.Unreliable, 1), (Lane.Bulk, 2));
            Pin((Delivery.ReliableOrdered, 0), (Delivery.Unreliable, 1));
            SequentialFrom<NetRefKind>(0, "Null", "Local", "Slot", "Prefab", "Provisional", "Derived");
            SequentialFrom<SessionRole>(0, "Host", "CoHost", "Builder", "Viewer");
            SequentialFrom<ObserveSource>(0, "Spawn", "Edc", "Sdc", "HotSet", "Motion", "Coalesced", "Repair", "Commit", "Prime", "CoCapture", "PathResult");
            SequentialFrom<CompressionKind>(0, "None", "ManagedLz4", "Zstd", "Lz4");
            SequentialFrom<ThreadKind>(0, "Main", "Network", "Worker", "Any");
            SequentialFrom<LogLevel>(0, "Trace", "Debug", "Info", "Warn", "Error", "Critical");
        }

        [Test]
        public void FlagEnumsHaveTheirBitValues()
        {
            Pin((TouchFlags.None, 0), (TouchFlags.Updated, 1), (TouchFlags.BatchesUpdated, 2), (TouchFlags.PathfindUpdated, 4), (TouchFlags.EffectsUpdated, 8));
            Pin((SpawnFlags.None, 0), (SpawnFlags.Mover, 1), (SpawnFlags.HasUpdateFrame, 2), (SpawnFlags.Repair, 4));
            Assert.Equal(2097152u, (uint)AttentionTopic.Statistics);
            Assert.Equal(1048576u, (uint)AttentionTopic.Transport);
            Assert.Equal(1u, (uint)AttentionTopic.Happiness);
        }

        [Test]
        public void EveryFlagEnumMemberIsASingleBit()
        {
            foreach (Type type in new[] { typeof(TouchFlags), typeof(SpawnFlags), typeof(AttentionTopic), typeof(Permission) })
            {
                Assert.NotNull(type.GetCustomAttribute<FlagsAttribute>(), type.Name + " is [Flags]");
                foreach (object value in Enum.GetValues(type))
                {
                    ulong bits = Convert.ToUInt64(value);
                    string name = Enum.GetName(type, value);
                    if (name == "None" || name == "All") continue;
                    Assert.True(bits != 0 && (bits & (bits - 1)) == 0, type.Name + "." + name + " is one bit");
                }
            }
        }

        [Test]
        public void PermissionAllIsEveryFlag()
        {
            Permission union = Permission.None;
            foreach (Permission p in Enum.GetValues(typeof(Permission)))
            {
                if (p != Permission.All) union |= p;
            }
            Assert.Equal(Permission.All, union);
            Assert.Equal(4095u, (uint)Permission.All);
        }

        [Test]
        public void ThreadAffinityAttributeCarriesItsKind()
        {
            var attribute = new ThreadAffinityAttribute(ThreadKind.Network);
            Assert.Equal(ThreadKind.Network, attribute.Kind);
            var usage = typeof(ThreadAffinityAttribute).GetCustomAttribute<AttributeUsageAttribute>();
            Assert.Equal(AttributeTargets.All, usage.ValidOn);
            var windowUsage = typeof(CaptureWindowAttribute).GetCustomAttribute<AttributeUsageAttribute>();
            Assert.Equal(AttributeTargets.Method, windowUsage.ValidOn);
            Assert.Equal(ThreadKind.Any, typeof(ILog).GetCustomAttribute<ThreadAffinityAttribute>().Kind);
            MethodInfo bind = typeof(IHostReplicationEngine).GetMethod("Bind");
            Assert.NotNull(bind.GetCustomAttribute<CaptureWindowAttribute>(), "Bind is a capture-window member");
            MethodInfo poll = typeof(ITransport).GetMethod("Poll");
            Assert.Equal(ThreadKind.Network, poll.GetCustomAttribute<ThreadAffinityAttribute>().Kind);
        }
    }
}
