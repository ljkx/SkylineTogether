using System;
using System.Globalization;
using System.Collections.Generic;
using System.Reflection;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>
    /// Pins the numeric values of every enum that travels on the wire, is stored in side tables or feeds a fingerprint:
    /// changing one would break compatibility between builds (ARCHITECTURE.md#code-rules).
    /// </summary>
    public sealed class TaxonomyTests
    {
        private static void Pin<TEnum>(params (TEnum value, int expected)[] pins) where TEnum : struct, Enum
        {
            foreach ((TEnum value, int expected) in pins)
            {
                Assert.Equal(expected, Convert.ToInt32(value, CultureInfo.InvariantCulture), typeof(TEnum).Name + "." + value);
            }
        }

        private static void SequentialFrom<TEnum>(int first, params string[] names) where TEnum : struct, Enum
        {
            Assert.SequenceEqual(names, Enum.GetNames(typeof(TEnum)), typeof(TEnum).Name + " members");
            for (int i = 0; i < names.Length; i++)
            {
                var value = (TEnum)Enum.Parse(typeof(TEnum), names[i]);
                Assert.Equal(first + i, Convert.ToInt32(value, CultureInfo.InvariantCulture), typeof(TEnum).Name + "." + names[i]);
            }
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
                "Terrain", "Water",
            };
            for (int i = 0; i < sequential.Length; i++)
            {
                Assert.Equal(1 + i, Convert.ToInt32((MessageKind)Enum.Parse(typeof(MessageKind), sequential[i]), CultureInfo.InvariantCulture), "MessageKind." + sequential[i]);
            }
            // reserved ranges: 60-64 network id reuse, 65-69 simulation values
            Pin((MessageKind.ApplyAck, 60), (MessageKind.SimValues, 65), (MessageKind.CellMaps, 66), (MessageKind.Statistics, 67));
            // 90-94 agents
            Pin((MessageKind.AgentValues, 90), (MessageKind.AgentRefresh, 91));
            // 100-104 utility flow status
            Pin((MessageKind.FlowStatus, 100));
            // 110-114 mod integration
            Pin((MessageKind.ModChannel, 110));
            Assert.Equal(sequential.Length + 8, Enum.GetNames(typeof(MessageKind)).Length, "every MessageKind is pinned");
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
            SequentialFrom<SessionRole>(0, "Host", "CoHost", "Builder", "Viewer");
            SequentialFrom<CompressionKind>(0, "None", "ManagedLz4", "Zstd", "Lz4");
            SequentialFrom<ThreadKind>(0, "Main", "Network", "Worker", "Any");
            SequentialFrom<LogLevel>(0, "Trace", "Debug", "Info", "Warn", "Error", "Critical");
        }

        [Test]
        public void FlagEnumsHaveTheirBitValues()
        {
            Assert.Equal(2097152u, (uint)AttentionTopic.Statistics);
            Assert.Equal(1048576u, (uint)AttentionTopic.Transport);
            Assert.Equal(1u, (uint)AttentionTopic.Happiness);
        }

        [Test]
        public void EveryFlagEnumMemberIsASingleBit()
        {
            foreach (Type type in new[] { typeof(AttentionTopic), typeof(Permission) })
            {
                Assert.NotNull(type.GetCustomAttribute<FlagsAttribute>(), type.Name + " is [Flags]");
                foreach (object value in Enum.GetValues(type))
                {
                    ulong bits = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
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
            Assert.Equal(ThreadKind.Any, typeof(ILog).GetCustomAttribute<ThreadAffinityAttribute>().Kind);
            MethodInfo poll = typeof(ITransport).GetMethod("Poll");
            Assert.Equal(ThreadKind.Network, poll.GetCustomAttribute<ThreadAffinityAttribute>().Kind);
        }
    }
}
