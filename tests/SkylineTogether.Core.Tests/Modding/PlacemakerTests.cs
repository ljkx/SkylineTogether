using System;
using System.Collections.Generic;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// Placemaker's on-demand runtime prefabs (docs/design/MOD-COMPATIBILITY.md §7): the names that describe them
    /// (<see cref="PlacemakerNames"/>), the requests a client's adapter sends the host (<see cref="PlacemakerMessage"/>)
    /// and the session's bookkeeping of prefabs being created (<see cref="RuntimePrefabBook"/>).
    /// </summary>
    public sealed class PlacemakerTests
    {
        // the name a host's Placemaker created in a real session (the joiner lacked it)
        private const string Observed = "Placemaker Park a30 l9 r6000 c3000 m16 u20000";

        [Test]
        public void ParkNamesCarryTheStatisticsAndRoundTrip()
        {
            Assert.True(PlacemakerNames.TryParsePark(Observed, out PlacemakerNames.ParkKey key));
            Assert.Equal(30, key.Attractiveness);
            Assert.Equal(9, key.Leisure);
            Assert.Equal(6000, key.Range);
            Assert.Equal(3000, key.Capacity);
            Assert.Equal(16, key.Magnitude);
            Assert.Equal(20000, key.Upkeep);
            Assert.False(key.Beach);
            Assert.Equal(Observed, PlacemakerNames.ParkName(key));

            var beach = new PlacemakerNames.ParkKey { Attractiveness = 0, Leisure = 1, Range = 250, Capacity = 100, Magnitude = 1, Upkeep = 0, Beach = true };
            string name = PlacemakerNames.ParkName(beach);
            Assert.Equal("Placemaker Park a0 l1 r250 c100 m1 u0 b", name);
            Assert.True(PlacemakerNames.TryParsePark(name, out PlacemakerNames.ParkKey back));
            Assert.Equal(beach, back);
            Assert.Equal("Placemaker Park a0 l0 r0 c0 m0 u0", PlacemakerNames.ParkName(new PlacemakerNames.ParkKey { Attractiveness = -5 }), "negative values count as 0");
        }

        [Test]
        public void OnlyTheCanonicalParkNameIsAccepted()
        {
            string[] refused =
            {
                null, "", "Placemaker Park ", "Placemaker Park a30 l9 r6000 c3000 m16", // a field missing
                "Placemaker Park a30 l9 r6000 c3000 m16 u20000 x", "Placemaker Park a30 l9 r6000 c3000 m16 u20000 b b",
                "Placemaker Park l9 a30 r6000 c3000 m16 u20000", // order
                "Placemaker Park a030 l9 r6000 c3000 m16 u20000", // leading zero
                "Placemaker Park a-1 l9 r6000 c3000 m16 u20000", "Placemaker Park a+1 l9 r6000 c3000 m16 u20000",
                "Placemaker Park a30  l9 r6000 c3000 m16 u20000", "Placemaker Park a30 l9 r6000 c3000 m16 u20000 ",
                "Placemaker Park a l9 r6000 c3000 m16 u20000", "Placemaker Park a1e3 l9 r6000 c3000 m16 u20000",
                "Placemaker Park a10000001 l9 r6000 c3000 m16 u20000", // above the bound
                "Placemaker Park a99999999999 l9 r6000 c3000 m16 u20000",
                "placemaker park a30 l9 r6000 c3000 m16 u20000", "Placemaker Parks a30 l9 r6000 c3000 m16 u20000",
                "Placemaker Park a30 l9 r6000 c3000 m16 u20000" + new string(' ', 200),
            };
            foreach (string name in refused) Assert.False(PlacemakerNames.TryParsePark(name, out _), name ?? "null");
            Assert.True(PlacemakerNames.TryParsePark("Placemaker Park a10000000 l0 r0 c0 m0 u10000000", out _), "the bound itself");
        }

        [Test]
        public void OverlayNamesNameTheOriginalSurface()
        {
            Assert.True(PlacemakerNames.TryParseOverlay("Placemaker Overlay (Missing Area)", out string original));
            Assert.Equal("Missing Area", original);
            Assert.Equal("Placemaker Overlay (Grass Surface 02)", PlacemakerNames.OverlayName("Grass Surface 02"));
            Assert.True(PlacemakerNames.TryParseOverlay("Placemaker Overlay (Sand (Wet))", out original), "parentheses inside the original");
            Assert.Equal("Sand (Wet)", original);
            foreach (string name in new[]
                     {
                         null, "Placemaker Overlay ()", "Placemaker Overlay (Grass", "Placemaker Overlay Grass)", "Placemaker Overlay (Placemaker Overlay (Grass))",
                         "Placemaker Overlay (Gr\u0001ss)", "Placemaker Overlay (" + new string('x', 200) + ")",
                     })
            {
                Assert.False(PlacemakerNames.TryParseOverlay(name, out _), name ?? "null");
            }
        }

        [Test]
        public void ClassifyChecksTheTypeWhenItIsKnown()
        {
            Assert.Equal(PlacemakerPrefabKind.Park, PlacemakerNames.Classify("BuildingPrefab", Observed));
            Assert.Equal(PlacemakerPrefabKind.Park, PlacemakerNames.Classify(null, Observed), "the name alone (prefab table before the load)");
            Assert.Equal(PlacemakerPrefabKind.None, PlacemakerNames.Classify("StaticObjectPrefab", Observed));
            Assert.Equal(PlacemakerPrefabKind.Overlay, PlacemakerNames.Classify("SurfacePrefab", "Placemaker Overlay (Missing Area)"));
            Assert.Equal(PlacemakerPrefabKind.None, PlacemakerNames.Classify("BuildingPrefab", "Placemaker Overlay (Missing Area)"));
            // the prefabs every peer's Placemaker creates by itself are not on-demand ones
            foreach (string own in new[] { "Placemaker Area", "Placemaker Zone Block", "Placemaker Parking Companion", "Placemaker Vehicle Car 1", "CityPark01" })
            {
                Assert.Equal(PlacemakerPrefabKind.None, PlacemakerNames.Classify(null, own), own);
            }
        }

        [Test]
        public void MessagesRoundTrip()
        {
            PlacemakerMessage ensure = PlacemakerMessage.EnsurePrefab("SurfacePrefab", "Placemaker Overlay (Gravel)");
            Assert.True(PlacemakerMessage.TryRead(ensure.ToBytes(), out PlacemakerMessage m));
            Assert.Equal(PlacemakerMessageKind.EnsurePrefab, m.Kind);
            Assert.Equal("SurfacePrefab", m.PrefabType);
            Assert.Equal("Placemaker Overlay (Gravel)", m.PrefabName);
            Assert.Equal((byte)PlacemakerMessageKind.EnsurePrefab, ensure.ToBytes()[0], "the kind is the first byte (channel permissions)");

            PlacemakerMessage park = PlacemakerMessage.ParkCompanion(42, Observed, -1234.5f, 678.25f, 1.5f);
            Assert.True(PlacemakerMessage.TryRead(park.ToBytes(), out m));
            Assert.Equal(PlacemakerMessageKind.ParkCompanion, m.Kind);
            Assert.Equal(42, m.Group);
            Assert.Equal(Observed, m.PrefabName);
            Assert.Equal(-1234.5f, m.X);
            Assert.Equal(678.25f, m.Z);
            Assert.Equal(1.5f, m.Yaw);

            Assert.True(PlacemakerMessage.TryRead(PlacemakerMessage.ParkingCompanion(7).ToBytes(), out m));
            Assert.Equal(PlacemakerMessageKind.ParkingCompanion, m.Kind);
            Assert.Equal(7, m.Group);
        }

        [Test]
        public void InvalidMessagesAreRefusedOnBothEnds()
        {
            var invalid = new[]
            {
                PlacemakerMessage.EnsurePrefab("SurfacePrefab", "CityPark01"),
                PlacemakerMessage.EnsurePrefab("BuildingPrefab", "Placemaker Overlay (Gravel)"),
                PlacemakerMessage.EnsurePrefab("", Observed),
                PlacemakerMessage.ParkCompanion(0, Observed, 0, 0, 0),
                PlacemakerMessage.ParkCompanion(1, "Placemaker Park a1", 0, 0, 0),
                PlacemakerMessage.ParkCompanion(1, Observed, float.NaN, 0, 0),
                PlacemakerMessage.ParkCompanion(1, Observed, 0, float.PositiveInfinity, 0),
                PlacemakerMessage.ParkCompanion(1, Observed, 20001f, 0, 0),
                PlacemakerMessage.ParkCompanion(1, Observed, 0, 0, 7f),
                PlacemakerMessage.ParkingCompanion(-3),
            };
            foreach (PlacemakerMessage m in invalid)
            {
                Assert.False(m.IsValid(), m.Kind + " " + m.PrefabName);
                Assert.Throws<ArgumentException>(() => m.ToBytes());
            }
            byte[] good = PlacemakerMessage.ParkCompanion(42, Observed, 1, 2, 0).ToBytes();
            Assert.False(PlacemakerMessage.TryRead(null, out _));
            Assert.False(PlacemakerMessage.TryRead(new byte[0], out _));
            Assert.False(PlacemakerMessage.TryRead(new byte[] { 9, 0, 0 }, out _), "unknown kind");
            var longer = new byte[good.Length + 1];
            Array.Copy(good, longer, good.Length);
            Assert.False(PlacemakerMessage.TryRead(longer, out _), "trailing bytes");
            for (int cut = 0; cut < good.Length; cut++)
            {
                var part = new byte[cut];
                Array.Copy(good, part, cut);
                Assert.False(PlacemakerMessage.TryRead(part, out _), "cut at " + cut);
            }
        }

        [Test]
        public void ReadingRandomBytesNeverThrows()
        {
            var random = new Random(163638);
            byte[] good = PlacemakerMessage.ParkCompanion(42, Observed, 1, 2, 0).ToBytes();
            for (int i = 0; i < 3000; i++)
            {
                byte[] bytes;
                if (i % 2 == 0)
                {
                    bytes = new byte[random.Next(0, 64)];
                    random.NextBytes(bytes);
                    if (bytes.Length > 0) bytes[0] = (byte)random.Next(0, 5);
                }
                else
                {
                    bytes = (byte[])good.Clone();
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }
                Assert.DoesNotThrow(() =>
                {
                    if (PlacemakerMessage.TryRead(bytes, out PlacemakerMessage m)) Assert.True(m.IsValid(), "only valid messages come out");
                });
            }
        }

        [Test]
        public void TheBookAdmitsABoundedNumberOfPrefabs()
        {
            var book = new RuntimePrefabBook();
            for (int i = 0; i < RuntimePrefabBook.MaxPrefabs; i++) Assert.True(book.Admit("p" + i));
            Assert.True(book.Admit("p0"), "an admitted prefab stays admitted");
            Assert.False(book.Admit("one more"));
            Assert.False(book.Admit(null));
            Assert.Equal(RuntimePrefabBook.MaxPrefabs, book.Admitted);
            book.Clear();
            Assert.True(book.Admit("one more"), "a new session starts empty");
        }

        [Test]
        public void WaitingRecordsAreAskedForOnceThePrefabExists()
        {
            var book = new RuntimePrefabBook();
            Assert.False(book.Wait(5, 100), "not tracked");
            book.Track(5, "BuildingPrefab", Observed, 10.0);
            book.Track(5, "BuildingPrefab", "other", 50.0); // kept
            Assert.True(book.TryGet(5, out string type, out string name));
            Assert.Equal("BuildingPrefab", type);
            Assert.Equal(Observed, name);
            Assert.True(book.Wait(5, 300));
            Assert.True(book.Wait(5, 100));
            Assert.True(book.Wait(5, 300));
            Assert.False(book.Wait(5, 0));
            var ids = new List<uint>();
            book.Resolve(5, ids);
            Assert.SequenceEqual(new uint[] { 100, 300 }, ids);
            Assert.False(book.IsTracked(5));
            book.Resolve(5, ids);
            Assert.Equal(2, ids.Count, "resolved once");
        }

        [Test]
        public void TheWaitingListIsBounded()
        {
            var book = new RuntimePrefabBook();
            book.Track(1, null, "x", 0);
            for (uint i = 1; i <= RuntimePrefabBook.MaxWaitingPerPrefab; i++) Assert.True(book.Wait(1, i));
            Assert.False(book.Wait(1, 999999));
            Assert.True(book.Wait(1, 1), "a record already waiting");
        }

        [Test]
        public void PrefabsNotCreatedInTimeAreGivenUp()
        {
            var book = new RuntimePrefabBook();
            book.Track(1, null, "a", 0.0);
            book.Track(2, null, "b", 100.0);
            Assert.Equal(0, book.Expire(RuntimePrefabBook.GiveUpSeconds).Count);
            Assert.SequenceEqual(new uint[] { 1 }, book.Expire(RuntimePrefabBook.GiveUpSeconds + 1));
            Assert.SequenceEqual(new uint[] { 2 }, book.TrackedIds());
            book.Drop(2);
            Assert.Equal(0, book.Tracked);
        }
    }
}
