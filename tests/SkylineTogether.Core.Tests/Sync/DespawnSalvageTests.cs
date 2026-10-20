using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The check behind "a failed batch is acknowledged only once its despawns are applied" (R2 S2): a despawn that threw
    /// half-way has unbound its id already, so the binding alone says nothing; the entity must be gone or tagged deleted.
    /// </summary>
    public sealed class DespawnSalvageTests
    {
        [Test]
        public void ADespawnWhoseEntitySurvivedIsNotApplied()
        {
            var ids = new List<uint> { 4, 5, 6 };
            var bound = new HashSet<uint>();
            var survivors = new HashSet<uint> { 5 }; // its deletion threw after the id was unbound
            int n = FailedBatchRecovery.CountNotApplied(ids, bound.Contains, survivors.Contains);
            Assert.Equal(1, n, "the binding alone counted 0 here and the batch was acknowledged");
            Assert.False(FailedBatchRecovery.MayAcknowledge(true, n));
        }

        [Test]
        public void StillBoundIdsAndSurvivorsCountOnceEach()
        {
            var ids = new List<uint> { 1, 2, 3 };
            var bound = new HashSet<uint> { 1, 2 };
            var survivors = new HashSet<uint> { 2, 3 };
            Assert.Equal(3, FailedBatchRecovery.CountNotApplied(ids, bound.Contains, survivors.Contains));
        }

        [Test]
        public void DespawnsThatDeletedTheirEntityAllowTheAcknowledgement()
        {
            var ids = new List<uint> { 7, 8 };
            int n = FailedBatchRecovery.CountNotApplied(ids, _ => false, _ => false);
            Assert.Equal(0, n);
            Assert.True(FailedBatchRecovery.MayAcknowledge(true, n));
            Assert.Equal(0, FailedBatchRecovery.CountNotApplied(new List<uint>(), _ => true, _ => true));
        }
    }
}
