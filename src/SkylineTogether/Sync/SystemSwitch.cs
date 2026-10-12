using System.Collections.Generic;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The original Enabled state of every vanilla system a follower component turns off (gate, follower clock, mover
    /// client). Several components may switch off the same system; each records the true original from here, so the
    /// game ends up as it was no matter in which order they restore.
    /// </summary>
    public static class SystemSwitch
    {
        private static readonly Dictionary<ComponentSystemBase, bool> Originals = new Dictionary<ComponentSystemBase, bool>();

        /// <summary>Records the system's original state on first use and returns it; then turns it off.</summary>
        public static bool TurnOff(ComponentSystemBase system)
        {
            if (!Originals.TryGetValue(system, out bool original))
            {
                original = system.Enabled;
                Originals[system] = original;
            }
            system.Enabled = false;
            return original;
        }

        /// <summary>Restores a system to its original state.</summary>
        public static void Restore(ComponentSystemBase system, bool original)
        {
            system.Enabled = original;
            Originals.Remove(system);
        }
    }
}
