using System.Collections.Generic;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// <see cref="ModStateLedger"/> with <see cref="ModStateProposalRule"/>, the way the client's capture uses them (R3 G2b):
    /// a change derived by the client's own mod system without input is consumed, and the player's next click, seconds
    /// later, proposes nothing; a change made with input is proposed once; the once-a-second limit and the slow sweep keep
    /// the time a change may have happened.
    /// </summary>
    public sealed class ModStateLedgerTests
    {
        /// <summary>The client's capture: collects the changes of the fast path, the held-back ones and the sweep's; returns those it proposes.</summary>
        private sealed class Client
        {
            public readonly ModStateLedger Ledger = new ModStateLedger();
            public double LastInput = double.NegativeInfinity;
            public readonly Dictionary<uint, (ulong P, ulong V)> State = new Dictionary<uint, (ulong, ulong)>();
            public readonly HashSet<uint> Touched = new HashSet<uint>(); // entities in chunks whose version changed

            public List<uint> Capture(double now)
            {
                var proposed = new List<uint>();
                double since = Ledger.BeginCollect(now);
                foreach (uint id in Touched) Consider(id, since, now, proposed);
                Touched.Clear();
                var retries = new List<(uint, double)>();
                Ledger.TakeRetries(now, retries);
                foreach ((uint id, double held) in retries) Consider(id, held, now, proposed);
                var sweep = new List<uint>();
                Ledger.NextSweep(256, now, sweep, out double sweepSince);
                foreach (uint id in sweep) Consider(id, sweepSince, now, proposed);
                return proposed;
            }

            private void Consider(uint id, double since, double now, List<uint> proposed)
            {
                (ulong p, ulong v) = State[id];
                if (!Ledger.Observe(id, p, v, false, since, now, out ModStateLedger.Change c)) return;
                if (ModStateProposalRule.MayPropose(false, c.Since, now, LastInput)) proposed.Add(id);
            }
        }

        private static Client Baseline(double now)
        {
            var c = new Client();
            c.Ledger.Reset(now);
            c.State[1] = (1, 100);
            c.State[2] = (1, 200);
            foreach (KeyValuePair<uint, (ulong P, ulong V)> pair in c.State) c.Ledger.Set(pair.Key, pair.Value.P, pair.Value.V);
            return c;
        }

        [Test]
        public void ADerivedChangeIsNotProposedByAClickTenSecondsLater()
        {
            Client c = Baseline(0);
            Assert.Equal(0, c.Capture(1).Count);
            c.State[1] = (1, 101); // the client's own mod system recomputed it, no input
            c.Touched.Add(1);
            Assert.Equal(0, c.Capture(2).Count, "derived: not proposed");
            for (double t = 2.1; t < 12; t += 0.1) Assert.Equal(0, c.Capture(t).Count);
            c.LastInput = 12; // the player clicks a speed button
            for (double t = 12; t < 20; t += 0.1) Assert.Equal(0, c.Capture(t).Count, "consumed: no later input proposes it (" + t + ")");
        }

        [Test]
        public void AChangeRightAfterTheInputIsProposedOnce()
        {
            Client c = Baseline(0);
            c.Capture(1);
            c.LastInput = 4.9; // a click on a mod's panel
            c.State[2] = (1, 201);
            c.Touched.Add(2);
            Assert.SequenceEqual(new uint[] { 2 }, c.Capture(5));
            for (double t = 5.1; t < 10; t += 0.1) Assert.Equal(0, c.Capture(t).Count, "reported once");
        }

        [Test]
        public void AChangeHeldBackByTheLimitKeepsTheTimeItWasFirstSeen()
        {
            Client c = Baseline(0);
            c.LastInput = 0.9;
            c.State[1] = (1, 101);
            c.Touched.Add(1);
            Assert.SequenceEqual(new uint[] { 1 }, c.Capture(1)); // an edit
            c.State[1] = (1, 102); // the same slider 0.3 s later: held back by the once-a-second limit
            c.Touched.Add(1);
            Assert.Equal(0, c.Capture(1.3).Count);
            // reported when the limit ends, 5 s after the input: it still counts from when it was first seen (1.0 .. 1.3)
            c.Ledger.BeginCollect(1.4);
            var retries = new List<(uint Id, double Since)>();
            c.Ledger.TakeRetries(5.9, retries);
            Assert.Equal(1, retries.Count);
            Assert.Equal(1u, retries[0].Id);
            Assert.True(retries[0].Since <= 1.3 && retries[0].Since >= 1.0, "since " + retries[0].Since);
            Assert.True(c.Ledger.Observe(1, 1, 102, false, retries[0].Since, 5.9, out ModStateLedger.Change change));
            Assert.True(ModStateProposalRule.MayPropose(false, change.Since, 5.9, c.LastInput), "the slider's later value is still the player's edit");
        }

        [Test]
        public void TheSweepFindsWhatTheChangeVersionsMissedWithThePreviousPassAsItsStart()
        {
            var ledger = new ModStateLedger();
            ledger.Reset(0);
            for (uint id = 1; id <= 10; id++) ledger.Set(id, 1, id);
            var batch = new List<uint>();
            ledger.NextSweep(4, 1, batch, out double since);
            Assert.SequenceEqual(new uint[] { 1, 2, 3, 4 }, batch);
            Assert.Equal(0.0, since, "the first pass: since the baseline");
            ledger.NextSweep(4, 2, batch, out _);
            ledger.NextSweep(4, 3, batch, out _);
            Assert.Equal(10, batch.Count, "one pass covers every known id");
            batch.Clear();
            ledger.NextSweep(4, 4, batch, out since); // a new pass starts at 4
            Assert.Equal(1.0, since, "the previous pass started at 1");
            Assert.True(ledger.Observe(3, 1, 33, false, since, 4, out ModStateLedger.Change change));
            Assert.Equal(1.0, change.Since);
            Assert.False(change.PresenceChanged);
            Assert.True(ledger.Observe(4, 2, 4, false, since, 4, out change));
            Assert.True(change.PresenceChanged, "a watched type added or removed");
            ledger.Forget(4);
            Assert.False(ledger.IsKnown(4));
        }

        [Test]
        public void AModDataEntityIsReportedWithoutTheLimit()
        {
            var ledger = new ModStateLedger();
            ledger.Reset(0);
            ledger.Set(7, 1, 1);
            Assert.True(ledger.Observe(7, 1, 2, true, 0, 1, out _));
            Assert.True(ledger.Observe(7, 1, 3, true, 1, 1.1, out _), "urgent");
            Assert.False(ledger.Observe(7, 1, 3, true, 1, 1.2, out _), "unchanged");
            Assert.False(ledger.Observe(7, 1, 3, false, 1, 1.2, out _), "unchanged");
            Assert.False(ledger.Observe(7, 1, 4, false, 1, 1.5, out _), "an ordinary entity within the limit is held back");
        }
    }
}
