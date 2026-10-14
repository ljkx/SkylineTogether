using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Modding
{
    /// <summary>How one component type of a mod travels.</summary>
    public enum ComponentTreatment : byte
    {
        /// <summary>Saved with its value: replicates through the generic codec, untagged changes are watched.</summary>
        Replicated = 0,
        /// <summary>Saved as presence only (<c>IEmptySerializable</c> or a saved tag): presence replicates.</summary>
        Presence = 1,
        /// <summary>Not saved by the game: local or derived data, never sent (the entity is unaffected).</summary>
        NotReplicated = 2,
        /// <summary>Declared <c>never-replicate</c>: excluded from the codec although the game saves it.</summary>
        Excluded = 3,
        /// <summary>Declared <c>tool-local</c>: an entity carrying it never replicates.</summary>
        ToolLocal = 4,
    }

    /// <summary>What happens to one system of a mod on a client.</summary>
    public enum SystemTreatment : byte
    {
        /// <summary>Runs on every peer (derivation from replicated entities, like vanilla Modification systems).</summary>
        Everywhere = 0,
        /// <summary>A GameSimulation system: off on a following client like every system of that phase.</summary>
        OffBySimulation = 1,
        /// <summary>Off on live clients (declared <c>host-only</c>, or it runs with the simulation step).</summary>
        HostOnly = 2,
        /// <summary>Declared <c>everywhere</c>: kept on clients even in a simulation phase.</summary>
        KeptOnClients = 3,
        /// <summary>Declared <c>join-off</c>: off on a client from the start of its join.</summary>
        JoinOff = 4,
        /// <summary>Only presentation phases (UI, rendering): local by nature.</summary>
        Presentation = 5,
        /// <summary>Editor simulation only: never runs in a game.</summary>
        Editor = 6,
        /// <summary>Registered in no phase (created on demand; another system updates it, if anything).</summary>
        NotScheduled = 7,
    }

    /// <summary>How a mod's tool is captured on a client.</summary>
    public enum ToolTreatment : byte
    {
        /// <summary>Changes made while it is active are proposed as direct writes (edit hold against the host's echoes).</summary>
        DirectWrite = 0,
        /// <summary>Applies through the game's Temp/apply pipeline like vanilla tools.</summary>
        Pipeline = 1,
    }

    public sealed class ComponentDecision
    {
        public string Name;
        public ComponentTreatment Treatment;
        public string Note;
    }

    public sealed class SystemDecision
    {
        public string Name;
        public SystemTreatment Treatment;
        public string Note;
    }

    public sealed class ToolDecision
    {
        public string Name;
        public ToolTreatment Treatment;
        public string Note;
    }

    /// <summary>A setting whose host value applies to everyone (declared <c>host-setting</c>, validated).</summary>
    public sealed class SettingDecision
    {
        public string SettingsType;
        public string Name;

        public override string ToString() => SettingsType + "." + Name;
    }

    /// <summary>
    /// The decisions about one mod (<see cref="ModClassifier.Classify"/>): whether it must match on every peer, how each
    /// of its components travels, which of its systems run on clients, how its tools are captured, which settings are
    /// the host's, and every declaration problem, each with the reason in words for the log and the diagnostics dump.
    /// </summary>
    public sealed class ModReport
    {
        public string AssemblyName;
        /// <summary>The mod can change the shared world: it must be on every peer in the same version.</summary>
        public bool WorldAffecting;
        /// <summary>The scope comes from the mod's own declaration (<c>client-local</c> or <c>world</c>).</summary>
        public bool ScopeDeclared;
        public readonly List<string> ScopeReasons = new List<string>();
        public readonly List<ComponentDecision> Components = new List<ComponentDecision>();
        public readonly List<SystemDecision> Systems = new List<SystemDecision>();
        public readonly List<ToolDecision> Tools = new List<ToolDecision>();
        public readonly List<SettingDecision> HostSettings = new List<SettingDecision>();
        /// <summary>Declaration problems (unknown role, missing target, wrong kind, conflicts); each one was ignored.</summary>
        public readonly List<string> Problems = new List<string>();
        /// <summary>Hints for the mod's author (nothing was changed because of them).</summary>
        public readonly List<string> Hints = new List<string>();

        public IEnumerable<string> ComponentsWith(ComponentTreatment t)
        {
            foreach (ComponentDecision c in Components)
            {
                if (c.Treatment == t) yield return c.Name;
            }
        }

        public IEnumerable<string> SystemsWith(SystemTreatment t)
        {
            foreach (SystemDecision s in Systems)
            {
                if (s.Treatment == t) yield return s.Name;
            }
        }

        public IEnumerable<string> ToolsWith(ToolTreatment t)
        {
            foreach (ToolDecision d in Tools)
            {
                if (d.Treatment == t) yield return d.Name;
            }
        }

        private int Count(ComponentTreatment t)
        {
            int n = 0;
            foreach (ComponentDecision c in Components) n += c.Treatment == t ? 1 : 0;
            return n;
        }

        private int Count(SystemTreatment t)
        {
            int n = 0;
            foreach (SystemDecision s in Systems) n += s.Treatment == t ? 1 : 0;
            return n;
        }

        /// <summary>One line: scope and what is done (the log line and the dump's heading).</summary>
        public string Summary()
        {
            var parts = new List<string>();
            int replicated = Count(ComponentTreatment.Replicated) + Count(ComponentTreatment.Presence);
            if (replicated > 0) parts.Add(Plural(replicated, "replicated component"));
            int local = Count(ComponentTreatment.ToolLocal);
            if (local > 0) parts.Add(Plural(local, "tool-local component"));
            int excluded = Count(ComponentTreatment.Excluded);
            if (excluded > 0) parts.Add(Plural(excluded, "excluded component"));
            int off = Count(SystemTreatment.OffBySimulation) + Count(SystemTreatment.HostOnly) + Count(SystemTreatment.JoinOff);
            if (off > 0) parts.Add(Plural(off, "system") + " off on clients");
            int kept = Count(SystemTreatment.KeptOnClients);
            if (kept > 0) parts.Add(Plural(kept, "simulation system") + " kept on clients");
            int direct = 0;
            foreach (ToolDecision t in Tools) direct += t.Treatment == ToolTreatment.DirectWrite ? 1 : 0;
            if (direct > 0) parts.Add(Plural(direct, "direct-write tool"));
            if (Tools.Count - direct > 0) parts.Add(Plural(Tools.Count - direct, "pipeline tool"));
            if (HostSettings.Count > 0) parts.Add(Plural(HostSettings.Count, "host setting"));
            if (Problems.Count > 0) parts.Add(Plural(Problems.Count, "declaration problem"));
            string scope = WorldAffecting ? "world-affecting" : "client-local";
            return scope + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
        }

        /// <summary>The full explanation, one decision per line (diagnostics dump).</summary>
        public List<string> Describe()
        {
            var lines = new List<string>
            {
                (AssemblyName ?? "?") + ": " + Summary(),
                "  scope: " + (WorldAffecting ? "world-affecting" : "client-local") + (ScopeDeclared ? " (declared)" : "") + " - " + string.Join("; ", ScopeReasons),
            };
            foreach (ComponentDecision c in Components) lines.Add("  component " + c.Name + ": " + Text(c.Treatment) + Note(c.Note));
            foreach (SystemDecision s in Systems) lines.Add("  system " + s.Name + ": " + Text(s.Treatment) + Note(s.Note));
            foreach (ToolDecision t in Tools) lines.Add("  tool " + t.Name + ": " + (t.Treatment == ToolTreatment.Pipeline ? "pipeline (captured like vanilla tools)" : "direct-write (changes proposed while active, edit hold)") + Note(t.Note));
            foreach (SettingDecision s in HostSettings) lines.Add("  setting " + s + ": the host's value for everyone");
            foreach (string p in Problems) lines.Add("  problem: " + p);
            foreach (string h in Hints) lines.Add("  hint: " + h);
            return lines;
        }

        private static string Note(string note) => string.IsNullOrEmpty(note) ? "" : " (" + note + ")";

        private static string Plural(int n, string what) => n.ToString(CultureInfo.InvariantCulture) + " " + what + (n == 1 ? "" : "s");

        public static string Text(ComponentTreatment t)
        {
            switch (t)
            {
                case ComponentTreatment.Replicated: return "replicated";
                case ComponentTreatment.Presence: return "presence replicated";
                case ComponentTreatment.Excluded: return "never replicated (declared)";
                case ComponentTreatment.ToolLocal: return "tool-local: its entities never replicate (declared)";
                default: return "not replicated (the game does not save it)";
            }
        }

        public static string Text(SystemTreatment t)
        {
            switch (t)
            {
                case SystemTreatment.OffBySimulation: return "off on clients (GameSimulation, like every vanilla simulation system)";
                case SystemTreatment.HostOnly: return "host only (off on live clients)";
                case SystemTreatment.KeptOnClients: return "kept on clients (declared)";
                case SystemTreatment.JoinOff: return "off on clients from the start of the join (declared)";
                case SystemTreatment.Presentation: return "local (UI or rendering only)";
                case SystemTreatment.Editor: return "editor only";
                case SystemTreatment.NotScheduled: return "not registered in an update phase";
                default: return "runs on every peer (derivation, like vanilla)";
            }
        }
    }

    /// <summary>
    /// Turns the scan of a mod (<see cref="ModFacts"/>) and its declarations (<see cref="ModDeclarations"/>) into
    /// decisions (docs/design/MOD-COMPATIBILITY.md §6). The rules are conservative:
    /// <list type="bullet">
    /// <item>Scope: a mod is client-local only when it declares so, or when nothing in it can touch shared state (no saved
    /// component, saved system, prefab type, runtime prefab, tool, system in a world phase, Harmony patch outside
    /// presentation code, unreadable patch list). A failed scan is world-affecting.</item>
    /// <item>Components: what the game saves replicates (the generic codec), what it does not save never travels; a
    /// declaration can exclude a saved type or mark tool entities, never force an unsaved type onto the wire.</item>
    /// <item>Systems: GameSimulation systems are off on clients like vanilla ones (the follower clock); systems that run
    /// with the simulation step (Pre/Post/LoadSimulation) are host-only too; everything else runs on every peer like
    /// vanilla derivation, unless declared otherwise.</item>
    /// <item>Tools: direct-write unless declared <c>pipeline</c> (a missed direct write would lose a client's edit; an
    /// extra one costs a redundant proposal).</item>
    /// </list>
    /// Invalid declarations are reported and ignored, never fatal. Pure and deterministic (host and client classify the
    /// same mod version identically).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ModClassifier
    {
        // substrings of component names that usually mean tool, preview or UI data (hints only)
        private static readonly string[] ToolishNames = { "Temp", "Preview", "Highlight", "Hover", "Selected", "Overlay", "ControlPoint", "Handle", "Tool" };

        public static ModReport Classify(ModFacts facts, ModDeclarations declarations = null)
        {
            var report = new ModReport { AssemblyName = facts?.AssemblyName };
            if (facts == null)
            {
                report.WorldAffecting = true;
                report.ScopeReasons.Add("nothing is known about the mod");
                return report;
            }
            declarations = declarations ?? new ModDeclarations();
            report.Problems.AddRange(declarations.Problems);
            if (declarations.HasIntegrationClass)
            {
                if (declarations.ApiVersion > ModDeclarations.SupportedApiVersion)
                {
                    report.Problems.Add("the integration class is written for API version " + declarations.ApiVersion.ToString(CultureInfo.InvariantCulture)
                                        + "; this Skyline Together implements version " + ModDeclarations.SupportedApiVersion.ToString(CultureInfo.InvariantCulture)
                                        + " (newer features are ignored)");
                }
                else if (declarations.ApiVersion < 1)
                {
                    report.Hints.Add("the integration class has no ApiVersion constant; read as version 1");
                }
            }

            var components = new Dictionary<string, ComponentFact>(StringComparer.Ordinal);
            foreach (ComponentFact c in facts.Components)
            {
                if (c?.Name != null) components[c.Name] = c;
            }
            var systems = new Dictionary<string, SystemFact>(StringComparer.Ordinal);
            foreach (SystemFact s in facts.Systems)
            {
                if (s?.Name != null) systems[s.Name] = s;
            }

            // ---- validate the declarations against the facts; conflicting roles on one target cancel each other
            var componentRoles = new Dictionary<string, HashSet<DeclarationRole>>(StringComparer.Ordinal);
            var systemRoles = new Dictionary<string, HashSet<DeclarationRole>>(StringComparer.Ordinal);
            var toolRoles = new Dictionary<string, HashSet<DeclarationRole>>(StringComparer.Ordinal);
            var settings = new List<SettingDecision>();
            bool declaredLocal = false, declaredWorld = false;
            foreach (ModDeclaration d in declarations.Items)
            {
                switch (d.Role)
                {
                    case DeclarationRole.ClientLocal:
                        declaredLocal = true;
                        break;
                    case DeclarationRole.World:
                        declaredWorld = true;
                        break;
                    case DeclarationRole.Replicate:
                    case DeclarationRole.NeverReplicate:
                    case DeclarationRole.ToolLocal:
                    {
                        if (!components.TryGetValue(d.Target, out ComponentFact c))
                        {
                            report.Problems.Add(Where(d) + ": " + d.Target + " is not a component type of this mod" + KindHint(d.Target, components, systems));
                            break;
                        }
                        if (c.Storage == ComponentStorage.Shared || c.Storage == ComponentStorage.Managed)
                        {
                            report.Problems.Add(Where(d) + ": " + d.Target + " is a shared or managed component; those never travel");
                            break;
                        }
                        if (d.Role == DeclarationRole.Replicate && !c.Serializable && !c.EmptySerializable)
                        {
                            report.Problems.Add(Where(d) + ": " + d.Target + " cannot replicate: the game does not save it (implement ISerializable, or IEmptySerializable for a tag)");
                            break;
                        }
                        if (d.Role == DeclarationRole.NeverReplicate && !c.Serializable && !c.EmptySerializable)
                        {
                            report.Hints.Add(d.Target + " is declared never-replicate but is not saved by the game, so it never travels anyway");
                        }
                        Role(componentRoles, d.Target, d.Role);
                        break;
                    }
                    case DeclarationRole.HostOnly:
                    case DeclarationRole.Everywhere:
                    case DeclarationRole.JoinOff:
                    {
                        if (!systems.TryGetValue(d.Target, out SystemFact s))
                        {
                            report.Problems.Add(Where(d) + ": " + d.Target + " is not a system of this mod" + KindHint(d.Target, components, systems));
                            break;
                        }
                        if (s.IsTool)
                        {
                            report.Problems.Add(Where(d) + ": " + d.Target + " is a tool; tools take \"direct-write\" or \"pipeline\"");
                            break;
                        }
                        Role(systemRoles, d.Target, d.Role);
                        break;
                    }
                    case DeclarationRole.DirectWrite:
                    case DeclarationRole.Pipeline:
                    {
                        if (!systems.TryGetValue(d.Target, out SystemFact s) || !s.IsTool)
                        {
                            report.Problems.Add(Where(d) + ": " + d.Target + " is not a tool (ToolBaseSystem) of this mod" + KindHint(d.Target, components, systems));
                            break;
                        }
                        Role(toolRoles, d.Target, d.Role);
                        break;
                    }
                    case DeclarationRole.HostSetting:
                    {
                        SettingFact setting = FindSetting(facts.Settings, d.Target, out string why);
                        if (setting == null)
                        {
                            report.Problems.Add(Where(d) + ": " + why);
                            break;
                        }
                        if (!setting.Supported)
                        {
                            report.Problems.Add(Where(d) + ": " + setting + " is not a bool, number, enum or string property with a public getter and setter");
                            break;
                        }
                        bool known = false;
                        foreach (SettingDecision s in settings) known |= s.SettingsType == setting.SettingsType && s.Name == setting.Name;
                        if (!known) settings.Add(new SettingDecision { SettingsType = setting.SettingsType, Name = setting.Name });
                        break;
                    }
                }
            }
            if (declaredLocal && declaredWorld)
            {
                report.Problems.Add("the mod declares both \"client-local\" and \"world\"; both are ignored");
                declaredLocal = declaredWorld = false;
            }
            DropConflicts(componentRoles, report, (a, b) => true);
            DropConflicts(systemRoles, report, (a, b) => !(a == DeclarationRole.JoinOff && b == DeclarationRole.HostOnly) && !(a == DeclarationRole.HostOnly && b == DeclarationRole.JoinOff));
            DropConflicts(toolRoles, report, (a, b) => true);
            report.HostSettings.AddRange(settings);

            // ---- components
            foreach (ComponentFact c in facts.Components)
            {
                if (c?.Name == null) continue;
                var decision = new ComponentDecision { Name = c.Name };
                componentRoles.TryGetValue(c.Name, out HashSet<DeclarationRole> roles);
                if (roles != null && roles.Contains(DeclarationRole.ToolLocal))
                {
                    decision.Treatment = ComponentTreatment.ToolLocal;
                }
                else if (roles != null && roles.Contains(DeclarationRole.NeverReplicate))
                {
                    decision.Treatment = ComponentTreatment.Excluded;
                }
                else if (c.Storage == ComponentStorage.Shared || c.Storage == ComponentStorage.Managed)
                {
                    decision.Treatment = ComponentTreatment.NotReplicated;
                    decision.Note = c.Storage == ComponentStorage.Shared ? "shared component" : "managed component";
                }
                else if (c.Serializable && c.Storage != ComponentStorage.Tag)
                {
                    decision.Treatment = ComponentTreatment.Replicated;
                    var notes = new List<string>();
                    if (c.HasEntityReferences) notes.Add("entity references sent as net ids, referenced mod entities bound with it");
                    if (c.Enableable && !c.SerializeAsEnabled) notes.Add("enabled bit replicated");
                    notes.Add("untagged changes watched");
                    decision.Note = string.Join("; ", notes);
                    if (Toolish(c.Name)) report.Hints.Add(c.Name + " is saved by the game but named like tool or preview data; if only tool entities carry it, declare it \"tool-local\"");
                }
                else if (c.Serializable || c.EmptySerializable)
                {
                    decision.Treatment = ComponentTreatment.Presence;
                    if (c.Enableable && !c.SerializeAsEnabled) decision.Note = "enabled bit replicated";
                }
                else
                {
                    decision.Treatment = ComponentTreatment.NotReplicated;
                }
                report.Components.Add(decision);
            }

            // ---- systems and tools
            foreach (SystemFact s in facts.Systems)
            {
                if (s?.Name == null) continue;
                if (s.IsTool)
                {
                    toolRoles.TryGetValue(s.Name, out HashSet<DeclarationRole> roles);
                    var tool = new ToolDecision { Name = s.Name, Treatment = roles != null && roles.Contains(DeclarationRole.Pipeline) ? ToolTreatment.Pipeline : ToolTreatment.DirectWrite };
                    if (roles != null) tool.Note = "declared";
                    else if (s.UsesCreationDefinitions) tool.Note = "it creates creation definitions, probably the Temp pipeline; declare it \"pipeline\" to skip direct-write capture";
                    report.Tools.Add(tool);
                    continue;
                }
                report.Systems.Add(DecideSystem(s, facts.PhasesKnown, systemRoles, report));
            }

            // ---- scope
            if (declaredLocal)
            {
                report.WorldAffecting = false;
                report.ScopeDeclared = true;
                report.ScopeReasons.Add("the mod declares itself client-local");
                var evidence = WorldEvidence(facts, report);
                if (evidence.Count > 0) report.Hints.Add("declared client-local although it " + string.Join("; ", evidence) + " (the declaration wins)");
            }
            else if (declaredWorld)
            {
                report.WorldAffecting = true;
                report.ScopeDeclared = true;
                report.ScopeReasons.Add("the mod declares itself world-affecting");
            }
            else if (facts.ScanFailed)
            {
                report.WorldAffecting = true;
                report.ScopeReasons.Add("the scan failed" + (string.IsNullOrEmpty(facts.ScanError) ? "" : " (" + facts.ScanError + ")") + "; treated as world-affecting");
            }
            else
            {
                List<string> evidence = WorldEvidence(facts, report);
                report.WorldAffecting = evidence.Count > 0;
                if (evidence.Count > 0) report.ScopeReasons.AddRange(evidence);
                else report.ScopeReasons.Add("nothing in it can change the shared world (UI, rendering or tooltips only)");
            }
            return report;
        }

        private static SystemDecision DecideSystem(SystemFact s, bool phasesKnown, Dictionary<string, HashSet<DeclarationRole>> systemRoles, ModReport report)
        {
            var decision = new SystemDecision { Name = s.Name };
            string phases = s.Phases.Count > 0 ? string.Join(", ", s.Phases) : "";
            systemRoles.TryGetValue(s.Name, out HashSet<DeclarationRole> roles);
            if (roles != null)
            {
                if (roles.Contains(DeclarationRole.JoinOff)) decision.Treatment = SystemTreatment.JoinOff;
                else if (roles.Contains(DeclarationRole.HostOnly)) decision.Treatment = SystemTreatment.HostOnly;
                else decision.Treatment = SystemTreatment.KeptOnClients;
                decision.Note = phases;
                return decision;
            }
            bool simulation = false, step = false, world = false, presentation = false, editor = false;
            foreach (string p in s.Phases)
            {
                PhaseKind kind = ModPhases.KindOf(p);
                simulation |= kind == PhaseKind.Simulation;
                step |= kind == PhaseKind.SimulationStep;
                presentation |= kind == PhaseKind.Presentation;
                editor |= kind == PhaseKind.Editor;
                world |= ModPhases.MayChangeWorld(kind) && kind != PhaseKind.Simulation && kind != PhaseKind.SimulationStep;
            }
            if (simulation)
            {
                decision.Treatment = SystemTreatment.OffBySimulation;
            }
            else if (step)
            {
                decision.Treatment = SystemTreatment.HostOnly;
                decision.Note = phases + ": runs with the simulation step, so it is the host's like the simulation";
                return decision;
            }
            else if (!phasesKnown)
            {
                decision.Treatment = SystemTreatment.Everywhere;
                decision.Note = "update phase unknown";
                return decision;
            }
            else if (s.Phases.Count == 0)
            {
                decision.Treatment = SystemTreatment.NotScheduled;
            }
            else if (!world && presentation)
            {
                decision.Treatment = SystemTreatment.Presentation;
            }
            else if (!world && editor)
            {
                decision.Treatment = SystemTreatment.Editor;
            }
            else
            {
                decision.Treatment = SystemTreatment.Everywhere;
            }
            if (s.SavesState && decision.Treatment == SystemTreatment.Everywhere)
            {
                report.Hints.Add(s.Name + " saves its own state with the city: joiners get the host's state, later changes are not replicated; if it decides shared state, declare it \"host-only\"");
            }
            if (s.PostDeserialize && decision.Treatment != SystemTreatment.OffBySimulation)
            {
                report.Hints.Add(s.Name + " runs after a city loads; if it changes the city from the player's own settings, declare it \"join-off\"");
            }
            decision.Note = phases;
            return decision;
        }

        /// <summary>Why the mod may change the shared world (empty: it cannot, as far as the scan sees).</summary>
        private static List<string> WorldEvidence(ModFacts facts, ModReport report)
        {
            var evidence = new List<string>();
            int saved = 0;
            foreach (ComponentFact c in facts.Components)
            {
                if (c != null && (c.Serializable || c.EmptySerializable) && c.Storage != ComponentStorage.Shared && c.Storage != ComponentStorage.Managed) saved++;
            }
            if (saved > 0) evidence.Add("saves " + saved.ToString(CultureInfo.InvariantCulture) + " component type(s) with entities");
            int savedSystems = 0, tools = 0, worldPhase = 0, unknownPhase = 0;
            string example = null;
            foreach (SystemFact s in facts.Systems)
            {
                if (s == null) continue;
                if (s.IsTool)
                {
                    tools++;
                    continue;
                }
                if (s.SavesState) savedSystems++;
                if (!facts.PhasesKnown)
                {
                    unknownPhase++;
                    continue;
                }
                foreach (string p in s.Phases)
                {
                    if (!ModPhases.MayChangeWorld(ModPhases.KindOf(p))) continue;
                    worldPhase++;
                    example = example ?? s.Name + " in " + p;
                    break;
                }
            }
            if (savedSystems > 0) evidence.Add("has " + savedSystems.ToString(CultureInfo.InvariantCulture) + " system(s) that save state");
            if (tools > 0) evidence.Add("has " + tools.ToString(CultureInfo.InvariantCulture) + " tool(s)");
            if (worldPhase > 0) evidence.Add("runs " + worldPhase.ToString(CultureInfo.InvariantCulture) + " system(s) in world phases (e.g. " + example + ")");
            if (unknownPhase > 0) evidence.Add("has " + unknownPhase.ToString(CultureInfo.InvariantCulture) + " system(s) whose update phase is unknown");
            if (facts.PrefabTypes.Count > 0) evidence.Add("declares " + facts.PrefabTypes.Count.ToString(CultureInfo.InvariantCulture) + " prefab type(s)");
            if (facts.RuntimePrefabCreators.Count > 0) evidence.Add("creates prefabs at runtime (" + facts.RuntimePrefabCreators[0] + ")");
            if (facts.PatchedMethods != null)
            {
                int world = 0;
                string first = null;
                foreach (string m in facts.PatchedMethods)
                {
                    if (ModPhases.IsPresentationTarget(m)) continue;
                    world++;
                    first = first ?? m;
                }
                if (world > 0) evidence.Add("patches " + world.ToString(CultureInfo.InvariantCulture) + " method(s) outside UI code with Harmony (e.g. " + first + ")");
            }
            else if (facts.ReferencesHarmony)
            {
                evidence.Add("uses Harmony and its patches could not be read");
            }
            if (report.HostSettings.Count > 0) evidence.Add("declares settings that must match the host");
            return evidence;
        }

        private static SettingFact FindSetting(List<SettingFact> settings, string target, out string why)
        {
            why = null;
            SettingFact shortMatch = null;
            int shortMatches = 0;
            foreach (SettingFact s in settings)
            {
                if (s == null) continue;
                if (string.Equals(s.SettingsType + "." + s.Name, target, StringComparison.Ordinal)) return s;
                if (string.Equals(s.Name, target, StringComparison.Ordinal))
                {
                    shortMatch = s;
                    shortMatches++;
                }
            }
            if (shortMatches == 1) return shortMatch;
            why = shortMatches > 1
                ? target + " exists in several settings classes; write SettingsType.Property"
                : target + " is not a property of a ModSetting class of this mod";
            return null;
        }

        private static void Role(Dictionary<string, HashSet<DeclarationRole>> map, string target, DeclarationRole role)
        {
            if (!map.TryGetValue(target, out HashSet<DeclarationRole> roles)) map[target] = roles = new HashSet<DeclarationRole>();
            roles.Add(role);
        }

        private static void DropConflicts(Dictionary<string, HashSet<DeclarationRole>> map, ModReport report, Func<DeclarationRole, DeclarationRole, bool> conflicts)
        {
            var dropped = new List<string>();
            foreach (KeyValuePair<string, HashSet<DeclarationRole>> pair in map)
            {
                var roles = new List<DeclarationRole>(pair.Value);
                bool conflict = false;
                for (int i = 0; i < roles.Count && !conflict; i++)
                {
                    for (int k = i + 1; k < roles.Count && !conflict; k++) conflict = conflicts(roles[i], roles[k]);
                }
                if (!conflict) continue;
                roles.Sort();
                var names = new List<string>();
                foreach (DeclarationRole r in roles) names.Add(ModDeclarations.RoleName(r));
                report.Problems.Add(pair.Key + " is declared " + string.Join(" and ", names) + "; these contradict each other, so neither applies");
                dropped.Add(pair.Key);
            }
            foreach (string key in dropped) map.Remove(key);
        }

        private static string Where(ModDeclaration d) => string.IsNullOrEmpty(d.Source) ? ModDeclarations.RoleName(d.Role) : d.Source;

        private static string KindHint(string target, Dictionary<string, ComponentFact> components, Dictionary<string, SystemFact> systems)
        {
            if (components.ContainsKey(target)) return " (it is a component)";
            if (systems.TryGetValue(target, out SystemFact s)) return s.IsTool ? " (it is a tool)" : " (it is a system)";
            return " (no such type; check the full name, nested types use '+')";
        }

        private static bool Toolish(string name)
        {
            int dot = name.LastIndexOfAny(new[] { '.', '+' });
            string simple = dot >= 0 ? name.Substring(dot + 1) : name;
            foreach (string s in ToolishNames)
            {
                if (simple.IndexOf(s, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>The report's explanation as one text block (tests, dumps).</summary>
        public static string Explain(ModReport report)
        {
            var sb = new StringBuilder();
            foreach (string line in report.Describe()) sb.Append(line).Append('\n');
            return sb.ToString();
        }
    }
}
