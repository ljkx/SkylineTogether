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

        /// <summary>
        /// The mod's live settings object when the mod keeps it somewhere else than on its <c>IMod</c> instance (Placemaker:
        /// a static property of its tool registration), or null to search the <c>IMod</c> instance (<see cref="ModSettingsSync"/>).
        /// </summary>
        public virtual object SettingsInstance() => null;

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

        /// <summary>
        /// Systems disabled on a client from the moment it starts joining until it is live, then switched back on right
        /// after <see cref="OnClientLive"/>: systems that must run in the session but whose post-load step would redo, with
        /// the joiner's own settings, what the host already did to the city (Placemaker's group registry repair).
        /// </summary>
        public virtual IEnumerable<string> LoadOffSystems => None;

        /// <summary>
        /// Mod component types (full names) whose new entities a mod creates on a client without update tags as the
        /// player's edit (Placemaker's group records and their carrier): the client binds them from its id pool and
        /// proposes them as spawns, under the same rule as other untagged mod state (a tool applying or recent input).
        /// Without this, a client only proposes new mod data entities that a proposed value references.
        /// </summary>
        public virtual IEnumerable<string> ClientCreatedTypes => None;

        /// <summary>
        /// Members (<c>Type::member</c>, full type name in the mod's assembly, a nested type after <c>+</c>) the adapter
        /// reads, writes or calls by reflection; checked like every other name (§5), so a renamed one turns the adapter off.
        /// </summary>
        public virtual IEnumerable<string> Members => None;

        /// <summary>
        /// Runtime prefabs the mod creates on demand from their type and name (docs/design/MOD-COMPATIBILITY.md §7):
        /// <see cref="Core.Modding.RuntimePrefabState.NotHandled"/> for anything else. The first call for a prefab asks the
        /// mod to create it; later calls report whether it is ready. <paramref name="type"/> is null when only the name is
        /// known (the host's prefab table read before the city is loaded). Main thread.
        /// </summary>
        public virtual Core.Modding.RuntimePrefabState ProvideRuntimePrefab(string type, string name) => Core.Modding.RuntimePrefabState.NotHandled;

        /// <summary>
        /// True for the type and name of a prefab <see cref="ProvideRuntimePrefab"/> creates (a pure name check; the session's
        /// limit of requests counts only these).
        /// </summary>
        public virtual bool HandlesRuntimePrefab(string type, string name) => false;

        /// <summary>The mod channel name of the adapter's own messages (<see cref="ModIntegration.RegisterInternal"/>), or null.</summary>
        public virtual string InternalChannel => null;

        /// <summary>Sends on <see cref="InternalChannel"/> (payload, target: -1 everyone, 0 the host, n player n); set by <see cref="ModCompat"/>.</summary>
        public Func<byte[], int, bool> ChannelSender { get; internal set; }

        /// <summary>A message on <see cref="InternalChannel"/> from player <paramref name="from"/> (the host receives clients').</summary>
        public virtual void OnChannelMessage(int from, byte[] payload)
        {
        }

        /// <summary>Client, live: called once, before the <see cref="LoadOffSystems"/> are switched back on.</summary>
        public virtual void OnClientLive()
        {
        }

        /// <summary>Every frame while a session runs (host, or a live client), main thread; the caller catches.</summary>
        public virtual void Tick(bool host, double now)
        {
        }

        /// <summary>Session end: forget the session's requests.</summary>
        public virtual void OnSessionEnded()
        {
        }

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

        /// <summary>
        /// Resolves a <see cref="Members"/> entry (<c>Type::member</c>): a field, property or method of any visibility
        /// declared by that type; null when the type or member is gone, or a method name is overloaded.
        /// </summary>
        public MemberInfo FindMember(string spec)
        {
            int sep = spec == null ? -1 : spec.IndexOf("::", StringComparison.Ordinal);
            if (sep <= 0) return null;
            Type type = FindType(spec.Substring(0, sep));
            string name = spec.Substring(sep + 2);
            if (type == null || name.Length == 0) return null;
            try
            {
                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                MemberInfo[] found = type.GetMember(name, MemberTypes.Field | MemberTypes.Property | MemberTypes.Method, Flags);
                return found.Length == 1 ? found[0] : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override string ToString() => ModName + (Active ? " (active)" : Loaded ? " (disabled)" : "");
    }
}
