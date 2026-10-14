using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>Mover existence records (SyncRecordKind 20-22).</summary>
    public sealed class MoverRecordTests
    {
        private static List<SyncRecord> RoundTrip(SyncBatch.Writer w)
        {
            ArraySegment<byte> bytes = w.Finish();
            var copy = new byte[bytes.Count + 3];
            Buffer.BlockCopy(bytes.Array, 0, copy, 3, bytes.Count);
            return SyncBatch.Read(copy, 3, bytes.Count);
        }

        [Test]
        public void SpawnStateAndDespawnRoundTrip()
        {
            var car = new MoverRecord
            {
                Kind = MoverKind.Moving,
                Unspawned = true,
                TrainController = true,
                PrefabNetId = 4711,
                UpdateFrame = 13,
                Seed = 65000,
                X = -1234.5f, Y = 77.25f, Z = 3000.125f,
                QX = 0f, QY = 0.7071068f, QZ = 0f, QW = 0.7071068f,
                Controller = 0,
                Layout = new uint[] { 900, 901, 902 },
                Route = 55,
            };
            var rider = new MoverRecord
            {
                Kind = MoverKind.Riding,
                PrefabNetId = 12,
                UpdateFrame = 2,
                Seed = 3,
                Controller = 77,
                HasHumanFlags = true,
                HumanFlags = 0x80000001u,
                Vehicle = 901,
                VehicleFlags = 6,
                RX = 0.5f, RY = 1.25f, RZ = -0.75f,
                RQX = 0f, RQY = 0f, RQZ = 0f, RQW = 1f,
                BoneX = -1, BoneY = 4, BoneZ = 0,
            };
            var w = new SyncBatch.Writer();
            w.Mover(SyncRecordKind.MoverSpawn, 900, car);
            w.Mover(SyncRecordKind.MoverState, 1000, rider);
            w.MoverDespawn(1001);
            w.Clock(5);
            List<SyncRecord> recs = RoundTrip(w);
            Assert.Equal(4, recs.Count);

            Assert.Equal(SyncRecordKind.MoverSpawn, recs[0].Kind);
            Assert.Equal(900u, recs[0].NetId);
            MoverRecord a = recs[0].Mover;
            Assert.Equal(MoverKind.Moving, a.Kind);
            Assert.True(a.Unspawned);
            Assert.True(a.TrainController);
            Assert.Equal(4711u, a.PrefabNetId);
            Assert.Equal((byte)13, a.UpdateFrame);
            Assert.Equal((ushort)65000, a.Seed);
            Assert.Equal(-1234.5f, a.X);
            Assert.Equal(77.25f, a.Y);
            Assert.Equal(3000.125f, a.Z);
            Assert.Near(0.7071068, a.QY, 1e-4);
            Assert.Near(0.7071068, a.QW, 1e-4);
            Assert.SequenceEqual(new uint[] { 900, 901, 902 }, a.Layout);
            Assert.Equal(55u, a.Route);
            Assert.Equal(0u, a.Controller);
            Assert.False(a.HasHumanFlags);

            Assert.Equal(SyncRecordKind.MoverState, recs[1].Kind);
            MoverRecord b = recs[1].Mover;
            Assert.Equal(MoverKind.Riding, b.Kind);
            Assert.Equal(77u, b.Controller);
            Assert.Null(b.Layout);
            Assert.True(b.HasHumanFlags);
            Assert.Equal(0x80000001u, b.HumanFlags);
            Assert.Equal(901u, b.Vehicle);
            Assert.Equal(6u, b.VehicleFlags);
            Assert.Equal(1.25f, b.RY);
            Assert.Near(1.0, b.RQW, 1e-4);
            Assert.Equal(-1, b.BoneX);
            Assert.Equal(4, b.BoneY);

            Assert.Equal(SyncRecordKind.MoverDespawn, recs[2].Kind);
            Assert.Equal(1001u, recs[2].NetId);
            Assert.Equal(5u, recs[3].Frame);
        }

        [Test]
        public void InsideCarriesTheVehicleButNoRelativePose()
        {
            var w = new SyncBatch.Writer();
            w.Mover(SyncRecordKind.MoverState, 1, new MoverRecord { Kind = MoverKind.Inside, PrefabNetId = 2, Vehicle = 3, VehicleFlags = 1, RX = 99f });
            MoverRecord m = RoundTrip(w)[0].Mover;
            Assert.Equal(MoverKind.Inside, m.Kind);
            Assert.Equal(3u, m.Vehicle);
            Assert.Equal(0f, m.RX);
        }

        [Test]
        public void DegenerateRotationsBecomeIdentity()
        {
            var w = new SyncBatch.Writer();
            w.Mover(SyncRecordKind.MoverSpawn, 1, new MoverRecord { PrefabNetId = 2, QX = 0, QY = 0, QZ = 0, QW = 0, X = float.NaN });
            MoverRecord m = RoundTrip(w)[0].Mover;
            Assert.Equal(1f, m.QW);
            Assert.Equal(0f, m.X);
        }

        [Test]
        public void MalformedMoverRecordsAreDecodeExceptions()
        {
            var w = new SyncBatch.Writer();
            w.Mover(SyncRecordKind.MoverSpawn, 1, new MoverRecord { PrefabNetId = 2, Layout = new uint[] { 5 } });
            ArraySegment<byte> bytes = w.Finish();
            byte[] data = new byte[bytes.Count];
            Buffer.BlockCopy(bytes.Array, 0, data, 0, bytes.Count);
            for (int cut = 6; cut < data.Length; cut++)
            {
                Assert.Throws<DecodeException>(() => SyncBatch.Read(data, 0, cut), "truncated at " + cut);
            }
            // update frame above 15
            byte[] bad = (byte[])data.Clone();
            bad[5 + 1 + 1 + 1 + 1] = 16;   // header(5) | kind | netId | flags | prefab | updateFrame
            Assert.Throws<DecodeException>(() => SyncBatch.Read(bad, 0, bad.Length));
            Assert.Throws<ArgumentException>(() => w.Mover(SyncRecordKind.Spawn, 1, new MoverRecord()));
        }
    }
}
