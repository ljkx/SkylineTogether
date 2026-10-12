using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The mod compatibility records (kinds 110-119): enabled bits and mod settings.</summary>
    public sealed class ModRecordTests
    {
        private static List<SyncRecord> RoundTrip(SyncBatch.Writer w)
        {
            ArraySegment<byte> s = w.Finish();
            var copy = new byte[s.Count];
            Buffer.BlockCopy(s.Array, s.Offset, copy, 0, s.Count);
            return SyncBatch.Read(copy, 0, copy.Length);
        }

        [Test]
        public void EnabledBitsFollowTheirEntityRecord()
        {
            var w = new SyncBatch.Writer();
            w.BeginEntity(SyncRecordKind.Spawn, 77, Family.Object, true);
            w.AddComponent(0x1234UL, SyncComponentKind.Presence, null, 0, 0);
            w.EndEntity();
            w.EnabledBits(77, new[] { new SyncEnabledBit(0x1234UL, false), new SyncEnabledBit(0xFFFFFFFFFFFFFFFFUL, true) });
            List<SyncRecord> records = RoundTrip(w);
            Assert.Equal(2, records.Count);
            SyncRecord bits = records[1];
            Assert.Equal(SyncRecordKind.EnabledBits, bits.Kind);
            Assert.Equal(77u, bits.NetId);
            Assert.Equal(2, bits.EnabledBits.Count);
            Assert.Equal(0x1234UL, bits.EnabledBits[0].TypeHash);
            Assert.False(bits.EnabledBits[0].Enabled);
            Assert.True(bits.EnabledBits[1].Enabled);
        }

        [Test]
        public void ModSettingsRoundTrip()
        {
            var w = new SyncBatch.Writer();
            w.ModSettings(new[]
            {
                new SettingEntry { Mod = "Water_Features", Key = "EvaporationRate", Value = "0.0001" },
                new SettingEntry { Mod = "Anarchy", Key = "AllowPlacingMultipleUniqueBuildings", Value = "true" },
                new SettingEntry { Mod = null, Key = "", Value = null },
            });
            List<SyncRecord> records = RoundTrip(w);
            Assert.Equal(1, records.Count);
            Assert.Equal(SyncRecordKind.ModSettings, records[0].Kind);
            List<SettingEntry> s = records[0].Settings;
            Assert.Equal(3, s.Count);
            Assert.Equal("Water_Features", s[0].Mod);
            Assert.Equal("EvaporationRate", s[0].Key);
            Assert.Equal("0.0001", s[0].Value);
            Assert.Equal("true", s[1].Value);
            Assert.Equal("", s[2].Mod);
            Assert.Equal("", s[2].Value);
        }

        [Test]
        public void TruncatedModRecordsAreDecodeExceptions()
        {
            var w = new SyncBatch.Writer();
            w.EnabledBits(5, new[] { new SyncEnabledBit(1, true) });
            w.ModSettings(new[] { new SettingEntry { Mod = "a", Key = "b", Value = "c" } });
            ArraySegment<byte> s = w.Finish();
            for (int cut = SyncBatch.HeaderBytes + 1; cut < s.Count; cut++)
            {
                var copy = new byte[cut];
                Buffer.BlockCopy(s.Array, s.Offset, copy, 0, cut);
                Assert.Throws<DecodeException>(() => SyncBatch.Read(copy, 0, copy.Length), "cut at " + cut);
            }
        }

        [Test]
        public void OversizedListsAreRefusedByTheWriter()
        {
            var w = new SyncBatch.Writer();
            var settings = new SettingEntry[SyncBatch.MaxModSettings + 1];
            for (int i = 0; i < settings.Length; i++) settings[i] = new SettingEntry { Mod = "m", Key = "k" + i, Value = "v" };
            Assert.Throws<ArgumentException>(() => w.ModSettings(settings));
            Assert.Throws<ArgumentException>(() => w.EnabledBits(1, new SyncEnabledBit[600]));
        }
    }
}
