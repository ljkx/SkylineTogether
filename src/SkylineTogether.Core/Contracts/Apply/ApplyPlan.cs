using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>
    /// One unit of client apply work (ARCH §4.7): the merged content of one or more complete epochs, in record order
    /// per list, with all payload bytes in <see cref="Arena"/>. Built by core-replication's <c>ApplyPlanMerger</c>
    /// (or by the re-resolution and repair paths), turned into an <see cref="ApplyProgram"/> by the <c>ApplyPlanner</c>,
    /// and reused after <see cref="Clear"/>. Not thread-safe; a plan belongs to one thread at a time.
    /// </summary>
    public sealed class ApplyPlan
    {
        public EpochId FirstEpoch;
        public EpochId LastEpoch;
        /// <summary>The host tick of <see cref="LastEpoch"/>.</summary>
        public uint HostTick;
        /// <summary>The plan belongs to join catch-up (bigger budget, clock follows the epoch ticks).</summary>
        public bool IsCatchUp;
        /// <summary>The plan carries repair records (SPAWN flagged Repair, rebaseline content).</summary>
        public bool IsRepair;

        public readonly List<BindRecord> Binds = new List<BindRecord>();
        public readonly List<DespawnRecord> Despawns = new List<DespawnRecord>();
        public readonly List<SpawnOp> Spawns = new List<SpawnOp>();
        public readonly List<StructOp> Structs = new List<StructOp>();
        public readonly List<ValueOp> Values = new List<ValueOp>();
        public readonly List<TouchRecord> Touches = new List<TouchRecord>();
        public readonly List<PoseOp> Poses = new List<PoseOp>();
        public readonly List<KeyedOp> Keyed = new List<KeyedOp>();
        /// <summary>Component payloads of <see cref="Spawns"/> and <see cref="Structs"/> (referenced by index ranges).</summary>
        public readonly List<ComponentOp> Components = new List<ComponentOp>();
        public readonly List<ChannelOp> Channels = new List<ChannelOp>();
        public readonly List<HashBeacon> Beacons = new List<HashBeacon>();
        public readonly List<ResultRecord> Results = new List<ResultRecord>();
        public readonly List<InspectionBatch> Inspections = new List<InspectionBatch>();
        /// <summary>Values of <see cref="Inspections"/> (referenced by index ranges).</summary>
        public readonly List<ValueOp> InspectionValues = new List<ValueOp>();
        /// <summary>Type-set definitions carried by the plan's epochs (already loaded into the registry by the merger).</summary>
        public readonly List<TypeSetDefinition> TypeSets = new List<TypeSetDefinition>();
        public readonly PayloadArena Arena = new PayloadArena();

        /// <summary>
        /// True when the plan changes entity structure or slot bindings (despawns, spawns, signature changes or binds),
        /// i.e. when the applier needs its structural batch with its single sync point (ARCH §4.7.3).
        /// </summary>
        public bool HasStructural => Despawns.Count > 0 || Spawns.Count > 0 || Structs.Count > 0 || Binds.Count > 0;

        /// <summary>True when the plan contains nothing to apply.</summary>
        public bool IsEmpty =>
            Binds.Count == 0 && Despawns.Count == 0 && Spawns.Count == 0 && Structs.Count == 0 && Values.Count == 0
            && Touches.Count == 0 && Poses.Count == 0 && Keyed.Count == 0 && Channels.Count == 0 && Beacons.Count == 0
            && Results.Count == 0 && Inspections.Count == 0 && TypeSets.Count == 0;

        /// <summary>Empties every list and the arena and resets the header fields (list capacities are kept).</summary>
        public void Clear()
        {
            FirstEpoch = EpochId.None;
            LastEpoch = EpochId.None;
            HostTick = 0;
            IsCatchUp = false;
            IsRepair = false;
            Binds.Clear();
            Despawns.Clear();
            Spawns.Clear();
            Structs.Clear();
            Values.Clear();
            Touches.Clear();
            Poses.Clear();
            Keyed.Clear();
            Components.Clear();
            Channels.Clear();
            Beacons.Clear();
            Results.Clear();
            Inspections.Clear();
            InspectionValues.Clear();
            TypeSets.Clear();
            Arena.Clear();
        }
    }
}
