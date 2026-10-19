using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// Atomic spawns (a new entity is kept only once its component set and values were written) and the recovery of a
    /// host batch whose apply threw (acknowledged only once its despawns are applied).
    /// </summary>
    public sealed class ApplyTransactionTests
    {
        [Test]
        public void OnlyCompletelyWrittenSpawnsSurviveAFailedApply()
        {
            var t = new SpawnTransaction<string>();
            var spawned = new List<string> { "edge", "node", "tree", "house" };
            t.Written("edge");
            t.Written("node");
            t.Fail("node"); // values written, then a later step of the same record failed
            t.Written("tree");
            // "house": created, the apply threw before its values
            var rollBack = new List<string>();
            t.Incomplete(spawned, rollBack);
            Assert.SequenceEqual(new[] { "node", "house" }, rollBack.ToArray());
            Assert.True(t.IsComplete("edge") && t.IsComplete("tree"));
        }

        [Test]
        public void FailedSpawnsAreDroppedOnceAndNeverCountAsWritten()
        {
            var t = new SpawnTransaction<int>();
            t.Fail(1);
            t.Fail(2);
            t.Written(2);
            Assert.Equal(2, t.FailedCount);
            Assert.False(t.IsComplete(2), "a failure wins over a later write");
            t.ClearFailed();
            Assert.Equal(0, t.FailedCount);
            Assert.False(t.IsComplete(2), "a dropped entity is gone, not written");
            t.Written(3);
            t.Forget(3);
            Assert.False(t.IsComplete(3));
            var rest = new List<int>();
            t.Incomplete(new[] { 4 }, rest);
            Assert.SequenceEqual(new[] { 4 }, rest.ToArray());
        }

        private static SyncRecord Rec(SyncRecordKind kind, uint id) => new SyncRecord { Kind = kind, NetId = id };

        [Test]
        public void AFailedBatchAppliesEveryDespawnExceptThoseReusedWithinIt()
        {
            var records = new List<SyncRecord>
            {
                Rec(SyncRecordKind.Despawn, 5),
                Rec(SyncRecordKind.Spawn, 5), // id 5 reused within the batch: its despawn went first
                Rec(SyncRecordKind.Upsert, 6),
                Rec(SyncRecordKind.Despawn, 6), // written, then despawned: despawned
                Rec(SyncRecordKind.Despawn, 7),
                Rec(SyncRecordKind.Despawn, 7),
                Rec(SyncRecordKind.Values, 8),
                Rec(SyncRecordKind.Clock, 0),
            };
            var despawns = new List<uint>();
            FailedBatchRecovery.DespawnsToApply(records, despawns);
            Assert.SequenceEqual(new uint[] { 6, 7 }, despawns.ToArray());
            var ask = new List<uint>();
            FailedBatchRecovery.IdsToRequest(records, ask);
            Assert.SequenceEqual(new uint[] { 5, 6, 8 }, ask.ToArray());
        }

        [Test]
        public void AFailedBatchIsAcknowledgedOnlyWhenItsDespawnsAreApplied()
        {
            Assert.True(FailedBatchRecovery.MayAcknowledge(true, 0));
            Assert.False(FailedBatchRecovery.MayAcknowledge(true, 1), "a despawned id still bound here: the host must not reuse it");
            Assert.False(FailedBatchRecovery.MayAcknowledge(false, 0), "an undecodable batch: what it despawned is unknown");
        }
    }
}
