using System;
using System.Collections.Generic;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>Proposal records, intents, unlock sets, roles, validation rules, build orders.</summary>
    public sealed class GameplayTests
    {
        private static List<SyncRecord> RoundTrip(SyncBatch.Writer w)
        {
            ArraySegment<byte> b = w.Finish();
            var copy = new byte[b.Count + 2];
            Buffer.BlockCopy(b.Array, 0, copy, 2, b.Count);
            return SyncBatch.Read(copy, 2, b.Count);
        }

        [Test]
        public void ProposalRecordsRoundTrip()
        {
            var w = new SyncBatch.Writer();
            w.Despawn(9);
            w.ProposalHeader(42, 1234567, ProposalFlags.ToolApply | ProposalFlags.UiDelete);
            w.ProposalResult(42, RejectReason.Conflict);
            w.Epoch(77);
            List<SyncRecord> r = RoundTrip(w);
            Assert.Equal(4, r.Count);
            Assert.Equal(SyncRecordKind.ProposalHeader, r[1].Kind);
            Assert.Equal(42u, r[1].Seq);
            Assert.Equal(1234567u, r[1].BaseEpoch);
            Assert.Equal(ProposalFlags.ToolApply | ProposalFlags.UiDelete, r[1].ProposalFlags);
            Assert.Equal(SyncRecordKind.ProposalResult, r[2].Kind);
            Assert.Equal(RejectReason.Conflict, r[2].Reason);
            Assert.Equal(77u, r[3].EpochNumber);
        }

        [Test]
        public void OnlyTheHeaderIsAcceptedFromClients()
        {
            Assert.True(SyncBatch.AcceptedFromClient(SyncRecordKind.ProposalHeader));
            Assert.False(SyncBatch.AcceptedFromClient(SyncRecordKind.ProposalResult));
            Assert.False(SyncBatch.AcceptedFromClient(SyncRecordKind.Epoch));
        }

        [Test]
        public void GameplayKindsStayInTheirReservedRange()
        {
            foreach (SyncRecordKind k in new[] { SyncRecordKind.Epoch, SyncRecordKind.ProposalHeader, SyncRecordKind.ProposalResult })
            {
                Assert.InRange((byte)k, 80, 89);
            }
        }

        [Test]
        public void EpochRecordAtTheEndKeepsFirstRecordRouting()
        {
            var w = new SyncBatch.Writer();
            w.Global(GlobalChannel.Tax, new byte[] { 1 }, 0, 1);
            w.Epoch(5);
            ArraySegment<byte> b = w.Finish();
            Assert.Equal((byte)SyncRecordKind.Global, SyncBatch.FirstRecordKind(b.Array, b.Offset, b.Count));
        }

        [Test]
        public void ActionPayloadRoundTrips()
        {
            var list = new List<ActionIntent>
            {
                new ActionIntent { Kind = ActionKind.DevTreePurchase, Target = 7 },
                new ActionIntent { Kind = ActionKind.PurchaseMapTiles, Ids = new uint[] { 3, 900000 }, Amount = 125000 },
                new ActionIntent { Kind = ActionKind.SetMeshColor, Target = 11, Values = new float[12] },
                new ActionIntent
                {
                    Kind = ActionKind.DeleteSubElements,
                    Target = 2,
                    Keys = new[] { new SubElementKey { Kind = 1, OwnerNetId = 5, PrefabNetId = 6, Index = -1, X = -100, Y = 3, Z = 1 << 20 } },
                },
            };
            byte[] bytes = ActionPayload.Write(list);
            List<ActionIntent> back = ActionPayload.Read(bytes, 0, bytes.Length);
            Assert.Equal(4, back.Count);
            Assert.Equal(ActionKind.DevTreePurchase, back[0].Kind);
            Assert.Equal(7u, back[0].Target);
            Assert.SequenceEqual(new uint[] { 3, 900000 }, back[1].Ids);
            Assert.Equal(125000L, back[1].Amount);
            Assert.Equal(12, back[2].Values.Length);
            Assert.Equal(1, back[3].Keys.Length);
            Assert.Equal(-100, back[3].Keys[0].X);
            Assert.Equal(1 << 20, back[3].Keys[0].Z);
            Assert.Equal(-1, back[3].Keys[0].Index);
            Assert.Equal(6u, back[3].Keys[0].PrefabNetId);
        }

        [Test]
        public void MalformedActionPayloadsAreDecodeExceptions()
        {
            byte[] bytes = ActionPayload.Write(new List<ActionIntent> { new ActionIntent { Kind = ActionKind.SetRouteColor, Target = 1, Values = new float[] { 1, 2, 3, 4 } } });
            Assert.Throws<DecodeException>(() => ActionPayload.Read(bytes, 0, bytes.Length - 1));
            var trailing = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, trailing, 0, bytes.Length);
            Assert.Throws<DecodeException>(() => ActionPayload.Read(trailing, 0, trailing.Length));
            byte[] full = (byte[])bytes.Clone();
            full[0] = (byte)GlobalPayloadMode.Full;
            Assert.Throws<DecodeException>(() => ActionPayload.Read(full, 0, full.Length));
        }

        [Test]
        public void ActionValidationChecksShapes()
        {
            Assert.Equal(RejectReason.None, ActionPayload.Validate(new ActionIntent { Kind = ActionKind.SetHistorical, Target = 4, Amount = 1 }));
            Assert.Equal(RejectReason.Invalid, ActionPayload.Validate(new ActionIntent { Kind = ActionKind.SetHistorical, Target = 4, Amount = 2 }));
            Assert.Equal(RejectReason.Invalid, ActionPayload.Validate(new ActionIntent { Kind = ActionKind.SetVehicleModels, Target = 4, Ids = new uint[] { 1 } }));
            Assert.Equal(RejectReason.None, ActionPayload.Validate(new ActionIntent { Kind = ActionKind.SetMeshColor, Target = 4 }));
            Assert.Equal(RejectReason.Invalid, ActionPayload.Validate(new ActionIntent { Kind = ActionKind.SetMeshColor, Target = 4, Values = new float[3] }));
            Assert.Equal(RejectReason.Invalid, ActionPayload.Validate(new ActionIntent { Kind = ActionKind.SetRouteColor, Target = 4, Values = new[] { float.NaN, 0, 0, 0 } }));
            Assert.Equal(RejectReason.Invalid, ActionPayload.Validate(new ActionIntent { Kind = ActionKind.PurchaseMapTiles, Ids = new uint[] { 1 }, Amount = -5 }));
            Assert.Equal(RejectReason.Invalid, ActionPayload.Validate(new ActionIntent { Kind = ActionKind.DeleteSubElements }));
            Assert.Equal(RejectReason.Invalid, ActionPayload.Validate(new ActionIntent { Kind = (ActionKind)200, Target = 1 }));
            Assert.Equal(Permission.Progression, ActionPayload.Required(ActionKind.DevTreePurchase));
            Assert.Equal(Permission.MapTiles, ActionPayload.Required(ActionKind.PurchaseMapTiles));
            Assert.Equal(Permission.Demolish, ActionPayload.Required(ActionKind.DeleteSubElements));
        }

        [Test]
        public void UnlockSetRoundTripsAndDiffs()
        {
            var ids = new List<uint> { 1, 2, 50, 70000, uint.MaxValue };
            byte[] bytes = UnlockSet.Write(ids);
            Assert.SequenceEqual(ids, UnlockSet.Read(bytes, 0, bytes.Length));
            Assert.Throws<ArgumentException>(() => UnlockSet.Write(new List<uint> { 5, 5 }));
            Assert.Throws<ArgumentException>(() => UnlockSet.Write(new List<uint> { 6, 5 }));

            var unlock = new List<uint>();
            var relock = new List<uint>();
            UnlockSet.Diff(new List<uint> { 1, 2, 3, 99 }, new HashSet<uint> { 2, 4 }, new HashSet<uint> { 1, 2, 3, 4 }, unlock, relock);
            Assert.SequenceEqual(new uint[] { 1, 3 }, unlock); // 99 is not a lockable prefab here
            Assert.SequenceEqual(new uint[] { 4 }, relock);
        }

        [Test]
        public void RolesAndPermissions()
        {
            Assert.True(RolePermissions.Allows(SessionRole.Builder, Permission.Build | Permission.Economy));
            Assert.False(RolePermissions.Allows(SessionRole.Viewer, Permission.Build));
            Assert.True(RolePermissions.Allows(SessionRole.Viewer, Permission.Chat));
            Assert.Equal(SessionRole.Viewer, RolePermissions.Toggle(SessionRole.Builder));
            Assert.Equal(SessionRole.Builder, RolePermissions.Toggle(SessionRole.Viewer));
            Assert.Equal(SessionRole.CoHost, RolePermissions.Toggle(SessionRole.CoHost));
            Assert.False(RolePermissions.Predicts(SessionRole.Viewer));
            Assert.True(RolePermissions.Predicts(SessionRole.Builder));
        }

        [Test]
        public void RateLimiterAllowsBurstThenRefills()
        {
            var limiter = new ProposalRateLimiter(10, 3);
            Assert.True(limiter.TryTake(1, 0));
            Assert.True(limiter.TryTake(1, 0));
            Assert.True(limiter.TryTake(1, 0));
            Assert.False(limiter.TryTake(1, 0));
            Assert.True(limiter.TryTake(2, 0)); // per peer
            Assert.True(limiter.TryTake(1, 0.15)); // 1.5 tokens refilled
            Assert.False(limiter.TryTake(1, 0.15));
        }

        [Test]
        public void ViolationsKickOnlyWhenRepeatedInTheWindow()
        {
            var v = new ViolationTracker(3, 10);
            Assert.False(v.Record(1, 0));
            Assert.False(v.Record(1, 1));
            Assert.False(v.Record(1, 20)); // the first two expired
            Assert.False(v.Record(1, 21));
            Assert.True(v.Record(1, 22));
            Assert.True(ViolationTracker.IsViolation(RejectReason.RateLimited));
            Assert.False(ViolationTracker.IsViolation(RejectReason.Conflict));
        }

        [Test]
        public void LedgerFlagsOnlyNewerChangesOfOthers()
        {
            var ledger = new AuthoredLedger();
            Assert.Equal(1u, ledger.Current);
            ledger.MarkAuthored(10, 0);          // host edit in epoch 1
            Assert.Equal(1u, ledger.Advance());  // broadcast as epoch 1
            ledger.MarkAuthored(11, 3);          // peer 3 in epoch 2
            ledger.Advance();
            Assert.True(ledger.IsConflict(10, 0, 3));   // client 3 had not seen epoch 1
            Assert.False(ledger.IsConflict(10, 1, 3));  // it had
            Assert.False(ledger.IsConflict(11, 0, 3));  // its own change
            Assert.True(ledger.IsConflict(11, 1, 4));   // somebody else's newer change
            Assert.False(ledger.IsConflict(12, 0, 4));  // never authored
            ledger.Forget(10);
            Assert.False(ledger.IsConflict(10, 0, 3));
            Assert.Equal(2u, ledger.LastBroadcast);
        }

        [Test]
        public void ProposalRulesPermissionsAndCost()
        {
            var f = new ProposalFacts { Spawns = 2, Despawns = 1, ZoneBlocks = 1, Flags = ProposalFlags.DirectTool };
            Assert.Equal(Permission.Build | Permission.Demolish | Permission.Zoning | Permission.ModTools, ProposalRules.Required(f));
            var cost = new ProposalFacts { Spawns = 1, HasCost = true, Cost = 500 };
            Assert.Equal(RejectReason.None, ProposalRules.CheckCost(cost, 500, false));
            Assert.Equal(RejectReason.NotEnoughMoney, ProposalRules.CheckCost(cost, 499, false));
            Assert.Equal(RejectReason.None, ProposalRules.CheckCost(cost, 0, true));
            var refund = new ProposalFacts { Spawns = 1, HasCost = true, Cost = -500 };
            Assert.Equal(RejectReason.Invalid, ProposalRules.CheckCost(refund, 0, false)); // a refund without removing anything
            refund.Despawns = 1;
            Assert.Equal(RejectReason.None, ProposalRules.CheckCost(refund, 0, false));
            var huge = new ProposalFacts { HasCost = true, Cost = (long)int.MaxValue + 1 };
            Assert.Equal(RejectReason.Invalid, ProposalRules.CheckCost(huge, int.MaxValue, true));
            Assert.Equal(RejectReason.Invalid, ProposalRules.CheckSize(new ProposalFacts { Upserts = ProposalRules.MaxEntities + 1 }));
            Assert.Contains("another player", ProposalRules.Describe(RejectReason.Conflict));
        }

        [Test]
        public void EditWatchCountsOnlyUnwrittenChanges()
        {
            var w = new EditWatch();
            Assert.False(w.Check(1, 5, false)); // first sight: baseline
            Assert.False(w.Check(1, 5, false));
            Assert.True(w.Check(1, 6, false));  // the player changed it
            Assert.False(w.Check(1, 7, true));  // the host wrote it
            Assert.False(w.Check(1, 7, false));
            w.Clear();
            Assert.False(w.Check(1, 9, false));
        }

        [Test]
        public void BuildOrderPlannerKeepsInheritedSplitRanges()
        {
            var spawned = new List<BuildOrderRange>
            {
                new BuildOrderRange(100, 107), // left half of the split host edge [100, 115]
                new BuildOrderRange(108, 115), // right half
                new BuildOrderRange(16, 31),   // a fresh edge from the client's own counter
                new BuildOrderRange(47, 32),   // a fresh inverted edge
            };
            var touched = new List<BuildOrderRange> { new BuildOrderRange(100, 115) };
            bool[] fresh = BuildOrderPlanner.SelectFresh(spawned, touched);
            Assert.SequenceEqual(new[] { false, false, true, true }, fresh);

            var allocator = new BuildOrderAllocator();
            allocator.Observe(500, 515);
            int n = BuildOrderPlanner.RebaseFresh(allocator, spawned, touched, 900);
            Assert.Equal(2, n);
            Assert.Equal(100u, spawned[0].Start); // untouched
            Assert.Equal(115u, spawned[1].End);
            Assert.True(spawned[2].Min >= 900u, "fresh ranges start at the host counter");
            Assert.Equal(15u, spawned[2].Width);
            Assert.True(spawned[3].IsInverted);
            Assert.True(spawned[3].Min > spawned[2].Max, "relative order kept");
            Assert.True(allocator.Next > spawned[3].Max);
        }
    }
}
