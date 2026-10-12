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
            var names = new List<string>(sequential) { "EpochAck" }; // w2/ids: merge-reserved block 60-64
            Assert.SequenceEqual(names, Enum.GetNames(typeof(MessageKind)), "MessageKind members");
            for (int i = 0; i < sequential.Length; i++) Assert.Equal(1 + i, (int)(MessageKind)Enum.Parse(typeof(MessageKind), sequential[i]), sequential[i]);
            Assert.Equal(60, (int)MessageKind.EpochAck);
        }

        [Test]
        public void SectionTypesStartAtOne()
        {
            SequentialFrom<SectionType>(1, "Tx", "Values", "SysState", "PrefabState", "Icons", "Events", "Names", "CellMaps", "Clock", "Hash",
                                        "Session", "Inspection", "TypeSets");
        }

        [Test]
        public void RecordKindsStartAtOne()
        {
            SequentialFrom<RecordKind>(1, "TypeSetDef", "Spawn", "Despawn", "Struct", "Value", "Touch", "Bind", "Pose", "Keyed", "KeyedDelete",
                                       "Zone", "Icon", "Name", "PrefabState", "SysState", "Result", "Hash", "Event", "TerrainStamp", "TypeSetKeyframe");
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
            SequentialFrom<TypeKind>(0, "Serializable", "EmptyTag", "EmptyData", "SharedSerializable");
            SequentialFrom<CodecKind>(0, "Serializable", "EmptyTag", "EnableBit", "EntityRefList", "UpdateFrame", "Shared", "Custom");
            SequentialFrom<SessionRole>(0, "Host", "CoHost", "Builder", "Viewer");
            SequentialFrom<IconOp>(0, "Add", "Remove", "RemoveAll", "Event");
            SequentialFrom<EventRecordKind>(0, "XpToast", "MilestoneHint", "JournalChanged", "RadioTag", "HostBusy", "SpeedChanged");
            SequentialFrom<ApplyOpKind>(0, "Unbind", "MarkDeleted", "CreateGroup", "Bind", "ReconcileSignature", "WriteValue", "SetEnabled", "Park",
                                        "Hold", "AddTags", "ClosureTag", "Finalize", "DeriveStreetLights", "SynthesizeEvent");
            SequentialFrom<SynthesizedEventKind>(0, "RentersUpdated", "ColorUpdated", "SubObjectsUpdated", "PathUpdated");
            SequentialFrom<ObserveSource>(0, "Spawn", "Edc", "Sdc", "HotSet", "Motion", "Coalesced", "Repair", "Commit", "Prime", "CoCapture", "PathResult");
            SequentialFrom<FaultCategory>(0, "StructuralApply", "ValueApply", "Capture", "Commit", "Snapshot", "Transport", "Decode", "Ui", "Probe", "Handover");
            SequentialFrom<GateAction>(0, "Off", "OffAndFeed", "Keep", "OffDuringSessionLoad", "ToolScoped");
            SequentialFrom<CompressionKind>(0, "None", "ManagedLz4", "Zstd", "Lz4");
            SequentialFrom<ThreadKind>(0, "Main", "Network", "Worker", "Any");
            SequentialFrom<LogLevel>(0, "Trace", "Debug", "Info", "Warn", "Error", "Critical");
        }

        [Test]
        public void FlagEnumsHaveTheirBitValues()
        {
            Pin((TouchFlags.None, 0), (TouchFlags.Updated, 1), (TouchFlags.BatchesUpdated, 2), (TouchFlags.PathfindUpdated, 4), (TouchFlags.EffectsUpdated, 8));
            Pin((SpawnFlags.None, 0), (SpawnFlags.Mover, 1), (SpawnFlags.HasUpdateFrame, 2), (SpawnFlags.Repair, 4));
            Pin((ValueHeaderFlags.None, 0), (ValueHeaderFlags.HasEnabledBit, 1), (ValueHeaderFlags.Enabled, 2), (ValueHeaderFlags.PresenceOnly, 4));
            Assert.Equal(1u, (uint)TypeFlags.Buffer);
            Assert.Equal(2u, (uint)TypeFlags.Enableable);
            Assert.Equal(4u, (uint)TypeFlags.SerializeAsEnabled);
            Assert.Equal(8u, (uint)TypeFlags.PlayerAuthorable);
            Assert.Equal(16u, (uint)TypeFlags.HashAsSet);
            Assert.Equal(32u, (uint)TypeFlags.NoReapply);
            Assert.Equal(64u, (uint)TypeFlags.OnDemand);
            Assert.Equal(128u, (uint)TypeFlags.Excluded);
            Assert.Equal(256u, (uint)TypeFlags.Merge);
            Assert.Equal(512u, (uint)TypeFlags.DataReplicatedPresence);
            Assert.Equal(1024u, (uint)TypeFlags.Source);
            Assert.Equal(2048u, (uint)TypeFlags.AuthorableMask);
            Assert.Equal(4096u, (uint)TypeFlags.PresenceAuthorable);
            Assert.Equal(8192u, (uint)TypeFlags.ForwardReference);
            Assert.Equal(16384u, (uint)TypeFlags.CacheReceived);
            Assert.Equal(32768u, (uint)TypeFlags.Guarded);
            Assert.Equal(65536u, (uint)TypeFlags.PresenceSafe);
            Assert.Equal(131072u, (uint)TypeFlags.DeferredFields);
            Assert.Equal(2097152u, (uint)AttentionTopic.Statistics);
            Assert.Equal(1048576u, (uint)AttentionTopic.Transport);
            Assert.Equal(1u, (uint)AttentionTopic.Happiness);
        }

        [Test]
        public void EveryFlagEnumMemberIsASingleBit()
        {
            foreach (Type type in new[] { typeof(TouchFlags), typeof(SpawnFlags), typeof(TypeFlags), typeof(AttentionTopic), typeof(Permission), typeof(ValueHeaderFlags) })
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
        public void SessionPhasesAreComplete()
        {
            SequentialFrom<SessionPhase>(0, "Idle", "PreparingSession", "Hosting", "Connecting", "Handshake", "Mismatch", "AwaitingSnapshot",
                                         "Downloading", "Loading", "CatchingUp", "LiveVerifying", "Synced", "Repairing", "Resyncing", "HostBusy",
                                         "Lagging", "Degraded", "Disconnected", "Faulted", "Ended");
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
            Assert.Equal(ThreadKind.Worker, typeof(IPeerScheduler).GetCustomAttribute<ThreadAffinityAttribute>().Kind);
            MethodInfo bind = typeof(IHostReplicationEngine).GetMethod("Bind");
            Assert.NotNull(bind.GetCustomAttribute<CaptureWindowAttribute>(), "Bind is a capture-window member");
            MethodInfo poll = typeof(ITransport).GetMethod("Poll");
            Assert.Equal(ThreadKind.Network, poll.GetCustomAttribute<ThreadAffinityAttribute>().Kind);
        }
    }
}
