using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// Movers under reused ids (R2 S1, S4, G10): a mover record carries its id's generation; a client replaces a mover
    /// still bound under a reused id (by generation, else by prefab) instead of writing another mover's state into it, and
    /// the roster's back-off of an id ends with the mover it was for.
    /// </summary>
    public sealed class MoverBindingTests
    {
        [Test]
        public void AMoverRecordCarriesItsIdsGeneration()
        {
            var w = new SyncBatch.Writer();
            w.Mover(SyncRecordKind.MoverState, 300, new MoverRecord { PrefabNetId = 7 }, 5);
            System.ArraySegment<byte> bytes = w.Finish();
            List<SyncRecord> records = SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count);
            Assert.Equal(1, records.Count);
            Assert.Equal(300u, records[0].NetId);
            Assert.Equal((byte)5, records[0].Generation);
            Assert.Equal(7u, records[0].Mover.PrefabNetId);
            // the layout after the batch header: kind | varuint netId (300 = AC 02) | u8 generation | mover
            byte[] data = new byte[bytes.Count];
            System.Buffer.BlockCopy(bytes.Array, bytes.Offset, data, 0, bytes.Count);
            Assert.Equal((byte)SyncRecordKind.MoverState, data[5]);
            Assert.Equal((byte)0xAC, data[6]);
            Assert.Equal((byte)0x02, data[7]);
            Assert.Equal((byte)5, data[8]);
        }

        [Test]
        public void AMoverOfAnotherGenerationOrPrefabIsReplaced()
        {
            Assert.Equal(BoundRecordVerdict.Write, MoverBindingCheck.Check(3, true, 3, true, false), "the generation is the whole answer when known");
            Assert.Equal(BoundRecordVerdict.Replace, MoverBindingCheck.Check(4, true, 3, true, true));
            Assert.Equal(BoundRecordVerdict.Replace, MoverBindingCheck.Check(0, false, 0, true, false), "a mover never changes its prefab");
            Assert.Equal(BoundRecordVerdict.Write, MoverBindingCheck.Check(0, false, 0, true, true));
            Assert.Equal(BoundRecordVerdict.Write, MoverBindingCheck.Check(0, false, 0, false, false), "nothing known: written as before");
        }

        [Test]
        public void AReusedIdDoesNotInheritTheRosterBackoff()
        {
            var audit = new MoverRosterAudit();
            var remove = new List<uint>();
            var request = new List<uint>();
            uint[] hostHas = { 10, 1 }; // a run: id 10, one id
            var local = new List<uint>();
            int asked = 0;
            for (int pass = 0; pass < 12; pass++)
            {
                request.Clear();
                audit.OnRoster(1, 64, hostHas, local, remove, request);
                asked += request.Count;
            }
            Assert.True(asked < 6, "a mover that never arrives is asked for less and less often (" + asked + ")");
            // the host despawned it and reused the id for another mover: asked for at the second roster again
            audit.Forget(10);
            request.Clear();
            audit.OnRoster(1, 64, hostHas, local, remove, request);
            audit.OnRoster(1, 64, hostHas, local, remove, request);
            Assert.Equal(1, request.Count);
            Assert.Equal(10u, request[0]);
        }
    }
}
