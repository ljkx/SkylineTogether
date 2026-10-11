using System;
using System.Collections.Generic;
using SkylineTogether.Core.Follower;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Follower
{
    /// <summary>
    /// <see cref="CosmeticSchedule"/>: the interval/offset arithmetic of <c>UpdateSystem.Update(phase, updateIndex,
    /// iterationIndex)</c> (<c>UpdateSystem.cs:240-248</c>) checked against hand-computed frame tables, and the stable
    /// auto offsets checked against golden values computed with an independent XXH64 implementation.
    /// </summary>
    public sealed class CosmeticScheduleTests
    {
        // the real cosmetic systems (cosmetic.json) with their GetUpdateInterval(GameSimulation); none overrides
        // GetUpdateOffset, so every one gets the hashed offset (decompiled Game.Simulation sources)
        private static readonly (string Name, int Interval, int GoldenOffset)[] CosmeticSystems =
        {
            ("Game.Simulation.TimeSystem", 1, 0),
            ("Game.Simulation.ClimateSystem", 1, 0),
            ("Game.Simulation.SnowSystem", 4, 0),                    // SnowSystem.cs:226-229
            ("Game.Simulation.WindSimulationSystem", 512, 196),      // kUpdateInterval = 512
            ("Game.Simulation.WindSystem", 512, 476),
            ("Game.Simulation.ZoneAmbienceSystem", 2048, 421),       // 262144 / kUpdatesPerDay (128)
            ("Game.Simulation.SpawnableAmbienceSystem", 128, 75),    // 262144 / (128 * 16)
            ("Game.Simulation.WetnessSystem", 256, 167),
            ("Game.Simulation.DirtynessSystem", 256, 238),
        };

        [Test]
        public void HandComputedFrameTable()
        {
            var schedule = new CosmeticSchedule();
            schedule.Add("T.EveryTick", 1, -1);
            schedule.Add("T.Odd", 2, 1);
            schedule.Add("T.FourZero", 4, 0);
            schedule.Add("T.FourThree", 4, 3);

            // frames 1..8, the incremented frameIndex of each tick as SimulationSystem passes it
            bool[] everyTick = { true, true, true, true, true, true, true, true };
            bool[] odd = { true, false, true, false, true, false, true, false };
            bool[] fourZero = { false, false, false, true, false, false, false, true };
            bool[] fourThree = { false, false, true, false, false, false, true, false };
            for (uint frame = 1; frame <= 8; frame++)
            {
                int i = (int)frame - 1;
                Assert.Equal(everyTick[i], schedule.ShouldRun("T.EveryTick", frame), "every tick, frame " + frame);
                Assert.Equal(odd[i], schedule.ShouldRun("T.Odd", frame), "interval 2 offset 1, frame " + frame);
                Assert.Equal(fourZero[i], schedule.ShouldRun("T.FourZero", frame), "interval 4 offset 0, frame " + frame);
                Assert.Equal(fourThree[i], schedule.ShouldRun("T.FourThree", frame), "interval 4 offset 3, frame " + frame);
            }
        }

        [Test]
        public void AutoOffsetsMatchGoldenValues()
        {
            foreach ((string name, int interval, int golden) in CosmeticSystems)
            {
                Assert.Equal(golden, CosmeticSchedule.AutoOffset(name, interval), name);
            }
            // fold32 of XXH64("Mod.Cosmetic.SparkleSystem") is 0x2212C19B; & 63 = 27
            Assert.Equal(27, CosmeticSchedule.AutoOffset("Mod.Cosmetic.SparkleSystem", 64));
            Assert.Equal(0x2212C19B & 1023, CosmeticSchedule.AutoOffset("Mod.Cosmetic.SparkleSystem", 1024));
        }

        [Test]
        public void CosmeticListRunsAtItsHashedOffsets()
        {
            var schedule = new CosmeticSchedule();
            foreach ((string name, int interval, _) in CosmeticSystems) schedule.Add(name, interval, -1);
            Assert.Equal(CosmeticSystems.Length, schedule.Count);

            var runs = new Dictionary<string, List<uint>>();
            foreach ((string name, _, _) in CosmeticSystems) runs[name] = new List<uint>();
            for (uint frame = 1; frame <= 4096; frame++)
            {
                foreach ((string name, _, _) in CosmeticSystems)
                {
                    if (schedule.ShouldRun(name, frame)) runs[name].Add(frame);
                }
            }
            foreach ((string name, int interval, int golden) in CosmeticSystems)
            {
                List<uint> frames = runs[name];
                Assert.Equal(4096 / interval, frames.Count, name + " runs once per interval");
                uint first = golden == 0 ? (uint)interval : (uint)golden;   // frame 0 is not part of the window 1..4096
                Assert.Equal(first, frames[0], name + " first run");
                for (int i = 1; i < frames.Count; i++) Assert.Equal((uint)interval, frames[i] - frames[i - 1], name + " spacing");
            }
            Assert.SequenceEqual(new uint[] { 167, 423, 679, 935 }, runs["Game.Simulation.WetnessSystem"].GetRange(0, 4));
            Assert.SequenceEqual(new uint[] { 421, 2469 }, runs["Game.Simulation.ZoneAmbienceSystem"]);
            Assert.SequenceEqual(new uint[] { 4, 8, 12 }, runs["Game.Simulation.SnowSystem"].GetRange(0, 3));
        }

        [Test]
        public void FrameIndexUsesUnsignedMasking()
        {
            var schedule = new CosmeticSchedule();
            schedule.Add("T.FourThree", 4, 3);
            schedule.Add("T.Big", 1 << 30, 5);
            Assert.True(schedule.ShouldRun("T.FourThree", uint.MaxValue), "0xFFFFFFFF & 3 == 3");
            Assert.False(schedule.ShouldRun("T.FourThree", 0));
            Assert.True(schedule.ShouldRun("T.Big", 5));
            Assert.True(schedule.ShouldRun("T.Big", 5u + (1u << 30)));
            Assert.False(schedule.ShouldRun("T.Big", 6));
        }

        [Test]
        public void ResetDependencyFollowsTheIterationRule()
        {
            var schedule = new CosmeticSchedule();
            schedule.Add("T.EveryTick", 1, -1);
            schedule.Add("T.Four", 4, 0);
            Assert.False(schedule.ShouldResetDependency("T.EveryTick", 0), "first update of the frame keeps the dependency");
            Assert.True(schedule.ShouldResetDependency("T.EveryTick", 1));
            Assert.True(schedule.ShouldResetDependency("T.EveryTick", 15));
            Assert.False(schedule.ShouldResetDependency("T.Four", 3));
            Assert.True(schedule.ShouldResetDependency("T.Four", 4));
            Assert.False(schedule.ShouldResetDependency("T.Unknown", 100));
            Assert.False(schedule.ShouldResetDependency(null, 100));
        }

        [Test]
        public void RunnerLoopOverOneFrameWithThreeTicks()
        {
            // ClientClockSystem: frameIndex 4, 5, 6 with iterations 0, 1, 2 (ARCH §6.1 step 3)
            var schedule = new CosmeticSchedule();
            schedule.Add("T.EveryTick", 1, 0);
            schedule.Add("T.Two", 2, 0);
            var log = new List<string>();
            uint frameIndex = 3;
            for (int iteration = 0; iteration < 3; iteration++)
            {
                frameIndex++;
                foreach (string name in schedule.SystemTypeNames)
                {
                    if (!schedule.ShouldRun(name, frameIndex)) continue;
                    log.Add(name + "@" + frameIndex + (schedule.ShouldResetDependency(name, iteration) ? "+reset" : string.Empty));
                }
            }
            Assert.SequenceEqual(new[] { "T.EveryTick@4", "T.Two@4", "T.EveryTick@5+reset", "T.EveryTick@6+reset", "T.Two@6+reset" }, log);
        }

        [Test]
        public void NegativeOffsetsAreAutomatic()
        {
            var schedule = new CosmeticSchedule();
            schedule.Add("Game.Simulation.WetnessSystem", 256, -1);
            schedule.Add("Game.Simulation.DirtynessSystem", 256, -7);
            Assert.True(schedule.TryGetSchedule("Game.Simulation.WetnessSystem", out int interval, out int offset));
            Assert.Equal(256, interval);
            Assert.Equal(167, offset);
            Assert.True(schedule.TryGetSchedule("Game.Simulation.DirtynessSystem", out _, out offset));
            Assert.Equal(238, offset, "every negative offset means 'assign one', as UpdateSystem treats m_Offset < 0");
            Assert.False(schedule.TryGetSchedule("T.Missing", out interval, out offset));
            Assert.Equal(0, interval);
        }

        [Test]
        public void RejectsWhatUpdateSystemRejects()
        {
            var schedule = new CosmeticSchedule();
            foreach (int bad in new[] { 0, -1, -4, 3, 6, 12, int.MaxValue, int.MinValue })
            {
                int interval = bad;
                Assert.Throws<ArgumentOutOfRangeException>(() => schedule.Add("T.Bad", interval, -1), "interval " + bad);
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => schedule.Add("T.Bad", 4, 4), "offset == interval never runs");
            Assert.Throws<ArgumentOutOfRangeException>(() => schedule.Add("T.Bad", 4, 9));
            Assert.Throws<ArgumentException>(() => schedule.Add(null, 4, 0));
            Assert.Throws<ArgumentException>(() => schedule.Add(string.Empty, 4, 0));
            Assert.Equal(0, schedule.Count, "rejected systems are not scheduled");
            Assert.Throws<ArgumentNullException>(() => CosmeticSchedule.AutoOffset(null, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => CosmeticSchedule.AutoOffset("T.X", 3));
        }

        [Test]
        public void AddingAgainReplacesAndKeepsTheOrder()
        {
            var schedule = new CosmeticSchedule();
            schedule.Add("T.A", 4, 1);
            schedule.Add("T.B", 1, 0);
            schedule.Add("T.A", 8, 2);
            Assert.Equal(2, schedule.Count);
            Assert.SequenceEqual(new[] { "T.A", "T.B" }, schedule.SystemTypeNames);
            Assert.True(schedule.TryGetSchedule("T.A", out int interval, out int offset));
            Assert.Equal(8, interval);
            Assert.Equal(2, offset);
            Assert.True(schedule.ShouldRun("T.A", 10));
            Assert.False(schedule.ShouldRun("T.A", 5), "the old schedule is gone");
        }

        [Test]
        public void UnknownAndRemovedSystemsNeverRun()
        {
            var schedule = new CosmeticSchedule();
            Assert.False(schedule.ShouldRun("T.Nothing", 0));
            Assert.False(schedule.ShouldRun(null, 0));
            schedule.Add("T.A", 1, 0);
            Assert.True(schedule.Contains("T.A"));
            Assert.True(schedule.Remove("T.A"));
            Assert.False(schedule.Remove("T.A"));
            Assert.False(schedule.Remove(null));
            Assert.False(schedule.Contains("T.A"));
            Assert.False(schedule.Contains(null));
            Assert.False(schedule.ShouldRun("T.A", 7));
            Assert.Equal(0, schedule.SystemTypeNames.Count);
        }

        [Test]
        public void TypeNamesAreCaseSensitive()
        {
            var schedule = new CosmeticSchedule();
            schedule.Add("Game.Simulation.TimeSystem", 1, 0);
            Assert.False(schedule.ShouldRun("game.simulation.timesystem", 1));
            // golden: fold32 of XXH64("game.simulation.wetnesssystem") is 0x03E988B4 (& 255 = 180, the proper name gives 167)
            Assert.Equal(180, CosmeticSchedule.AutoOffset("game.simulation.wetnesssystem", 256));
        }
    }
}
