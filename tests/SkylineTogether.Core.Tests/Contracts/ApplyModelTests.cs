using System;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>The apply plan and apply program models (ARCH §4.7).</summary>
    public sealed class ApplyModelTests
    {
        [Test]
        public void ArenaAppendsAndReturnsRanges()
        {
            var arena = new PayloadArena(4);
            PayloadRef a = arena.Append(new byte[] { 1, 2, 3 }, 0, 3);
            PayloadRef b = arena.Append(new byte[] { 9, 8, 7, 6, 5 }, 1, 3);
            PayloadRef empty = arena.Append(new byte[0], 0, 0);
            Assert.Equal(new PayloadRef(0, 3), a);
            Assert.Equal(new PayloadRef(3, 3), b);
            Assert.Equal(new PayloadRef(6, 0), empty);
            Assert.True(empty.IsEmpty);
            Assert.Equal(6, arena.Length);
            Assert.SequenceEqual(new byte[] { 1, 2, 3 }, arena.Segment(a));
            Assert.SequenceEqual(new byte[] { 8, 7, 6 }, arena.Segment(b));
            Assert.True(arena.Contains(a) && arena.Contains(b) && arena.Contains(empty));
            Assert.False(arena.Contains(new PayloadRef(5, 2)));
            Assert.False(arena.Contains(new PayloadRef(-1, 1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => arena.Segment(new PayloadRef(4, 10)));
        }

        [Test]
        public void ArenaGrowsAndKeepsContent()
        {
            var arena = new PayloadArena(0);
            var expected = new byte[100000];
            for (int i = 0; i < expected.Length; i++) expected[i] = (byte)(i * 31);
            for (int offset = 0; offset < expected.Length; offset += 1000)
            {
                arena.Append(expected, offset, 1000);
            }
            Assert.Equal(expected.Length, arena.Length);
            Assert.SequenceEqual(expected, arena.Segment(new PayloadRef(0, expected.Length)));
        }

        [Test]
        public void ArenaValidatesArguments()
        {
            var arena = new PayloadArena();
            Assert.Throws<ArgumentNullException>(() => arena.Append(null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => arena.Append(new byte[4], 2, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => arena.Append(new byte[4], -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => arena.Append(new byte[4], 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PayloadArena(-1));
        }

        [Test]
        public void ArenaClearKeepsSmallBuffersAndReleasesHugeOnes()
        {
            var arena = new PayloadArena(16);
            arena.Append(new byte[10], 0, 10);
            byte[] small = arena.Buffer;
            arena.Clear();
            Assert.Equal(0, arena.Length);
            Assert.Same(small, arena.Buffer);

            arena.Append(new byte[PayloadArena.RetainLimitBytes + 1], 0, PayloadArena.RetainLimitBytes + 1);
            Assert.True(arena.Buffer.Length > PayloadArena.RetainLimitBytes);
            arena.Clear();
            Assert.Equal(16, arena.Buffer.Length);
        }

        private static ApplyPlan FilledPlan()
        {
            var plan = new ApplyPlan
            {
                FirstEpoch = new EpochId(3),
                LastEpoch = new EpochId(9),
                HostTick = 1234,
                IsCatchUp = true,
                IsRepair = true
            };
            PayloadRef payload = plan.Arena.Append(new byte[] { 1, 2, 3, 4 }, 0, 4);
            plan.Binds.Add(new BindRecord { Origin = new PeerId(2), Local = new LocalId(1), Slot = new NetSlot(5), Generation = 1 });
            plan.Despawns.Add(new DespawnRecord { Slot = new NetSlot(6), Generation = 2 });
            plan.Components.Add(new ComponentOp { Type = new TypeRank(1), Payload = payload });
            plan.Spawns.Add(new SpawnOp { Header = new SpawnHeader { Slot = new NetSlot(7), Prefab = new PrefabRank(3), TypeSet = new TypeSetId(1), ComponentCount = 1 }, FirstComponent = 0 });
            plan.Structs.Add(new StructOp { Header = new StructHeader { Slot = new NetSlot(8), NewTypeSet = new TypeSetId(2) } });
            plan.Values.Add(new ValueOp { Slot = new NetSlot(8), Component = new ComponentOp { Type = new TypeRank(2), Payload = payload } });
            plan.Touches.Add(new TouchRecord { Slot = new NetSlot(8), Flags = TouchFlags.Updated });
            plan.Poses.Add(new PoseOp { Slot = new NetSlot(9), Payload = payload });
            plan.Keyed.Add(new KeyedOp { Key = new DerivedKey(DerivedKeyKind.Block, new NetSlot(8), PrefabRank.None, 0, 0, 0, 0, 0, 0, 0), Delete = true });
            plan.Channels.Add(new ChannelOp { Section = SectionType.Icons, Payload = payload });
            plan.Beacons.Add(new HashBeacon { Class = ReplicationClass.S, Bucket = 0, Sum = 7, Count = 1 });
            plan.Results.Add(new ResultRecord { Origin = new PeerId(2), ProposalSeq = 4, Reason = RejectReason.Conflict });
            plan.InspectionValues.Add(new ValueOp { Slot = new NetSlot(10) });
            plan.Inspections.Add(new InspectionBatch { SampleTick = 512, FirstValue = 0, ValueCount = 1 });
            plan.TypeSets.Add(new TypeSetDefinition { Id = new TypeSetId(1), Types = new[] { new TypeRank(1) } });
            return plan;
        }

        [Test]
        public void PlanClearEmptiesEverything()
        {
            ApplyPlan plan = FilledPlan();
            Assert.True(plan.HasStructural);
            Assert.False(plan.IsEmpty);
            plan.Clear();
            Assert.Equal(EpochId.None, plan.FirstEpoch);
            Assert.Equal(EpochId.None, plan.LastEpoch);
            Assert.Equal(0u, plan.HostTick);
            Assert.False(plan.IsCatchUp);
            Assert.False(plan.IsRepair);
            Assert.Equal(0, plan.Binds.Count + plan.Despawns.Count + plan.Spawns.Count + plan.Structs.Count + plan.Values.Count
                            + plan.Touches.Count + plan.Poses.Count + plan.Keyed.Count + plan.Components.Count + plan.Channels.Count
                            + plan.Beacons.Count + plan.Results.Count + plan.Inspections.Count + plan.InspectionValues.Count + plan.TypeSets.Count);
            Assert.Equal(0, plan.Arena.Length);
            Assert.False(plan.HasStructural);
            Assert.True(plan.IsEmpty);
        }

        [Test]
        public void HasStructuralMeansDespawnsSpawnsStructsOrBinds()
        {
            var plan = new ApplyPlan();
            Assert.False(plan.HasStructural);
            plan.Values.Add(new ValueOp { Slot = new NetSlot(1) });
            plan.Touches.Add(new TouchRecord { Slot = new NetSlot(1) });
            plan.Keyed.Add(new KeyedOp());
            plan.Channels.Add(new ChannelOp());
            plan.Results.Add(new ResultRecord());
            plan.TypeSets.Add(new TypeSetDefinition());
            Assert.False(plan.HasStructural, "values, touches, keyed state, channels, results and type sets are not structural");

            plan.Clear();
            plan.Despawns.Add(new DespawnRecord());
            Assert.True(plan.HasStructural);
            plan.Clear();
            plan.Spawns.Add(new SpawnOp());
            Assert.True(plan.HasStructural);
            plan.Clear();
            plan.Structs.Add(new StructOp());
            Assert.True(plan.HasStructural);
            plan.Clear();
            plan.Binds.Add(new BindRecord());
            Assert.True(plan.HasStructural);
        }

        private static ApplyOp Op(ApplyOpKind kind, uint slot = 1)
        {
            ApplyOp op = ApplyOp.Create(kind);
            op.Slot = slot == 0 ? NetSlot.None : new NetSlot(slot);
            return op;
        }

        private static ApplyProgram ValidProgram(ApplyPlan source)
        {
            var program = new ApplyProgram { Source = source };
            ApplyOp create = Op(ApplyOpKind.CreateGroup, 0);
            create.GroupIndex = 0;
            create.Count = 2;
            create.Family = Family.Object;
            create.Prefab = new PrefabRank(3);
            create.TypeSet = new TypeSetId(1);
            program.Ops.Add(create);
            for (uint slot = 10; slot < 12; slot++)
            {
                ApplyOp bind = Op(ApplyOpKind.Bind, slot);
                bind.GroupIndex = 0;
                bind.TypeSet = new TypeSetId(1);
                program.Ops.Add(bind);
            }
            ApplyOp write = Op(ApplyOpKind.WriteValue, 10);
            write.Type = new TypeRank(2);
            write.Payload = new PayloadRef(0, 4);
            program.Ops.Add(write);
            ApplyOp finalize = Op(ApplyOpKind.Finalize, 0);
            finalize.GroupIndex = 0;
            program.Ops.Add(finalize);
            ApplyOp evt = Op(ApplyOpKind.SynthesizeEvent, 10);
            evt.Aux = (uint)SynthesizedEventKind.PathUpdated;
            program.Ops.Add(evt);
            return program;
        }

        [Test]
        public void ProgramCountsCreatedGroupsAndClears()
        {
            var plan = new ApplyPlan();
            plan.Arena.Append(new byte[4], 0, 4);
            ApplyProgram program = ValidProgram(plan);
            Assert.Equal(1, program.CreatedGroups);
            ApplyOp second = Op(ApplyOpKind.CreateGroup, 0);
            second.GroupIndex = 1;
            program.Ops.Add(second);
            Assert.Equal(2, program.CreatedGroups);
            program.Clear();
            Assert.Equal(0, program.Ops.Count);
            Assert.Null(program.Source);
            Assert.Equal(0, program.CreatedGroups);
        }

        [Test]
        public void ValidateAcceptsAWellFormedProgram()
        {
            var plan = new ApplyPlan();
            plan.Arena.Append(new byte[4], 0, 4);
            ApplyProgram program = ValidProgram(plan);
            Assert.True(program.Validate(out string error), error);
            Assert.Null(error);
            Assert.True(new ApplyProgram().Validate(out _), "an empty program is valid");
        }

        [Test]
        public void ValidateRejectsMalformedPrograms()
        {
            var plan = new ApplyPlan();
            plan.Arena.Append(new byte[4], 0, 4);

            void Expect(string fragment, Action<ApplyProgram> corrupt)
            {
                ApplyProgram program = ValidProgram(plan);
                corrupt(program);
                Assert.False(program.Validate(out string error), "expected a validation error containing \"" + fragment + "\"");
                Assert.Contains(fragment, error);
            }

            Expect("numbered in order", p => { ApplyOp op = p.Ops[0]; op.GroupIndex = 1; p.Ops[0] = op; });
            Expect("positive count", p => { ApplyOp op = p.Ops[0]; op.Count = 0; p.Ops[0] = op; });
            Expect("type set", p => { ApplyOp op = p.Ops[0]; op.TypeSet = TypeSetId.None; p.Ops[0] = op; });
            Expect("more binds than entities", p => { ApplyOp extra = p.Ops[1]; extra.Slot = new NetSlot(99); p.Ops.Insert(3, extra); });
            Expect("before all its entities were bound", p => p.Ops.RemoveAt(2));
            Expect("creates 2 entities but binds 1", p => { p.Ops.RemoveAt(4); p.Ops.RemoveAt(2); });
            Expect("not created", p => { ApplyOp op = p.Ops[1]; op.GroupIndex = 5; p.Ops[1] = op; });
            Expect("without a slot", p => { ApplyOp op = p.Ops[1]; op.Slot = NetSlot.None; p.Ops[1] = op; });
            Expect("LocalId", p => { ApplyOp op = p.Ops[1]; op.GroupIndex = -1; op.Aux = 0; p.Ops[1] = op; });
            Expect("finalised twice", p => p.Ops.Add(p.Ops[4]));
            Expect("finalised group", p => p.Ops.Add(p.Ops[1]));
            Expect("outside the source arena", p => { ApplyOp op = p.Ops[3]; op.Payload = new PayloadRef(2, 4); p.Ops[3] = op; });
            Expect("negative payload", p => { ApplyOp op = p.Ops[3]; op.Payload = new PayloadRef(-1, 1); p.Ops[3] = op; });
            Expect("unknown synthesized event", p => { ApplyOp op = p.Ops[5]; op.Aux = 9; p.Ops[5] = op; });
            Expect("unknown op kind", p => p.Ops.Add(Op((ApplyOpKind)200)));
            Expect("missing slot in Aux", p => { ApplyOp park = Op(ApplyOpKind.Park, 10); park.Payload = new PayloadRef(0, 4); p.Ops.Add(park); });
            Expect("op without a slot", p => p.Ops.Add(Op(ApplyOpKind.AddTags, 0)));
            Expect("signature reconcile without a type set", p => p.Ops.Add(Op(ApplyOpKind.ReconcileSignature, 10)));
        }

        [Test]
        public void PredictionBindsNeedNoGroup()
        {
            var program = new ApplyProgram();
            ApplyOp bind = Op(ApplyOpKind.Bind, 42);
            bind.Aux = 7;   // LocalId of the prediction
            program.Ops.Add(bind);
            ApplyOp park = Op(ApplyOpKind.Park, 42);
            park.Aux = 43;
            program.Ops.Add(park);
            Assert.True(program.Validate(out string error), error);
        }

        [Test]
        public void ApplyOpCreateHasNeutralDefaults()
        {
            ApplyOp op = ApplyOp.Create(ApplyOpKind.WriteValue);
            Assert.Equal(ApplyOpKind.WriteValue, op.Kind);
            Assert.Equal(-1, op.GroupIndex);
            Assert.True(op.Prefab.IsNone);
            Assert.True(op.Slot.IsNone);
            Assert.Contains("WriteValue", op.ToString());
        }

        [Test]
        public void BindingViewUnboundUsesNaN()
        {
            BindingView view = BindingView.Unbound;
            Assert.False(view.Bound);
            Assert.False(view.Alive);
            Assert.True(double.IsNaN(view.BoundFromPredictionAtSeconds));
            Assert.True(view.Prefab.IsNone);
        }

        [Test]
        public void ApplyResultFaultHelper()
        {
            ApplyResult result = ApplyResult.Fault(FaultCategory.StructuralApply, "boom");
            Assert.True(result.Faulted);
            Assert.Equal(FaultCategory.StructuralApply, result.FaultCategory);
            Assert.Equal("boom", result.FaultDetail);
            Assert.Equal(0, result.Applied);
        }
    }
}
