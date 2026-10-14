using System;
using System.Collections.Generic;
using System.Reflection;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// What Skyline Together must know about one third-party mod to keep it in sync (docs/design/MOD-COMPATIBILITY.md).
    /// An adapter is pure data plus optional hooks; it is active only when the mod's assembly is loaded, and every name
    /// in it is resolved by reflection at runtime (no compile-time reference to another mod), so a renamed type in a
    /// mod update costs a log line, never a crash. Most mod state needs no adapter at all: every serializable mod
    /// component replicates through the generic codec (<c>Sync.TypeHandlers</c>).
    /// </summary>
    public abstract class ModAdapter
    {
        protected static readonly string[] None = Array.Empty<string>();

        /// <summary>Readable name (logs, diagnostics).</summary>
        public abstract string ModName { get; }

        /// <summary>The mod's assembly name (detection).</summary>
        public abstract string AssemblyName { get; }

        /// <summary>The loaded assembly, or null when the mod is not running.</summary>
        public Assembly Assembly { get; internal set; }

        public bool Loaded => Assembly != null;

        /// <summary>
        /// The mod is loaded but a name this adapter relies on is gone (the mod was updated): the adapter is off and the mod
        /// gets the generic treatment (<see cref="ModCompat"/>, Core <c>AdapterCheck</c>).
        /// </summary>
        public string DisabledReason { get; internal set; }

        /// <summary>The mod is loaded and every name of the adapter resolved.</summary>
        public bool Active => Loaded && DisabledReason == null;

        /// <summary>
        /// Component types (full names, in the mod's assembly) that mark tool or UI entities: an entity carrying one is
        /// never replicated (Traffic's connectors and lane handles, Move It's control points and overlays).
        /// </summary>
        public virtual IEnumerable<string> LocalTypes => None;

        /// <summary>
        /// Vanilla component types (full names) the mod writes without update tags, watched for changes on entities that
        /// carry them (<see cref="ModStateWatcher"/>; Water Features edits <c>WaterSourceData</c> in place). Mod
        /// components are always watched.
        /// </summary>
        public virtual IEnumerable<string> WatchedVanillaTypes => None;

        /// <summary>
        /// Systems (full names in the mod's assembly) that derive host-owned state and must not run on a live client:
        /// the host's result is replicated instead (Plop the Growables' zone check and plop tagging).
        /// </summary>
        public virtual IEnumerable<string> ClientOffSystems => None;

        /// <summary>
        /// Systems disabled on a client from the moment it starts joining until the session ends: load-time mutators
        /// driven by the player's own settings (Historical Start's unlocks, 529 Tiles' tile unlocks). The host's city
        /// arrives already processed with the host's settings. <c>PostDeserialize:Type.Name</c> names the game's
        /// <c>PostDeserialize&lt;T&gt;</c> wrapper of an <c>IPostDeserialize</c> system.
        /// </summary>
        public virtual IEnumerable<string> JoinOffSystems => None;

        /// <summary>
        /// GameSimulation systems that keep running on a client although its simulation follows the host (the follower
        /// clock switches the phase off): only systems that write no replicated state, such as Water Features applying
        /// the (host's) evaporation and fluidness settings to the local water simulation.
        /// </summary>
        public virtual IEnumerable<string> ClientSimulationSystems => None;

        /// <summary>
        /// Tools of the mod (full names) that change the world only through the game's Temp/apply pipeline (or not at
        /// all): their applies are captured like vanilla ones, and their idle frames are not treated as direct writes.
        /// Every other tool of another mod counts as a direct-write tool (Move It, Tree Controller's brush, ...).
        /// </summary>
        public virtual IEnumerable<string> PipelineTools => None;

        /// <summary>Names of the mod's <c>ModSetting</c> properties whose host value applies to everyone.</summary>
        public virtual IEnumerable<string> SyncedSettings => None;

        /// <summary>Settings type full name when the mod has several <c>ModSetting</c> classes (null: the first one found).</summary>
        public virtual string SettingsTypeName => null;

        /// <summary>The settings type that holds <paramref name="key"/> (adapters built from declarations span several).</summary>
        public virtual string SettingsTypeFor(string key) => SettingsTypeName;

        /// <summary>
        /// Component types (full names, in the mod's assembly) the game saves but that must never be sent; the entities
        /// carrying them still replicate (declared <c>never-replicate</c> by the mod, docs/MOD-INTEGRATION.md).
        /// </summary>
        public virtual IEnumerable<string> NeverReplicatedTypes => None;

        /// <summary>
        /// Host placement checks of client proposals (ARCHITECTURE.md#host-validation-and-commit) that this mod legitimately
        /// bypasses while it is loaded on the host: Anarchy places anywhere (tiles, geometry), Find It and Plop the Growables
        /// place prefabs the toolbar would not offer (unlocks). Direct-write tools (Move It) need nothing here: their
        /// proposals are flagged and skip every placement check.
        /// </summary>
        public virtual Core.Sync.PlacementChecks RelaxedPlacementChecks => Core.Sync.PlacementChecks.None;

        /// <summary>One line for the diagnostics dump and the compatibility table.</summary>
        public abstract string Summary { get; }

        /// <summary>Resolves a type of the mod's assembly (null when the mod is not loaded or the type is gone).</summary>
        public Type FindType(string fullName)
        {
            if (Assembly == null || string.IsNullOrEmpty(fullName)) return null;
            try
            {
                return Assembly.GetType(fullName, false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override string ToString() => ModName + (Active ? " (active)" : Loaded ? " (disabled)" : "");
    }
}
