namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// Generations of the joiner's downloads (the game layer's snapshot loader): every new download target is a new
    /// generation, and an asynchronous step (the import of the host's bundled assets, the city load) remembers the
    /// generation it started for. When it completes for an older generation (a rejoin opened a newer download meanwhile)
    /// it must release only its own resources and must not start a load on the newer, still incomplete download.
    /// <see cref="Busy"/> counts the steps of every generation, so no new join starts while any of them runs. Pure
    /// bookkeeping; main thread only.
    /// </summary>
    public sealed class LoadGeneration
    {
        private int _currentRunning;

        /// <summary>The generation of the current download.</summary>
        public int Current { get; private set; }

        /// <summary>Asynchronous steps still running, of any generation.</summary>
        public int Running { get; private set; }

        /// <summary>True while any step runs (a new join must wait).</summary>
        public bool Busy => Running > 0;

        /// <summary>True while a step of the current generation runs (its completion releases the current download).</summary>
        public bool CurrentBusy => _currentRunning > 0;

        /// <summary>The session ended while a step of the current generation ran: its completion disposes instead of continuing.</summary>
        public bool DisposeRequested { get; set; }

        /// <summary>A new download target opens: returns its generation (a pending dispose request belonged to the old one).</summary>
        public int Next()
        {
            Current++;
            _currentRunning = 0;
            DisposeRequested = false;
            return Current;
        }

        /// <summary>An asynchronous step starts for <paramref name="generation"/>.</summary>
        public void Started(int generation)
        {
            Running++;
            if (generation == Current) _currentRunning++;
        }

        /// <summary>
        /// The step of <paramref name="generation"/> completed. True when it is still the current generation (the caller
        /// goes on with the download); false when a newer download replaced it (the caller releases its own resources only).
        /// </summary>
        public bool Finished(int generation)
        {
            if (Running > 0) Running--;
            if (generation != Current) return false;
            if (_currentRunning > 0) _currentRunning--;
            return true;
        }
    }
}
