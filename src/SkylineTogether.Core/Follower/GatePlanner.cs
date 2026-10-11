using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// Turns the gate table, the adapters' gate entries, the systems the game layer found and the keep list into the
    /// client gate plan and its self-check (ARCH §6.3, Appendix E, R11 §4, §8). game-follower only resolves names, calls
    /// <see cref="Plan"/> and flips <c>Enabled</c>.
    /// <para>
    /// <b>Claims.</b> A system can be named by several sources: gates.json <c>entries</c> (Off, OffAndFeed, Keep),
    /// <c>sessionLoad</c>, <c>toolScoped</c> (systems of each entry; the tools themselves are only resolved), adapter
    /// entries (<see cref="GateAction.Off"/>, <see cref="GateAction.OffAndFeed"/>, <see cref="GateAction.Keep"/>,
    /// <see cref="GateAction.OffDuringSessionLoad"/>, and <see cref="GateAction.ToolScoped"/> for systems of an
    /// adapter's tool-scoped entry), the keep list (the cosmetic systems the follower clock runs directly, which must stay
    /// enabled) and the built-in keep <c>GameModeGovernmentSubsidiesSystem</c> (a pure function of replicated money; the
    /// budget UI reads its <c>Enabled</c> flag, ARCH §6.1).
    /// </para>
    /// <para>
    /// <b>Precedence</b> (each overridden claim is reported in <see cref="GateReport.Conflicts"/>):
    /// (1) <c>SimulationSystem</c> is never gated by the table: the load hand-over owns it (ARCH §6.1, §6.3);
    /// (2) <c>IconCommandSystem</c> must stay on: its queue list has no <c>Enabled</c> guard (R11 §4 Q8), so it is always
    /// kept, and any off claim is ignored and reported as a producer/consumer violation;
    /// (3) tool-scoped claims win over everything else, because the tool-scoped gates must own their systems (a
    /// re-assertion of the permanent list would otherwise close an open window);
    /// (4) Off claims win over session-load and keep claims (fail closed, ARCH K3);
    /// (5) session-load claims win over keep claims (a kept Deserialize-phase rewriter would change the joiner's world);
    /// (6) every other system registered in GameSimulation, EditorSimulation or LoadSimulation is disabled unless it has a
    /// claim (belt and braces: clients never run those phases, and producers guarded by <c>Enabled</c> stop, ARCH §6.1);
    /// (7) keep claims.
    /// </para>
    /// <para>
    /// <b>Checks.</b> Names that do not resolve are listed (critical ones block the join); the producer/consumer rules of
    /// ARCH §6.3 are checked against the plan (a producer must be off whenever its consumer is off); Game-assembly systems
    /// of the world-writing phases (MainLoop, LateUpdate, the Modification phases, the tool phases, Pre/PostSimulation, or
    /// an unknown phase) that no source names and no phase rule covers are listed as unclassified.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class GatePlanner
    {
        /// <summary>Disabled by the load hand-over, never by the table (ARCH §6.1).</summary>
        public const string SimulationSystemTypeName = "Game.Simulation.SimulationSystem";

        /// <summary>Must stay enabled on clients (R11 §4 Q8).</summary>
        public const string IconCommandSystemTypeName = "Game.Notifications.IconCommandSystem";

        /// <summary>Kept although registered in GameSimulation (ARCH §6.1).</summary>
        public const string GovernmentSubsidiesSystemTypeName = "Game.Simulation.GameModeGovernmentSubsidiesSystem";

        /// <summary>Namespace prefix of Game-assembly systems (the scope of the unclassified report).</summary>
        public const string GameAssemblyPrefix = "Game.";

        internal static readonly string[] SimulationPhases = { "GameSimulation", "EditorSimulation", "LoadSimulation" };

        internal static readonly string[] WorldWritingPhases =
        {
            "MainLoop", "LateUpdate", "Modification1", "Modification2", "Modification2B", "Modification3", "Modification4",
            "Modification4B", "Modification5", "ModificationEnd", "PreSimulation", "PostSimulation", "PreTool", "ToolUpdate",
            "ClearTool", "ApplyTool", "PostTool"
        };

        internal static readonly string[] BuiltInKeep = { GovernmentSubsidiesSystemTypeName };

        /// <summary>The producer/consumer pairs of ARCH §6.3 (R11 §4): the consumer drains a queue that the producers write
        /// without checking the consumer's <c>Enabled</c> flag, so the consumer may be off only together with them.</summary>
        internal static readonly ProducerConsumerRule[] Rules =
        {
            new ProducerConsumerRule("Game.Triggers.TriggerSystem", "R11 §4 Q1: TempJob queue list per CreateActionBuffer, assert only",
                                     "Game.Citizens.CitizenInitializeSystem", "Game.Events.AddHealthProblemSystem",
                                     "Game.Events.InitializeSystem", "Game.Triggers.NotificationTriggerSystem"),
            new ProducerConsumerRule("Game.Simulation.CityStatisticsSystem", "R11 §4 Q3: persistent statistics queue, assert only",
                                     "Game.Events.AddHealthProblemSystem"),
            new ProducerConsumerRule("Game.Simulation.XPSystem", "R11 §4 Q4: persistent XP queue, no guard",
                                     "Game.Simulation.XPBuiltSystem", "Game.Simulation.NetXPSystem", "Game.Simulation.XPAccumulationSystem"),
            new ProducerConsumerRule("Game.Simulation.PropertyProcessingSystem", "R11 §4 Q6: persistent rent queue, assert only",
                                     "Game.Citizens.CompanyInitializeSystem"),
        };

        private enum Source : byte { TableEntries, TableSessionLoad, Adapter }

        private enum GateClass : byte { None, Keep, SessionLoad, ToolScoped, Off }

        /// <summary>Computes the gate plan and its report. Deterministic: the same input gives identical lists.</summary>
        /// <param name="table">The parsed gate table (required).</param>
        /// <param name="adapterEntries">Gate entries contributed by adapters (null = none). Adapters' tool-scoped entries
        /// (<c>IGateRegistry.AddToolScoped</c>) belong in <paramref name="table"/>'s <see cref="GateTable.ToolScopedEntries"/>
        /// (the caller builds a merged <see cref="GateTable"/>), so their tools resolve and the producer/consumer check knows
        /// which tool opens which system; a <see cref="GateAction.ToolScoped"/> gate entry carries no tool and is only
        /// classified.</param>
        /// <param name="allSystems">Every registration the game layer found, plus unresolved rows for missing names (null =
        /// none, then every name is unresolved).</param>
        /// <param name="keepList">Systems that must stay enabled, at least the cosmetic systems (null = none).</param>
        public static GatePlan Plan(GateTable table, IEnumerable<GateEntry> adapterEntries, IReadOnlyList<ResolvedSystem> allSystems, IReadOnlyList<string> keepList)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));

            var conflicts = new List<string>();
            var violations = new List<string>();
            Dictionary<string, SystemInfo> systems = IndexSystems(allSystems, conflicts);
            var claims = new Dictionary<string, Claim>(StringComparer.Ordinal);

            AddEntries(claims, table.Entries, Source.TableEntries, conflicts);
            AddEntries(claims, table.SessionLoadEntries, Source.TableSessionLoad, conflicts);
            AddToolScoped(claims, table.ToolScopedEntries, conflicts);
            if (adapterEntries != null) AddEntries(claims, adapterEntries, Source.Adapter, conflicts);
            if (keepList != null)
            {
                int unnamed = 0;
                foreach (string name in keepList)
                {
                    if (string.IsNullOrEmpty(name)) unnamed++;
                    else GetClaim(claims, name).KeepList = true;
                }
                if (unnamed > 0) conflicts.Add(Num(unnamed) + " empty name(s) in the keep list ignored");
            }
            foreach (string name in BuiltInKeep) GetClaim(claims, name).BuiltInKeep = true;
            // the protected systems are always classified (never "unclassified"), whether or not a source names them
            GetClaim(claims, SimulationSystemTypeName);
            GetClaim(claims, IconCommandSystemTypeName);

            var names = new List<string>(claims.Keys);
            names.Sort(StringComparer.Ordinal);
            var unresolved = new List<string>();
            var criticalUnresolved = new List<string>();
            var finalClass = new Dictionary<string, GateClass>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                Claim claim = claims[name];
                systems.TryGetValue(name, out SystemInfo info);
                Classify(claim, info, conflicts, violations);
                bool resolved = info != null && info.Resolved;
                if (!resolved)
                {
                    if (claim.Named)
                    {
                        unresolved.Add(name);
                        if (claim.Critical) criticalUnresolved.Add(name);
                    }
                    continue;
                }
                finalClass[name] = claim.Class;
            }

            // belt and braces: every unclaimed system of a simulation phase is disabled (ARCH §6.1, Appendix E row 1)
            var disabledByPhase = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, SystemInfo> pair in systems)
            {
                if (!pair.Value.Resolved || !pair.Value.SimulationPhase) continue;
                if (claims.TryGetValue(pair.Key, out Claim claim) && (claim.Class != GateClass.None || claim.Protected)) continue;
                finalClass[pair.Key] = GateClass.Off;
                disabledByPhase.Add(pair.Key);
                if (pair.Value.OtherPhases != null)
                {
                    // Enabled is per system, not per registration (e.g. DebugWatchSystem also runs in MainLoop, SystemOrder.cs)
                    pair.Value.OtherPhases.Sort(StringComparer.Ordinal);
                    conflicts.Add(pair.Key + ": disabled by the simulation-phase rule, which also stops its registration in "
                                  + string.Join(", ", pair.Value.OtherPhases.ToArray()) + "; list it as Keep if it must run on clients (ARCH §6.1)");
                }
            }

            var disable = new List<string>();
            var sessionLoad = new List<string>();
            var toolScoped = new List<string>();
            var keep = new List<string>();
            foreach (KeyValuePair<string, GateClass> pair in finalClass)
            {
                switch (pair.Value)
                {
                    case GateClass.Off: disable.Add(pair.Key); break;
                    case GateClass.SessionLoad: sessionLoad.Add(pair.Key); break;
                    case GateClass.ToolScoped: toolScoped.Add(pair.Key); break;
                    case GateClass.Keep: keep.Add(pair.Key); break;
                }
            }

            var unclassified = new List<string>();
            foreach (KeyValuePair<string, SystemInfo> pair in systems)
            {
                SystemInfo info = pair.Value;
                if (!info.Resolved || disabledByPhase.Contains(pair.Key)) continue;
                if (!pair.Key.StartsWith(GameAssemblyPrefix, StringComparison.Ordinal)) continue;
                if (!info.WorldWritingPhase && !info.UnknownPhase) continue;
                if (claims.TryGetValue(pair.Key, out Claim claim) && (claim.Class != GateClass.None || claim.Protected || claim.IsTool)) continue;
                unclassified.Add(pair.Key);
            }

            CheckRules(systems, claims, finalClass, violations);
            CheckMustStayOn(systems, violations);

            disable.Sort(StringComparer.Ordinal);
            sessionLoad.Sort(StringComparer.Ordinal);
            toolScoped.Sort(StringComparer.Ordinal);
            keep.Sort(StringComparer.Ordinal);
            unclassified.Sort(StringComparer.Ordinal);
            conflicts.Sort(StringComparer.Ordinal);
            violations.Sort(StringComparer.Ordinal);

            var report = new GateReport
            {
                Unresolved = unresolved.AsReadOnly(),
                CriticalUnresolved = criticalUnresolved.AsReadOnly(),
                ProducerConsumerViolations = violations.AsReadOnly(),
                Unclassified = unclassified.AsReadOnly(),
                Conflicts = conflicts.AsReadOnly()
            };
            return new GatePlan
            {
                Disable = disable.AsReadOnly(),
                SessionLoadDisable = sessionLoad.AsReadOnly(),
                Keep = keep.AsReadOnly(),
                ToolScoped = toolScoped.AsReadOnly(),
                Report = report
            };
        }

        private static Dictionary<string, SystemInfo> IndexSystems(IReadOnlyList<ResolvedSystem> allSystems, List<string> conflicts)
        {
            var systems = new Dictionary<string, SystemInfo>(StringComparer.Ordinal);
            if (allSystems == null) return systems;
            int unnamed = 0;
            for (int i = 0; i < allSystems.Count; i++)
            {
                ResolvedSystem row = allSystems[i];
                if (row == null || string.IsNullOrEmpty(row.TypeName))
                {
                    unnamed++;
                    continue;
                }
                if (!systems.TryGetValue(row.TypeName, out SystemInfo info))
                {
                    info = new SystemInfo();
                    systems.Add(row.TypeName, info);
                }
                if (!row.Resolved) continue;
                // rows of one system share one instance; if they disagree on Enabled, assume the worst for the checks
                info.Enabled = info.Resolved ? info.Enabled && row.CurrentlyEnabled : row.CurrentlyEnabled;
                info.Resolved = true;
                if (string.IsNullOrEmpty(row.Phase))
                {
                    info.UnknownPhase = true;
                    continue;
                }
                if (ContainsIgnoreCase(SimulationPhases, row.Phase))
                {
                    info.SimulationPhase = true;
                    continue;
                }
                if (ContainsIgnoreCase(WorldWritingPhases, row.Phase)) info.WorldWritingPhase = true;
                if (info.OtherPhases == null) info.OtherPhases = new List<string>(2);
                if (!info.OtherPhases.Contains(row.Phase)) info.OtherPhases.Add(row.Phase);
            }
            if (unnamed > 0) conflicts.Add(Num(unnamed) + " row(s) of the system list without a type name ignored");
            return systems;
        }

        private static void AddEntries(Dictionary<string, Claim> claims, IEnumerable<GateEntry> entries, Source source, List<string> conflicts)
        {
            if (entries == null) return;
            int unnamed = 0;
            foreach (GateEntry entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.TypeName))
                {
                    unnamed++;
                    continue;
                }
                Claim claim = GetClaim(claims, entry.TypeName);
                claim.Critical |= entry.Critical;
                if (source == Source.TableSessionLoad)
                {
                    // the section defines the meaning; the parser guarantees OffDuringSessionLoad
                    claim.TableSessionLoad = true;
                    continue;
                }
                bool table = source == Source.TableEntries;
                switch (entry.Action)
                {
                    case GateAction.Off:
                    case GateAction.OffAndFeed:
                        if (table) claim.TableOff = true;
                        else claim.AdapterOff = true;
                        break;
                    case GateAction.Keep:
                        if (table) claim.TableKeep = true;
                        else claim.AdapterKeep = true;
                        break;
                    case GateAction.OffDuringSessionLoad:
                        if (table) claim.TableSessionLoad = true;
                        else claim.AdapterSessionLoad = true;
                        break;
                    case GateAction.ToolScoped:
                        // a system of a tool-scoped entry declared elsewhere (the entry carries the tool, this one not)
                        if (table) claim.TableToolScoped = true;
                        else claim.AdapterToolScoped = true;
                        claim.UnknownTool = true;
                        break;
                    default:
                        conflicts.Add(entry.TypeName + ": unknown gate action " + Num((int)entry.Action) + " treated as Off (fail closed)");
                        if (table) claim.TableOff = true;
                        else claim.AdapterOff = true;
                        break;
                }
            }
            if (unnamed > 0)
            {
                conflicts.Add(Num(unnamed) + " " + SourceName(source) + " entr" + (unnamed == 1 ? "y" : "ies") + " without a type name ignored");
            }
        }

        private static void AddToolScoped(Dictionary<string, Claim> claims, IReadOnlyList<ToolScopedGateEntry> entries, List<string> conflicts)
        {
            if (entries == null) return;
            foreach (ToolScopedGateEntry entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.ToolTypeName))
                {
                    conflicts.Add("a gates.json toolScoped entry without a tool ignored");
                    continue;
                }
                GetClaim(claims, entry.ToolTypeName).IsTool = true;
                if (entry.Systems == null) continue;
                foreach (string system in entry.Systems)
                {
                    if (string.IsNullOrEmpty(system)) continue;
                    Claim claim = GetClaim(claims, system);
                    claim.TableToolScoped = true;
                    if (claim.Tools == null) claim.Tools = new List<string>();
                    if (!claim.Tools.Contains(entry.ToolTypeName)) claim.Tools.Add(entry.ToolTypeName);
                }
            }
        }

        private static void Classify(Claim claim, SystemInfo info, List<string> conflicts, List<string> violations)
        {
            string name = claim.Name;
            if (name == SimulationSystemTypeName)
            {
                claim.Protected = true;
                claim.Class = GateClass.None;
                if (claim.AnyGateClaim)
                {
                    conflicts.Add(name + ": " + Describe(claim, true, true, true, true) + " ignored; the load hand-over disables it, never the gate table (ARCH §6.1)");
                }
                return;
            }
            if (name == IconCommandSystemTypeName)
            {
                // mandatory keep (Appendix E): every vanilla icon producer writes its queue list without a guard
                claim.Protected = true;
                claim.Class = GateClass.Keep;
                if (claim.AnyOff || claim.AnySessionLoad || claim.AnyToolScoped)
                {
                    violations.Add(name + " must stay on: its queue list has no Enabled guard (R11 §4 Q8); "
                                   + Describe(claim, true, true, true, false) + " ignored");
                }
                return;
            }
            if (claim.AnyToolScoped)
            {
                claim.Class = GateClass.ToolScoped;
                string overridden = Describe(claim, true, true, false, true);
                if (overridden.Length > 0) conflicts.Add(name + ": tool-scoped overrides " + overridden);
                if (info != null && info.Resolved && info.SimulationPhase)
                {
                    conflicts.Add(name + ": tool-scoped but registered in a simulation phase, which never runs on clients; its preview window has no effect");
                }
                return;
            }
            if (claim.AnyOff)
            {
                claim.Class = GateClass.Off;
                string overridden = Describe(claim, false, true, false, true);
                if (overridden.Length > 0) conflicts.Add(name + ": " + Describe(claim, true, false, false, false) + " overrides " + overridden + " (fail closed)");
                return;
            }
            if (claim.AnySessionLoad)
            {
                claim.Class = GateClass.SessionLoad;
                string overridden = Describe(claim, false, false, false, true);
                if (overridden.Length > 0) conflicts.Add(name + ": " + Describe(claim, false, true, false, false) + " overrides " + overridden);
                return;
            }
            claim.Class = claim.AnyKeep ? GateClass.Keep : GateClass.None;
        }

        /// <summary>The sources of the selected claim kinds, e.g. "Off (gates.json entries) and Keep (keep list)".</summary>
        private static string Describe(Claim claim, bool off, bool sessionLoad, bool toolScoped, bool keep)
        {
            var parts = new List<string>(4);
            if (off && claim.AnyOff)
            {
                parts.Add("Off (" + Join(claim.TableOff ? "gates.json entries" : null, claim.AdapterOff ? "adapter" : null) + ")");
            }
            if (sessionLoad && claim.AnySessionLoad)
            {
                parts.Add("OffDuringSessionLoad (" + Join(claim.TableSessionLoad ? "gates.json sessionLoad" : null, claim.AdapterSessionLoad ? "adapter" : null) + ")");
            }
            if (toolScoped && claim.AnyToolScoped)
            {
                parts.Add("ToolScoped (" + Join(claim.TableToolScoped ? "gates.json toolScoped" : null, claim.AdapterToolScoped ? "adapter" : null) + ")");
            }
            if (keep && claim.AnyKeep)
            {
                parts.Add("Keep (" + Join(Join(claim.TableKeep ? "gates.json entries" : null, claim.AdapterKeep ? "adapter" : null),
                                          Join(claim.KeepList ? "keep list" : null, claim.BuiltInKeep ? "built-in" : null)) + ")");
            }
            return parts.Count == 0 ? string.Empty : string.Join(" and ", parts.ToArray());
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? string.Empty;
            if (string.IsNullOrEmpty(b)) return a;
            return a + ", " + b;
        }

        private static void CheckRules(Dictionary<string, SystemInfo> systems, Dictionary<string, Claim> claims,
                                       Dictionary<string, GateClass> finalClass, List<string> violations)
        {
            foreach (ProducerConsumerRule rule in Rules)
            {
                GateClass consumer = ClassOf(rule.Consumer, systems, finalClass, out bool consumerPresent);
                if (!consumerPresent || !IsOff(consumer)) continue;
                foreach (string producerName in rule.Producers)
                {
                    GateClass producer = ClassOf(producerName, systems, finalClass, out bool producerPresent);
                    if (!producerPresent) continue;   // an absent producer cannot write the queue
                    if (Covers(producer, consumer, producerName, rule.Consumer, claims)) continue;
                    violations.Add(rule.Consumer + " is off " + WhenOff(consumer) + ", but its producer " + producerName + " is "
                                   + (IsOff(producer) ? "on at times when it is off" : "on") + ": the producer writes the consumer's queue without checking Enabled ("
                                   + rule.Evidence + "); gate " + producerName + " off as well");
                }
            }
        }

        private static void CheckMustStayOn(Dictionary<string, SystemInfo> systems, List<string> violations)
        {
            if (systems.TryGetValue(IconCommandSystemTypeName, out SystemInfo info) && info.Resolved && !info.Enabled)
            {
                violations.Add(IconCommandSystemTypeName + " must stay on (its queue list has no Enabled guard, R11 §4 Q8), but it is currently disabled");
            }
        }

        private static GateClass ClassOf(string name, Dictionary<string, SystemInfo> systems, Dictionary<string, GateClass> finalClass, out bool present)
        {
            present = systems.TryGetValue(name, out SystemInfo info) && info.Resolved;
            return present && finalClass.TryGetValue(name, out GateClass gateClass) ? gateClass : GateClass.None;
        }

        private static bool IsOff(GateClass gateClass) => gateClass == GateClass.Off || gateClass == GateClass.SessionLoad || gateClass == GateClass.ToolScoped;

        /// <summary>True when the producer is off at least whenever the consumer is off.</summary>
        private static bool Covers(GateClass producer, GateClass consumer, string producerName, string consumerName, Dictionary<string, Claim> claims)
        {
            switch (consumer)
            {
                case GateClass.Off:
                    return producer == GateClass.Off;
                case GateClass.SessionLoad:
                    // windows never open during a load (the client is not live), so tool-scoped producers are off then too
                    return producer == GateClass.Off || producer == GateClass.SessionLoad || producer == GateClass.ToolScoped;
                case GateClass.ToolScoped:
                    if (producer == GateClass.Off) return true;
                    if (producer != GateClass.ToolScoped) return false;
                    claims.TryGetValue(producerName, out Claim p);
                    claims.TryGetValue(consumerName, out Claim c);
                    if (p == null || c == null || p.UnknownTool || c.UnknownTool || p.Tools == null || c.Tools == null) return true;
                    // the producer may only run while one of the consumer's windows is open
                    foreach (string tool in p.Tools)
                    {
                        if (!c.Tools.Contains(tool)) return false;
                    }
                    return true;
                default:
                    return true;
            }
        }

        private static string WhenOff(GateClass gateClass)
        {
            switch (gateClass)
            {
                case GateClass.SessionLoad: return "during session loads";
                case GateClass.ToolScoped: return "outside its tool's preview windows";
                default: return "on clients";
            }
        }

        private static Claim GetClaim(Dictionary<string, Claim> claims, string name)
        {
            if (!claims.TryGetValue(name, out Claim claim))
            {
                claim = new Claim(name);
                claims.Add(name, claim);
            }
            return claim;
        }

        private static bool ContainsIgnoreCase(string[] values, string value)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string SourceName(Source source)
        {
            switch (source)
            {
                case Source.TableEntries: return "gates.json entries";
                case Source.TableSessionLoad: return "gates.json sessionLoad";
                default: return "adapter";
            }
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        private sealed class SystemInfo
        {
            public bool Resolved;
            public bool Enabled = true;
            public bool SimulationPhase;
            public bool WorldWritingPhase;
            public bool UnknownPhase;
            /// <summary>Distinct named phases other than the simulation phases (null when none).</summary>
            public List<string> OtherPhases;
        }

        private sealed class Claim
        {
            public readonly string Name;
            public bool TableOff, TableKeep, TableSessionLoad, TableToolScoped;
            public bool AdapterOff, AdapterKeep, AdapterSessionLoad, AdapterToolScoped;
            public bool KeepList, BuiltInKeep, IsTool, Critical, UnknownTool, Protected;
            public List<string> Tools;
            public GateClass Class;

            public Claim(string name)
            {
                Name = name;
            }

            public bool AnyOff => TableOff || AdapterOff;
            public bool AnySessionLoad => TableSessionLoad || AdapterSessionLoad;
            public bool AnyToolScoped => TableToolScoped || AdapterToolScoped;
            public bool AnyKeep => TableKeep || AdapterKeep || KeepList || BuiltInKeep;

            /// <summary>Named by a gate source or the keep list (the built-in keep alone does not count).</summary>
            public bool AnyGateClaim => AnyOff || AnySessionLoad || AnyToolScoped || TableKeep || AdapterKeep || KeepList;

            /// <summary>Should resolve: named by a source, or a tool of a tool-scoped entry.</summary>
            public bool Named => AnyGateClaim || IsTool;
        }
    }
}
