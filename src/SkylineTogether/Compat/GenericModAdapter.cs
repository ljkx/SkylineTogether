using System.Collections.Generic;
using System.Reflection;
using SkylineTogether.Core.Modding;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// The rules for any loaded code mod, built from its analysis and its own declarations (<see cref="ModReport"/>;
    /// docs/design/MOD-COMPATIBILITY.md §6, docs/MOD-INTEGRATION.md): systems that run with the simulation step and
    /// declared host-only systems go off on clients, declared join-off systems from the start of a join, declared
    /// simulation systems stay on, declared tool-local and never-replicate components, pipeline tools and host settings.
    /// It runs next to a built-in adapter of the same mod (the built-in one adds what was verified by hand). Everything
    /// else needs no rule: saved components replicate through the generic codec and GameSimulation systems are off on
    /// clients through the follower clock.
    /// </summary>
    public sealed class GenericModAdapter : ModAdapter
    {
        private readonly string _modName;
        private readonly string _assemblyName;
        private readonly string _summary;
        private readonly List<string> _local = new List<string>();
        private readonly List<string> _excluded = new List<string>();
        private readonly List<string> _clientOff = new List<string>();
        private readonly List<string> _joinOff = new List<string>();
        private readonly List<string> _kept = new List<string>();
        private readonly List<string> _pipeline = new List<string>();
        private readonly List<string> _settings = new List<string>();
        private readonly Dictionary<string, string> _settingsTypes = new Dictionary<string, string>();

        public GenericModAdapter(Assembly assembly, string modName, ModReport report)
        {
            Assembly = assembly;
            Report = report;
            _modName = modName;
            _assemblyName = report.AssemblyName;
            _local.AddRange(report.ComponentsWith(ComponentTreatment.ToolLocal));
            _excluded.AddRange(report.ComponentsWith(ComponentTreatment.Excluded));
            _clientOff.AddRange(report.SystemsWith(SystemTreatment.HostOnly));
            _joinOff.AddRange(report.SystemsWith(SystemTreatment.JoinOff));
            _kept.AddRange(report.SystemsWith(SystemTreatment.KeptOnClients));
            _pipeline.AddRange(report.ToolsWith(ToolTreatment.Pipeline));
            foreach (SettingDecision s in report.HostSettings)
            {
                // the wire names a setting by mod and property; the first settings class that declares the name wins
                if (_settingsTypes.ContainsKey(s.Name))
                {
                    report.Problems.Add("host-setting " + s + " has the same property name as " + _settingsTypes[s.Name] + "." + s.Name + "; only the first is synchronised");
                    continue;
                }
                _settingsTypes[s.Name] = s.SettingsType;
                _settings.Add(s.Name);
            }
            _summary = report.Summary();
        }

        /// <summary>The analysis this adapter was built from.</summary>
        public ModReport Report { get; }

        /// <summary>True when the adapter changes anything beyond what the codec and the follower clock do anyway.</summary>
        public bool HasRules => _local.Count + _excluded.Count + _clientOff.Count + _joinOff.Count + _kept.Count + _pipeline.Count + _settings.Count > 0;

        public override string ModName => _modName;

        public override string AssemblyName => _assemblyName;

        public override IEnumerable<string> LocalTypes => _local;

        public override IEnumerable<string> NeverReplicatedTypes => _excluded;

        public override IEnumerable<string> ClientOffSystems => _clientOff;

        public override IEnumerable<string> JoinOffSystems => _joinOff;

        public override IEnumerable<string> ClientSimulationSystems => _kept;

        public override IEnumerable<string> PipelineTools => _pipeline;

        public override IEnumerable<string> SyncedSettings => _settings;

        public override string SettingsTypeFor(string key) => _settingsTypes.TryGetValue(key ?? "", out string type) ? type : null;

        public override string Summary => _summary;
    }
}
