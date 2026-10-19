using System.Collections.Generic;

namespace SkylineTogether.Core.Modding
{
    /// <summary>How a component type of a mod is stored (the ECS kind).</summary>
    public enum ComponentStorage : byte
    {
        /// <summary>An <c>IComponentData</c> struct with fields.</summary>
        Data = 0,
        /// <summary>An <c>IBufferElementData</c> struct.</summary>
        Buffer = 1,
        /// <summary>An <c>IComponentData</c> struct without fields.</summary>
        Tag = 2,
        /// <summary>An <c>ISharedComponentData</c>.</summary>
        Shared = 3,
        /// <summary>A class <c>IComponentData</c> (managed component).</summary>
        Managed = 4,
    }

    /// <summary>One ECS component type declared by a mod.</summary>
    public sealed class ComponentFact
    {
        /// <summary>Full type name (nested types with <c>+</c>).</summary>
        public string Name;
        public ComponentStorage Storage;
        /// <summary>Implements the game's <c>ISerializable</c> (saved with its value).</summary>
        public bool Serializable;
        /// <summary>Implements the game's <c>IEmptySerializable</c> (saved as presence only).</summary>
        public bool EmptySerializable;
        /// <summary>Implements <c>IEnableableComponent</c>.</summary>
        public bool Enableable;
        /// <summary>Implements <c>ISerializeAsEnabled</c> (the enabled bit is part of the saved value).</summary>
        public bool SerializeAsEnabled;
        /// <summary>A field (or a field of a field) is an <c>Entity</c>.</summary>
        public bool HasEntityReferences;

        public override string ToString() => Name;
    }

    /// <summary>One ECS system declared by a mod (tools included).</summary>
    public sealed class SystemFact
    {
        /// <summary>Full type name.</summary>
        public string Name;
        /// <summary>
        /// The update phases the system is registered in (names of the game's <c>SystemUpdatePhase</c>), empty when it is
        /// not registered anywhere. Only meaningful when <see cref="ModFacts.PhasesKnown"/>.
        /// </summary>
        public readonly List<string> Phases = new List<string>();
        /// <summary>The system saves its own state with the city (it implements <c>ISerializable</c>).</summary>
        public bool SavesState;
        /// <summary>A <c>ToolBaseSystem</c>.</summary>
        public bool IsTool;
        /// <summary>The system (or a nested type of it) uses <c>CreationDefinition</c>: the vanilla Temp pipeline.</summary>
        public bool UsesCreationDefinitions;
        /// <summary>Implements <c>IPostDeserialize</c> (runs after a city loaded).</summary>
        public bool PostDeserialize;

        /// <summary>
        /// The system (or a type nested in it, such as its jobs) calls an entity write of <c>EntityManager</c> or
        /// <c>EntityCommandBuffer</c> (add, set, remove, create, destroy, enable). A UI system that does writes the world
        /// when its player clicks, so it is not presentation (ARCHITECTURE.md#mod-compatibility).
        /// </summary>
        public bool WritesEntities;

        public override string ToString() => Name;
    }

    /// <summary>One property of a mod's <c>ModSetting</c> class.</summary>
    public sealed class SettingFact
    {
        /// <summary>Full name of the settings class.</summary>
        public string SettingsType;
        /// <summary>The property name.</summary>
        public string Name;
        /// <summary>A bool, number, enum or string property with a public getter and setter (the settings channel can carry it).</summary>
        public bool Supported;

        public override string ToString() => SettingsType + "." + Name;
    }

    /// <summary>
    /// What a scan of one code mod's assembly found (docs/design/MOD-COMPATIBILITY.md §6). The game layer fills it from
    /// type metadata, method bodies read as bytes, the game's system registrations and Harmony's patch list; no code of
    /// the mod runs for it. <see cref="ModClassifier"/> turns it into decisions. Plain data.
    /// </summary>
    public sealed class ModFacts
    {
        /// <summary>The mod's assembly name (its identity in the handshake, the settings channel and the mod channel).</summary>
        public string AssemblyName;
        /// <summary>The scan threw; the classifier then treats the mod as world-affecting (fail closed).</summary>
        public bool ScanFailed;
        public string ScanError;
        public readonly List<ComponentFact> Components = new List<ComponentFact>();
        public readonly List<SystemFact> Systems = new List<SystemFact>();
        /// <summary>Prefab types and prefab component types (<c>PrefabBase</c>, <c>ComponentBase</c> subclasses) of the mod.</summary>
        public readonly List<string> PrefabTypes = new List<string>();
        /// <summary>Methods that call <c>PrefabSystem.AddPrefab</c> or <c>AddOrUpdatePrefab</c> (the mod creates prefabs at runtime).</summary>
        public readonly List<string> RuntimePrefabCreators = new List<string>();
        public readonly List<SettingFact> Settings = new List<SettingFact>();
        /// <summary>The assembly references <c>0Harmony</c>.</summary>
        public bool ReferencesHarmony;
        /// <summary>
        /// The game (or other) methods this mod's Harmony patches target, as <c>Namespace.Type.Method</c>; null when
        /// Harmony's patch list could not be read (then <see cref="ReferencesHarmony"/> alone counts).
        /// </summary>
        public List<string> PatchedMethods;
        /// <summary>The system update phases could be read (false: every system's phases are unknown).</summary>
        public bool PhasesKnown;

        public override string ToString() => AssemblyName ?? "?";
    }
}
