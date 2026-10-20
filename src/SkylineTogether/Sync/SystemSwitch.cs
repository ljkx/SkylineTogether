using System.Collections.Generic;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The original Enabled state of every vanilla system a follower component turns off (gate, follower clock, mover
    /// client, the recovery hold). Several components may switch off the same system; the first records the true
    /// original, each holds it off once, and the system gets its original state back only when the last holder
    /// releases it, no matter in which order they restore (e.g. the recovery hold on <c>AutoSaveSystem</c> ends while
    /// the gate of a rejoined session already holds it again).
    /// </summary>
    public static class SystemSwitch
    {
        private sealed class Entry
        {
            public bool Original;
            public int Holders;
        }

        private static readonly Dictionary<ComponentSystemBase, Entry> Entries = new Dictionary<ComponentSystemBase, Entry>();

        /// <summary>Systems currently held off (diagnostics).</summary>
        public static int HeldCount => Entries.Count;

        /// <summary>Records the system's original state on first use and returns it; then turns it off (one more holder).</summary>
        public static bool TurnOff(ComponentSystemBase system)
        {
            if (!Entries.TryGetValue(system, out Entry e))
            {
                e = new Entry { Original = system.Enabled };
                Entries[system] = e;
            }
            e.Holders++;
            system.Enabled = false;
            return e.Original;
        }

        /// <summary>
        /// Holds <paramref name="system"/> off again when something switched it back on (a mod that sets its own systems'
        /// <c>Enabled</c> when a city finished loading ran them with the client's settings for the rest of the session).
        /// The owner's wish counts when the hold ends: the system's original state becomes on. Returns true when it was
        /// switched off again; a system this class does not hold is left alone.
        /// </summary>
        public static bool Reassert(ComponentSystemBase system)
        {
            if (system == null || !system.Enabled || !Entries.TryGetValue(system, out Entry e)) return false;
            e.Original = true;
            system.Enabled = false;
            return true;
        }

        /// <summary>
        /// Releases one hold; when it was the last, the system gets its recorded original state back. A system this
        /// class does not know (e.g. the world was recreated) gets <paramref name="original"/>.
        /// </summary>
        public static void Restore(ComponentSystemBase system, bool original)
        {
            if (!Entries.TryGetValue(system, out Entry e))
            {
                system.Enabled = original;
                return;
            }
            if (--e.Holders > 0) return;
            Entries.Remove(system);
            system.Enabled = e.Original;
        }
    }
}
