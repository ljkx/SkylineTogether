using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Tests.Session;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// Support for Show Text Trends, Prefab Asset Fixes, Transport Policy Adjuster, Magic Hearse, Road Speed Adjuster, Zone
    /// Organizer, Advanced Simulation Speed, Skyve and Simple Mod Checker Plus (docs/design/MOD-COMPATIBILITY.md §2): their
    /// classification, the adapters (checked on the game layer's sources, which cannot be compiled here) and the exact game
    /// speed that Advanced Simulation Speed needs from the speed sync.
    /// </summary>
    public sealed class SimpleModSupportTests
    {
        private const string Adapters = "src/SkylineTogether/Compat/SimpleModAdapters.cs";
        private const string Registry = "src/SkylineTogether/Compat/ModCompat.cs";

        private static ModEntry M(string name, string id, bool registers = false)
        {
            return new ModEntry { Name = name, Version = "1.0", PlatformId = id, RegistersTypesOrSystemsOrPrefabs = registers };
        }

        private static string Read(string relative)
        {
            if (!TestPaths.TryGetRepoFile(relative, out string path)) Assert.Fail(relative + " is missing");
            return File.ReadAllText(path);
        }

        // ------------------------------------------------------------------------------------------ classification

        // assembly name, Paradox Mods id of the mods that never block a join
        private static readonly string[][] Local =
        {
            new[] { "Show Text Trends", "83733" }, new[] { "ZoneOrganizer", "95965" }, new[] { "AdvancedSimulationSpeed", "124304" },
            new[] { "MagicHearse", "123497" }, new[] { "SimpleModCheckerPlus", "79186" }, new[] { "Skyve Mod", "75804" },
        };

        private static readonly string[][] World =
        {
            new[] { "PrefabAssetFixes", "113708" }, new[] { "TransportPolicyAdjuster", "78622" }, new[] { "RoadSpeedAdjuster", "125866" },
        };

        [Test]
        public void TheClientLocalModsNeverBlockAndAreTiedToTheCheckedVersion()
        {
            foreach (string[] mod in Local)
            {
                Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M(mod[0], null)), mod[0]);
                Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M("renamed", mod[1])), mod[1]);
                Assert.True(ModCompatibility.HasVerifiedProfile(mod[0], null), mod[0] + " has no check against one version");
                // a peer whose copy has more than the checked version says so, and the scan decides (the mod's id is the key)
                Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M(mod[0], mod[1], registers: true)), mod[0]);
                // missing on one side or another version: told, never refused
                var host = new List<ModEntry> { M(mod[0], mod[1]) };
                Assert.False(ModCompatibility.Compare(host, new List<ModEntry>()).Blocks, mod[0] + " missing on the joiner");
                Assert.False(ModCompatibility.Compare(new List<ModEntry>(), host).Blocks, mod[0] + " only on the joiner");
            }
            // the original Advanced Simulation Speed (it fails to start in game 1.6.2) is local too
            Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M("renamed", "79794")));
        }

        [Test]
        public void TheWorldAffectingModsAreKnownAndRoadSpeedAdjusterStoresCityData()
        {
            foreach (string[] mod in World)
            {
                Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M(mod[0], null)), mod[0]);
                Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(new ModEntry { Name = "renamed", PlatformId = mod[1], ClientLocalHint = true }), mod[1]);
                Assert.True(ModCompatibility.IsKnownWorldAffecting(mod[0], null), mod[0]);
                var host = new List<ModEntry> { M(mod[0], mod[1]) };
                Assert.True(ModCompatibility.Compare(host, new List<ModEntry>()).Blocks, mod[0] + " missing on the joiner");
            }
            // its CustomSpeed component is saved with the road edges: a joiner without the mod would crash the game's loader
            Assert.True(CityDataRule.IsKnownCityData("RoadSpeedAdjuster", null));
            Assert.True(CityDataRule.IsKnownCityData("renamed", "125866"));
            Assert.False(CityDataRule.IsKnownCityData("PrefabAssetFixes", "113708"), "it saves no type of its own");
            Assert.False(CityDataRule.IsKnownCityData("TransportPolicyAdjuster", "78622"), "it saves no type of its own");
            Assert.False(ModCompatibility.IsKnownClientLocal("RoadSpeedAdjuster", "125866"));
        }

        private static ModFacts Scan(string assembly, params SystemFact[] systems)
        {
            var facts = new ModFacts { AssemblyName = assembly, PhasesKnown = true, PatchedMethods = new List<string>() };
            facts.Systems.AddRange(systems);
            return facts;
        }

        private static SystemFact Sys(string name, params string[] phases)
        {
            var s = new SystemFact { Name = name };
            s.Phases.AddRange(phases);
            return s;
        }

        // the scans of the checked versions as their code shows them (decompiled installed versions)
        private static IEnumerable<ModFacts> CheckedVersions()
        {
            yield return Scan("Show Text Trends", Sys("Show_Text_Trends.MoneyUISystem", "UIUpdate"));
            yield return Scan("Skyve Mod", Sys("Skyve.Mod.CS2.InstallSkyveUISystem", "UIUpdate"), Sys("Skyve.Mod.CS2.AchievementsSystem"));
            yield return Scan("AdvancedSimulationSpeed", Sys("AdvancedSimulationSpeed.Systems.AdvancedSimSpeedUISystem", "UIUpdate"));
            ModFacts zones = Scan("ZoneOrganizer", Sys("ZoneOrganizer.ZoneOrganizerSystem"));
            zones.RuntimePrefabCreators.Add("ZoneOrganizer.ZoneOrganizerSystem.CreateUIAssetCategoryPrefab");
            yield return zones;
            yield return Scan("MagicHearse", Sys("MagicHearse.MagicHearseSystem", "GameSimulation"), Sys("MagicHearse.CemeteryResetSystem", "GameSimulation"),
                Sys("MagicHearse.HearseWarningDelaySystem", "GameSimulation"), Sys("MagicHearse.FuneralDirectorSystem", "PrefabUpdate"), Sys("MagicHearse.DeathcareStatusSystem"));
            yield return Scan("SimpleModCheckerPlus", Sys("SimpleModCheckerPlus.Systems.AutosaveOffCheck", "MainLoop"), Sys("SimpleModCheckerPlus.Systems.CocCleaner"),
                Sys("SimpleModCheckerPlus.Systems.ModCheckup"), Sys("SimpleModCheckerPlus.Systems.GameSettingsBackup"));
        }

        [Test]
        public void TheCheckedVersionsMatchTheirChecks()
        {
            int n = 0;
            foreach (ModFacts f in CheckedVersions())
            {
                List<string> beyond = ModCompatibility.BeyondVerifiedLocal(f);
                Assert.NotNull(beyond, f.AssemblyName);
                Assert.Equal(0, beyond.Count, f.AssemblyName + ": " + string.Join(", ", beyond));
                n++;
            }
            Assert.Equal(Local.Length, n);
        }

        [Test]
        public void ACopyThatDoesMoreIsNotTheCheckedVersion()
        {
            // a speed mod that starts to simulate, a Magic Hearse that saves data or patches the game, a Skyve with a tool
            ModFacts speed = Scan("AdvancedSimulationSpeed", Sys("AdvancedSimulationSpeed.Systems.AdvancedSimSpeedUISystem", "UIUpdate"), Sys("AdvancedSimulationSpeed.Systems.StepSystem", "GameSimulation"));
            Assert.SequenceEqual(new[] { "system AdvancedSimulationSpeed.Systems.StepSystem in GameSimulation" }, ModCompatibility.BeyondVerifiedLocal(speed));

            ModFacts hearse = Scan("MagicHearse", Sys("MagicHearse.MagicHearseSystem", "GameSimulation"), Sys("MagicHearse.CemeteryResetSystem", "GameSimulation"),
                Sys("MagicHearse.HearseWarningDelaySystem", "GameSimulation"), Sys("MagicHearse.FuneralDirectorSystem", "PrefabUpdate"));
            hearse.Components.Add(new ComponentFact { Name = "MagicHearse.WorkProviderMax", Serializable = true });
            hearse.PatchedMethods.Add("Game.Simulation.DeathcareFacilityAISystem.OnUpdate");
            hearse.Systems.Add(Sys("MagicHearse.NewSystem", "Modification4"));
            Assert.SequenceEqual(new[] { "saved component MagicHearse.WorkProviderMax", "system MagicHearse.NewSystem in Modification4", "patch Game.Simulation.DeathcareFacilityAISystem.OnUpdate" },
                ModCompatibility.BeyondVerifiedLocal(hearse));

            ModFacts trends = Scan("Show Text Trends", Sys("Show_Text_Trends.MoneyUISystem", "UIUpdate"));
            trends.Systems[0].WritesEntities = true;
            Assert.SequenceEqual(new[] { "system Show_Text_Trends.MoneyUISystem in UIUpdate" }, ModCompatibility.BeyondVerifiedLocal(trends));

            ModFacts zones = Scan("ZoneOrganizer", Sys("ZoneOrganizer.ZoneOrganizerSystem", "GameSimulation"));
            zones.RuntimePrefabCreators.Add("ZoneOrganizer.ZoneOrganizerSystem.CreateUIAssetCategoryPrefab");
            zones.RuntimePrefabCreators.Add("ZoneOrganizer.Other.Make");
            Assert.SequenceEqual(new[] { "system ZoneOrganizer.ZoneOrganizerSystem in GameSimulation", "runtime prefabs in ZoneOrganizer.Other.Make" }, ModCompatibility.BeyondVerifiedLocal(zones));
        }

        [Test]
        public void TheCoveredMagicHearseSystemsAreExactlyItsGameplaySystems()
        {
            // the check lists the systems by phase: a system in another phase is not covered
            ModFacts moved = Scan("MagicHearse", Sys("MagicHearse.FuneralDirectorSystem", "Modification5"));
            Assert.SequenceEqual(new[] { "system MagicHearse.FuneralDirectorSystem in Modification5" }, ModCompatibility.BeyondVerifiedLocal(moved));
            Assert.Equal(4, ModCompatibility.VerifiedLocalItems("MagicHearse").Count);
            Assert.Equal(0, ModCompatibility.VerifiedLocalItems("Skyve Mod").Count);
        }

        // ------------------------------------------------------------------------------------------ adapters (sources)

        private static readonly Regex Class = new Regex(@"public sealed class (\w+Adapter) : ModAdapter\s*\{(.*?)\n    \}", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex AssemblyName = new Regex("AssemblyName => \"([^\"]+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex SystemNames = new Regex(@"(JoinOffSystems|ClientOffSystems) => new\[\] \{([^}]*)\}", RegexOptions.CultureInvariant);
        private static readonly Regex MemberNames = new Regex(@"Members => new\[\]\s*\{([^}]*)\}", RegexOptions.CultureInvariant);

        [Test]
        public void EveryAdapterIsRegisteredAndNamesOnlyTypesOfItsMod()
        {
            string adapters = Read(Adapters);
            string registry = Read(Registry);
            var seen = new List<string>();
            foreach (Match m in Class.Matches(adapters))
            {
                string name = m.Groups[1].Value;
                string body = m.Groups[2].Value;
                seen.Add(name);
                Assert.Contains("new " + name + "()", registry, name + " is not registered in ModCompat");
                Match a = AssemblyName.Match(body);
                Assert.True(a.Success, name + " names no assembly");
                string assembly = a.Groups[1].Value;
                bool local = ModCompatibility.IsKnownClientLocal(assembly, null);
                bool world = ModCompatibility.IsKnownWorldAffecting(assembly, null);
                Assert.True(local != world, assembly + " must be on exactly one of the built-in lists");
                foreach (Match list in SystemNames.Matches(body))
                {
                    foreach (Match q in Regex.Matches(list.Groups[2].Value, "\"([^\"]+)\""))
                    {
                        string target = q.Groups[1].Value;
                        Assert.True(target.StartsWith(assembly + ".", StringComparison.Ordinal), name + ": " + target + " is not a type of " + assembly);
                        if (!local) continue;
                        // the system a client-local mod's adapter switches off is one of the parts of its check
                        bool covered = false;
                        foreach (string v in ModCompatibility.VerifiedLocalItems(assembly)) covered |= v.StartsWith("system " + target + " in ", StringComparison.Ordinal);
                        Assert.True(covered, name + ": " + target + " is not part of the check of " + assembly);
                    }
                }
                foreach (Match list in MemberNames.Matches(body))
                {
                    foreach (Match q in Regex.Matches(list.Groups[1].Value, "\"([^\"]+)\""))
                    {
                        string target = q.Groups[1].Value;
                        Assert.True(target.StartsWith(assembly + ".", StringComparison.Ordinal) && target.Contains("::"), name + ": " + target + " is not a member of " + assembly);
                    }
                }
            }
            Assert.SequenceEqual(new[] { "PrefabAssetFixesAdapter", "TransportPolicyAdjusterAdapter", "MagicHearseAdapter", "RoadSpeedAdjusterAdapter", "AdvancedSimulationSpeedAdapter", "SimpleModCheckerPlusAdapter" }, seen);
        }

        [Test]
        public void ThePrefabEditsAreTheHostsAndTheRulesOfTheOtherAdaptersAreDeclared()
        {
            string adapters = Read(Adapters);
            // Prefab Asset Fixes: every toggle that edits prefab data is a host setting, the load-time city fix is off on joiners
            Assert.Contains("SettingsTypeName => \"PrefabAssetFixes.Setting\"", adapters);
            foreach (string key in new[] { "IndustrialCompanyWorker", "Prison", "Hospital", "USSWHospital", "HoveringPoles", "RSClinic", "LHTBusStation02", "LHTTaxiDepot01", "LHTTramDepot01", "LHTCargoHarbor01", "RHTBusStation01", "NLLowHousehold", "AdditionalTransformers", "FRCiltyHall" })
            {
                Assert.Contains("\"" + key + "\"", adapters, key);
            }
            Assert.Contains("JoinOffSystems => new[] { \"PrefabAssetFixes.Systems.AssetFixSystem\" }", adapters);
            Assert.Contains("\"PrefabAssetFixes.Mod::m_Setting\"", adapters);
            // the mod keeps its settings in a static field, which the settings channel cannot find on the mod instance
            Assert.Contains("public override object SettingsInstance()", adapters);
            // the file-based data of Road Speed Adjuster is never opened by a joined player
            Assert.Contains("JoinOffSystems => new[] { \"RoadSpeedAdjuster.Systems.RoadSpeedSaveDataSystem\" }", adapters);
            Assert.Contains("JoinOffSystems => new[] { \"MagicHearse.FuneralDirectorSystem\" }", adapters);
            Assert.Contains("JoinOffSystems => new[] { \"SimpleModCheckerPlus.Systems.AutosaveOffCheck\" }", adapters);
            Assert.Contains("AllowsFineGameSpeed => true", adapters);
            Assert.Contains("RoutePolicyRangeSlack => Core.Sync.GlobalProposalRules.RoutePolicyRangeSlack", adapters);
        }

        // ------------------------------------------------------------------------------------------ exact game speed

        private static byte[] Encode(SpeedState s)
        {
            var w = new ByteWriter();
            SessionPayloads.WriteSpeed(w, s);
            return w.ToArray();
        }

        private static SpeedState Decode(byte[] bytes)
        {
            return SessionPayloads.ReadSpeed(new ByteReader(bytes, 0, bytes.Length));
        }

        [Test]
        public void TheGamesOwnSpeedsKeepTheirTwoBytes()
        {
            foreach (int preset in new[] { 1, 2, 4 })
            {
                SpeedState s = SpeedState.Of(preset, false, false);
                Assert.Equal(0f, s.Exact, "a preset carries no exact value");
                Assert.Equal(preset, s.Speed);
                byte[] bytes = Encode(s);
                Assert.SequenceEqual(new byte[] { (byte)preset, 0 }, bytes);
                Assert.Equal(s, Decode(bytes));
            }
            Assert.SequenceEqual(new byte[] { 3, 3 }, Encode(new SpeedState { Speed = 3, Paused = true, Held = true }), "the old payload is unchanged");
            Assert.SequenceEqual(new byte[] { 2, 1 }, Encode(SpeedState.Of(2f, true, false)));
        }

        [Test]
        public void AnExactSpeedTravelsAfterTheTwoBytesOlderPeersRead()
        {
            foreach (float speed in new[] { 0.25f, 0.5f, 0.3f, 1.5f, 3f, 5f, 8f, 0.01f })
            {
                SpeedState s = SpeedState.Of(speed, false, false);
                Assert.Equal(speed, s.Exact, "exact " + speed);
                Assert.Equal(SpeedState.Bucket(speed), s.Speed);
                byte[] bytes = Encode(s);
                Assert.Equal(6, bytes.Length);
                // an older peer reads the first two bytes: the speed rounded to the game's own, and the flags it knows
                Assert.Equal((byte)SpeedState.Bucket(speed), bytes[0]);
                Assert.Equal(0, bytes[1] & 3);
                SpeedState back = Decode(bytes);
                Assert.Equal(s, back);
                Assert.Equal(speed, back.Multiplier);
            }
            SpeedState paused = SpeedState.Of(3f, true, true);
            Assert.Equal(3, Encode(paused)[1] & 7 & ~4, "paused and held flags stay where they were");
            SpeedState both = Decode(Encode(paused));
            Assert.True(both.Paused && both.Held);
            Assert.Equal(3f, both.Exact);
            Assert.Equal(2f, SpeedState.Of(2f, false, false).Multiplier);
        }

        [Test]
        public void AnInvalidExactSpeedFallsBackToTheRoundedOne()
        {
            Assert.Equal(1f, SpeedState.Sanitize(float.NaN));
            Assert.Equal(1f, SpeedState.Sanitize(float.PositiveInfinity));
            Assert.Equal(SpeedState.MaxExact, SpeedState.Sanitize(100f));
            Assert.Equal(SpeedState.MinExact, SpeedState.Sanitize(0f));
            Assert.Equal(SpeedState.MinExact, SpeedState.Sanitize(-3f));
            Assert.Equal(8f, SpeedState.Of(1e9f, false, false).Exact);
            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -1f, 0.001f, 8.5f, 1e30f })
            {
                var w = new ByteWriter();
                w.WriteByte(2);
                w.WriteByte(4);
                w.WriteSingle(bad);
                SpeedState s = Decode(w.ToArray());
                Assert.Equal(0f, s.Exact, "ignored: " + bad);
                Assert.Equal(2, s.Speed);
                Assert.Equal(2f, s.Multiplier);
            }
            // a flagged payload that is too short for the float is read as the rounded speed (a sender's bug, not a crash)
            SpeedState cut = Decode(new byte[] { 4, 4, 0, 0 });
            Assert.Equal(0f, cut.Exact);
            Assert.Equal(4, cut.Speed);
            // writing an exact value outside the range is left out
            Assert.Equal(2, Encode(new SpeedState { Speed = 2, Exact = 50f }).Length);
            Assert.Equal(2, Encode(new SpeedState { Speed = 2, Exact = float.NaN }).Length);
        }

        [Test]
        public void TheHostsExactSpeedReachesEveryPlayerAndAPlayersRequestReachesTheHost()
        {
            var rig = new SessionRig();
            var (anna, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            Assert.Equal(ClientState.Live, anna.State, anna.EndReason);
            rig.Host.Speed = SpeedState.Of(0.25f, false, false);
            rig.Run(60);
            Assert.Equal(0.25f, anna.Speed.Exact);
            Assert.Equal(1, anna.Speed.Speed);
            Assert.Equal(0.25f, anna.Speed.Multiplier);
            rig.Host.Speed = SpeedState.Of(8f, false, false);
            rig.Run(60);
            Assert.Equal(8f, anna.Speed.Multiplier);
            rig.Host.Speed = SpeedState.Of(2f, false, false);
            rig.Run(60);
            Assert.Equal(2f, anna.Speed.Multiplier);
            Assert.Equal(0f, anna.Speed.Exact, "back to one of the game's speeds");

            var asked = new List<SpeedState>();
            rig.Host.SpeedRequested += (p, s) => asked.Add(s);
            anna.RequestSpeed(SpeedState.Of(3f, false, false));
            rig.Run(30);
            Assert.Equal(1, asked.Count);
            Assert.Equal(3f, asked[0].Exact);
            Assert.Equal(2, asked[0].Speed);
        }

        // ------------------------------------------------------------------------------------------ policy range

        [Test]
        public void ARoutePolicyValueMayLeaveTheSliderOnlyBySlackSpans()
        {
            // the exact range without slack, as before
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(500f, true, 0f, 100f, out float a));
            Assert.Equal(100f, a);
            // with slack the range grows by that many spans on each side
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(500f, true, 0f, 100f, out a, 100f));
            Assert.Equal(500f, a);
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(-250f, true, 0f, 100f, out a, 100f));
            Assert.Equal(-250f, a);
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(1e9f, true, 0f, 100f, out a, 100f));
            Assert.Equal(100f + 100f * 100f, a);
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(-1e9f, true, -50f, 50f, out a, 100f));
            Assert.Equal(-50f - 100f * 100f, a);
            // more slack than the constant allows is cut, and bad input is still refused
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(1e9f, true, 0f, 100f, out a, 1e6f));
            Assert.Equal(100f + GlobalProposalRules.RoutePolicyRangeSlack * 100f, a);
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(1e9f, true, 0f, 100f, out a, -5f));
            Assert.Equal(100f, a, "a negative slack counts as none");
            Assert.False(GlobalProposalRules.TryPolicyAdjustment(float.NaN, true, 0f, 100f, out _, 100f));
            Assert.False(GlobalProposalRules.TryPolicyAdjustment(1f, true, 100f, 0f, out _, 100f), "an inverted range");
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(3e9f, true, 0f, 1e6f, out a, 100f));
            Assert.Equal(GlobalProposalRules.MaxUnrangedAdjustment, a, "never beyond the cap of a policy without a range");
        }
    }
}
