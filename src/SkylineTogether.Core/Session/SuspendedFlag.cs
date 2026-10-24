namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// A game flag a session turns off for its own duration and gives back afterwards (the joiner's
    /// <c>PlatformManager.achievementsEnabled</c>). The value from before the first suspension is kept, so suspending
    /// again (a rejoin loads a fresh snapshot) never records the session's own <c>false</c> as the player's choice.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class SuspendedFlag
    {
        private bool _held;
        private bool _before;

        /// <summary>True while a value from before the session is held.</summary>
        public bool Held => _held;

        /// <summary>Records <paramref name="current"/> (first call only) and returns the value to set: false.</summary>
        public bool Suspend(bool current)
        {
            if (!_held)
            {
                _held = true;
                _before = current;
            }
            return false;
        }

        /// <summary>
        /// True when something switched the flag back on while the session holds it off (Achievement Fixer does so after
        /// every load): the session sets it off again.
        /// </summary>
        public bool NeedsReassert(bool current) => _held && current;

        /// <summary>The value to put back when one is held; releases it.</summary>
        public bool TryRestore(out bool value)
        {
            value = _before;
            if (!_held) return false;
            _held = false;
            return true;
        }

        /// <summary>Drops the held value without restoring it (something else decides the flag from here on, e.g. the
        /// main menu sets it for the city it loads).</summary>
        public void Forget() => _held = false;
    }
}
