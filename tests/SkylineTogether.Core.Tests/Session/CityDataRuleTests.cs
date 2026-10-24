using System.Collections.Generic;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The join rules no option relaxes (<see cref="CityDataRule"/>, ARCHITECTURE.md#handshake-and-admission): a joiner
    /// that lacks one of the host's mods that store data in the city is always refused (the game's loader crashes on saved
    /// types of a missing mod), the old multiplayer mod refuses on either side, and the joiner checks the host's city-data
    /// mods again before it loads the city.
    /// </summary>
    public sealed class CityDataRuleTests
    {
        private const string Crash = "this city contains its data – loading it without the mod would crash the game";

        private static ModEntry Mod(string name, string version = "1.0.0.0", bool cityData = false, bool inCity = false, string id = null) =>
            new ModEntry { Name = name, Version = version, PlatformId = id, RegistersTypesOrSystemsOrPrefabs = true, StoresCityData = cityData, CityDataInCity = inCity };

        private static HandshakeManifest Manifest(params ModEntry[] mods)
        {
            var m = new HandshakeManifest
            {
                Mods = new List<ModEntry>(mods),
                Content = new List<ContentEntry>(),
                AssetPacks = new List<ContentEntry>(),
                Settings = new List<SettingEntry>(),
            };
            CompatibilityDigests.Fill(m);
            return m;
        }

        // the observed session: the host runs these, the joiner none of them
        private static HandshakeManifest ObservedHost() => Manifest(
            Mod("AchievementFixer", "1.3.1.0"),
            Mod("AreaBucket", cityData: true),
            Mod("ZoningToolkit", cityData: true, inCity: true));

        private static IEnumerable<CompatibilityOptions> EveryOption()
        {
            foreach (bool same in new[] { false, true })
            foreach (bool assets in new[] { false, true })
            foreach (bool allow in new[] { false, true })
            foreach (bool anyway in new[] { false, true })
            {
                yield return new CompatibilityOptions { RequireSameMods = same, RequireSameAssets = assets, AllowOverride = allow, JoinAnyway = anyway };
            }
        }

        // ------------------------------------------------------------------------------------------ classification

        [Test]
        public void SavedComponentsAndSavedSystemsStoreCityData()
        {
            var facts = new ModFacts { AssemblyName = "ZoningToolkit", PhasesKnown = true };
            facts.Components.Add(new ComponentFact { Name = "ZoningToolkit.Components.ZoningInfo", Serializable = true });
            facts.Components.Add(new ComponentFact { Name = "ZoningToolkit.Components.Temp", Serializable = false });
            CityDataEvidence e = CityDataEvidence.Of(facts);
            Assert.True(e.StoresData);
            Assert.SequenceEqual(new[] { "ZoningToolkit.Components.ZoningInfo" }, e.Components);
            Assert.Contains("1 component type (ZoningToolkit.Components.ZoningInfo)", e.Describe());

            var tag = new ModFacts { AssemblyName = "Tags" };
            tag.Components.Add(new ComponentFact { Name = "Tags.Marked", Storage = ComponentStorage.Tag, EmptySerializable = true });
            Assert.True(CityDataEvidence.Of(tag).StoresData, "a saved tag is in the save too");

            var system = new ModFacts { AssemblyName = "Sys" };
            system.Systems.Add(new SystemFact { Name = "Sys.StateSystem", SavesState = true });
            CityDataEvidence s = CityDataEvidence.Of(system);
            Assert.True(s.StoresData);
            Assert.Contains("1 system state (Sys.StateSystem)", s.Describe());
        }

        [Test]
        public void UnsavedTypesStoreNothingAndAFailedScanStoresData()
        {
            var ui = new ModFacts { AssemblyName = "Ui" };
            ui.Components.Add(new ComponentFact { Name = "Ui.Hover", Serializable = false });
            ui.Systems.Add(new SystemFact { Name = "Ui.PanelSystem" });
            Assert.False(CityDataEvidence.Of(ui).StoresData);
            Assert.Equal("stores no data in the city", CityDataEvidence.Of(ui).Describe());
            Assert.True(CityDataEvidence.Of(new ModFacts { AssemblyName = "X", ScanFailed = true }).StoresData, "fail closed");
            Assert.True(CityDataEvidence.Of(null).StoresData, "nothing known: fail closed");
        }

        [Test]
        public void ASavedTypeTheModNeverWritesIsCityDataOnlyWhereEntitiesHaveIt()
        {
            // Cim Route Highlighter declares a saved DeleteTimer and never puts it on an entity: only the save's type table
            // names it, which a game without the mod reads without data
            var tracker = new ModFacts { AssemblyName = "EmploymentTracker", PhasesKnown = true };
            tracker.Components.Add(new ComponentFact { Name = "EmploymentTracker.DeleteTimer", Serializable = true });
            CityDataEvidence e = CityDataEvidence.Of(tracker);
            Assert.False(e.StoresData);
            Assert.Equal(0, e.Components.Count);
            Assert.SequenceEqual(new[] { "EmploymentTracker.DeleteTimer" }, e.Dormant);
            Assert.Contains("stores no data in the city but declares 1 saved component type it never writes (EmploymentTracker.DeleteTimer", e.Describe());
            Assert.SequenceEqual(new[] { "EmploymentTracker.DeleteTimer" }, CityDataRule.DormantSavedTypes("employmenttracker"));
            ModReport r = ModClassifier.Classify(tracker);
            Assert.DoesNotContain("a joiner without the mod is refused", string.Join("\n", r.Describe()));

            // any other saved type of the same mod is city data as usual
            tracker.Components.Add(new ComponentFact { Name = "EmploymentTracker.RouteCache", Serializable = true });
            e = CityDataEvidence.Of(tracker);
            Assert.True(e.StoresData);
            Assert.SequenceEqual(new[] { "EmploymentTracker.RouteCache" }, e.Components);
            Assert.Contains("; declares 1 saved component type it never writes", e.Describe());

            // the list is per mod: the same type name in another assembly is city data
            var other = new ModFacts { AssemblyName = "Other" };
            other.Components.Add(new ComponentFact { Name = "EmploymentTracker.DeleteTimer", Serializable = true });
            Assert.True(CityDataEvidence.Of(other).StoresData);
            Assert.Equal(0, CityDataRule.DormantSavedTypes("Other").Count);
            Assert.Equal(0, CityDataRule.DormantSavedTypes(null).Count);
            Assert.True(CityDataEvidence.Of(new ModFacts { AssemblyName = "EmploymentTracker", ScanFailed = true }).StoresData, "fail closed");
        }

        [Test]
        public void DeclarationsNeverHideCityData()
        {
            var facts = new ModFacts { AssemblyName = "Declared", PhasesKnown = true };
            facts.Components.Add(new ComponentFact { Name = "Declared.Data", Serializable = true });
            var d = new ModDeclarations();
            d.AddLine("client-local: Declared", "Declarations[0]");
            d.AddLine("never-replicate: Declared.Data", "Declarations[1]");
            ModReport r = ModClassifier.Classify(facts, d);
            Assert.True(r.CityData.StoresData, "a type that never replicates is still saved with the city");
            Assert.Contains("city data: saves 1 component type", string.Join("\n", r.Describe()));
            Assert.Contains("a joiner without the mod is refused", string.Join("\n", r.Describe()));
        }

        [Test]
        public void TheBuiltInListKnowsModsThatSaveData()
        {
            Assert.True(CityDataRule.StoresCityData(new ModEntry { Name = "ZoningToolkit" }));
            Assert.True(CityDataRule.StoresCityData(new ModEntry { Name = "x", PlatformId = "80095" }), "Traffic by id");
            Assert.False(CityDataRule.StoresCityData(new ModEntry { Name = "MoveIt", PlatformId = "74324" }), "Move It saves nothing");
            Assert.False(CityDataRule.StoresCityData(new ModEntry { Name = "SomeUiMod" }));
            Assert.True(CityDataRule.StoresCityData(new ModEntry { Name = "SomeUiMod", StoresCityData = true }));
        }

        // ------------------------------------------------------------------------------------------ the host's rule

        [Test]
        public void AJoinerWithoutACityDataModIsRefusedWhateverTheOptions()
        {
            foreach (CompatibilityOptions o in EveryOption())
            {
                string which = "same=" + o.RequireSameMods + " assets=" + o.RequireSameAssets + " allow=" + o.AllowOverride + " anyway=" + o.JoinAnyway;
                CompatibilityResult r = CompatibilityCheck.Compare(ObservedHost(), Manifest(), o);
                Assert.NotNull(r.Refusal, which);
                Assert.Equal("mods", r.Refusal.Kind, which);
                Assert.False(r.Refusal.Overridable, "never overridable: " + which);
                Assert.Null(r.Notice, which);
                Assert.Equal(2, r.Refusal.CityData.Count, which);
                Assert.Equal("AreaBucket", r.Refusal.CityData[0].Name, which);
                Assert.False(r.Refusal.CityData[0].CityDataInCity, which);
                Assert.Equal("ZoningToolkit", r.Refusal.CityData[1].Name, which);
                Assert.True(r.Refusal.CityData[1].CityDataInCity, which);
                Assert.Contains("Enable these mods to join: AreaBucket (it can store data in a city – loading the city without the mod can crash the game), ZoningToolkit (" + Crash + ")", r.RefusalDetail, which);
                // the harmless one stays a normal difference
                Assert.Equal(1, r.Refusal.OtherMissing().Count, which);
                Assert.Equal("AchievementFixer", r.Refusal.OtherMissing()[0].Name, which);
                Assert.Equal(o.AllowOverride && o.RequireSameMods, r.Refusal.RestOverridable, which);
            }
        }

        [Test]
        public void TheRefusalNamesBothKindsAndReadsLikeTheRequest()
        {
            CompatibilityResult r = CompatibilityCheck.Compare(ObservedHost(), Manifest(Mod("Extra")), new CompatibilityOptions { RequireSameMods = true, AllowOverride = true, JoinAnyway = true });
            Assert.Contains("Enable these mods to join: AreaBucket", r.RefusalDetail);
            Assert.Contains("ZoningToolkit (" + Crash + ")", r.RefusalDetail);
            Assert.Contains("Other differences: Missing (enable these): AchievementFixer 1.3.1.0. Not on host (disable these): Extra 1.0.0.0.", r.RefusalDetail);
            Assert.Contains("Once these are enabled, the host lets you join anyway with the other differences.", r.RefusalDetail);
            Assert.DoesNotContain("Missing (enable these): AchievementFixer 1.3.1.0, AreaBucket", r.RefusalDetail, "a required mod is not listed twice");
            // older readers still see every missing mod in the plain list
            Assert.Equal(3, r.Refusal.Missing.Count);
        }

        [Test]
        public void TheBuiltInListCountsWithoutTheHostsOwnScan()
        {
            HandshakeManifest host = Manifest(Mod("ZoningToolkit"), Mod("Harmless"));
            CompatibilityResult r = CompatibilityCheck.Compare(host, Manifest(Mod("Harmless")), new CompatibilityOptions { RequireSameMods = false });
            Assert.NotNull(r.Refusal);
            Assert.Equal("ZoningToolkit", r.Refusal.CityData[0].Name);
        }

        [Test]
        public void AClientLocalModThatSavesDataIsRequiredToo()
        {
            var local = new ModEntry { Name = "EmploymentTracker", Version = "1.0", ClientLocalHint = true, StoresCityData = true };
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(local), Manifest(), new CompatibilityOptions { RequireSameMods = true, AllowOverride = true, JoinAnyway = true });
            Assert.NotNull(r.Refusal, "the join class does not matter: the save holds its type");
            Assert.Equal("EmploymentTracker", r.Refusal.CityData[0].Name);
            Assert.Equal(1, r.Refusal.Missing.Count, "listed as missing although client-local mods never count there");
        }

        [Test]
        public void ModsWithoutCityDataStayOverridableAndTheNoticeCarriesTheHostsList()
        {
            HandshakeManifest host = Manifest(Mod("ZoningToolkit", cityData: true, inCity: true), Mod("AchievementFixer", "1.3.1.0"));
            HandshakeManifest client = Manifest(Mod("ZoningToolkit", cityData: true), Mod("OnlyMine"));

            CompatibilityResult refused = CompatibilityCheck.Compare(host, client, new CompatibilityOptions { RequireSameMods = true, AllowOverride = true });
            Assert.NotNull(refused.Refusal);
            Assert.True(refused.Refusal.Overridable, "harmless differences: join anyway as before");
            Assert.Equal(0, refused.Refusal.CityData.Count);

            CompatibilityResult admitted = CompatibilityCheck.Compare(host, client, new CompatibilityOptions { RequireSameMods = true, AllowOverride = true, JoinAnyway = true });
            Assert.Null(admitted.Refusal);
            MismatchReport n = admitted.Notice;
            Assert.True(n.JoinedAnyway);
            Assert.True(n.CityDataChecked);
            Assert.Equal(1, n.HostCityData.Count);
            Assert.Equal("ZoningToolkit", n.HostCityData[0].Name);
            Assert.DoesNotContain("ZoningToolkit", n.Describe(), "the host's list is not a difference");
        }

        [Test]
        public void ExtraModsAndEqualListsAreUnaffected()
        {
            HandshakeManifest host = Manifest(Mod("ZoningToolkit", cityData: true, inCity: true));
            Assert.Null(CompatibilityCheck.Compare(host, Manifest(Mod("ZoningToolkit", cityData: true, inCity: true)), null).Refusal);
            CompatibilityResult extra = CompatibilityCheck.Compare(host, Manifest(Mod("ZoningToolkit"), Mod("Traffic", id: "80095")), new CompatibilityOptions { RequireSameMods = false });
            Assert.Null(extra.Refusal, "a city-data mod only the joiner has is no crash (like adding a mod to a save)");
            Assert.Equal("Traffic", extra.Notice.Extra[0].Name);
        }

        [Test]
        public void AJoinerListTooLongToSendCannotProveItHasThem()
        {
            HandshakeManifest host = Manifest(Mod("ZoningToolkit", cityData: true, inCity: true), Mod("Harmless"));
            HandshakeManifest client = Manifest(Mod("Other"));
            client.Mods = null;
            CompatibilityResult r = CompatibilityCheck.Compare(host, client, new CompatibilityOptions { RequireSameMods = false });
            Assert.NotNull(r.Refusal);
            Assert.Equal("ZoningToolkit", r.Refusal.CityData[0].Name);
            Assert.Contains("too long to compare", r.RefusalDetail);
        }

        // ------------------------------------------------------------------------------------- the old multiplayer mod

        [Test]
        public void TheOldMultiplayerModRefusesOnEitherSideWhateverTheOptions()
        {
            foreach (CompatibilityOptions o in EveryOption())
            {
                CompatibilityResult joiner = CompatibilityCheck.Compare(Manifest(Mod("A")), Manifest(Mod("A"), Mod("CS2MultiplayerMod")), o);
                Assert.Equal("conflict", joiner.Refusal.Kind);
                Assert.False(joiner.Refusal.Overridable);
                Assert.NotNull(joiner.Refusal.ClientConflict);
                Assert.Null(joiner.Refusal.HostConflict);
                Assert.Contains("Disable the 'CS2 Multiplayer Mod' – two multiplayer mods at once conflict", joiner.RefusalDetail);

                CompatibilityResult host = CompatibilityCheck.Compare(Manifest(Mod("A"), Mod("Renamed", id: "150432")), Manifest(Mod("A")), o);
                Assert.Equal("conflict", host.Refusal.Kind, "found by its Paradox Mods id");
                Assert.NotNull(host.Refusal.HostConflict);
                Assert.Contains("The host has the 'CS2 Multiplayer Mod' enabled", host.RefusalDetail);
            }
            // both have it: the lists are equal, still refused
            CompatibilityResult both = CompatibilityCheck.Compare(Manifest(Mod("CS2MultiplayerMod")), Manifest(Mod("CS2MultiplayerMod")), null);
            Assert.Equal("conflict", both.Refusal.Kind);
            Assert.NotNull(both.Refusal.HostConflict);
            Assert.NotNull(both.Refusal.ClientConflict);
        }

        [Test]
        public void TheOldModIsFoundByNameOrIdOnly()
        {
            Assert.True(CityDataRule.IsOldMultiplayerMod(new ModEntry { Name = "cs2multiplayermod" }));
            Assert.True(CityDataRule.IsOldMultiplayerMod(new ModEntry { Name = "Whatever", PlatformId = "150432" }));
            Assert.False(CityDataRule.IsOldMultiplayerMod(new ModEntry { Name = "CS2MultiplayerModHelper" }));
            Assert.False(CityDataRule.IsOldMultiplayerMod(new ModEntry { Name = "SkylineTogether", PlatformId = "1504320" }));
            Assert.False(CityDataRule.IsOldMultiplayerMod(null));
        }

        // ------------------------------------------------------------------------------------------ the report

        [Test]
        public void TheNewRecordsRoundTripAndOldRecordsStay()
        {
            var r = new MismatchReport { Kind = "mods", RestOverridable = true };
            r.CityData.Add(new ModEntry { Name = "ZoningToolkit", Version = "1.0.0.0", StoresCityData = true, CityDataInCity = true });
            r.CityData.Add(new ModEntry { Name = "AreaBucket", Version = "1.0.0.0", PlatformId = "123", StoresCityData = true });
            r.Missing.Add(new ModEntry { Name = "ZoningToolkit", Version = "1.0.0.0" });
            MismatchReport back = MismatchReport.Decode(MismatchReport.Encode(r));
            Assert.Equal(2, back.CityData.Count);
            Assert.True(back.CityData[0].CityDataInCity);
            Assert.False(back.CityData[1].CityDataInCity);
            Assert.Equal("123", back.CityData[1].PlatformId);
            Assert.True(back.RestOverridable);
            Assert.False(back.Overridable);
            Assert.Equal(1, back.Missing.Count);
            Assert.Equal(0, back.OtherMissing().Count);

            var n = new MismatchReport { Kind = "notice", CityDataChecked = true };
            n.HostCityData.Add(new ModEntry { Name = "Traffic", Version = "2", PlatformId = "80095", CityDataInCity = true });
            MismatchReport nb = MismatchReport.Decode(MismatchReport.Encode(n));
            Assert.True(nb.CityDataChecked);
            Assert.Equal("Traffic", nb.HostCityData[0].Name);
            Assert.True(nb.HostCityData[0].StoresCityData);
            Assert.False(nb.HasDifferences, "the host's list is no difference");

            MismatchReport c = MismatchReport.Decode(MismatchReport.Encode(CityDataRule.Conflict(new ModEntry { Name = "CS2MultiplayerMod", Version = "1.0" }, null)));
            Assert.Equal("conflict", c.Kind);
            Assert.Equal("CS2MultiplayerMod", c.HostConflict.Name);
            Assert.Null(c.ClientConflict);
        }

        [Test]
        public void AReaderSkipsRecordsItDoesNotKnow()
        {
            byte[] future = System.Text.Encoding.UTF8.GetBytes("kind\tmods\ncity-data\tZ\t1\t\t1\nsomething-new\tx\nmissing\tZ\t1\t\n");
            MismatchReport r = MismatchReport.Decode(future);
            Assert.Equal(1, r.CityData.Count);
            Assert.Equal(1, r.Missing.Count);
        }

        // ------------------------------------------------------------------------------------- the joiner's own check

        [Test]
        public void TheJoinerStopsBeforeLoadingWhenItLacksAHostCityDataMod()
        {
            var notice = new MismatchReport { Kind = "notice", CityDataChecked = true };
            notice.HostCityData.Add(new ModEntry { Name = "ZoningToolkit", Version = "1.0.0.0", StoresCityData = true, CityDataInCity = true });
            notice.Extra.Add(new ModEntry { Name = "Mine" });

            MismatchReport stop = CityDataRule.CheckBeforeLoad(notice, new List<ModEntry> { Mod("Mine") });
            Assert.NotNull(stop);
            Assert.Equal("mods", stop.Kind);
            Assert.False(stop.Overridable);
            Assert.Equal("ZoningToolkit", stop.CityData[0].Name);
            Assert.True(stop.CityData[0].CityDataInCity);
            Assert.Contains("Enable these mods to join: ZoningToolkit (" + Crash + ")", stop.Describe());

            Assert.Null(CityDataRule.CheckBeforeLoad(notice, new List<ModEntry> { Mod("zoningtoolkit"), Mod("Mine") }), "names compare case-insensitively");
            Assert.Null(CityDataRule.CheckBeforeLoad(null, new List<ModEntry>()), "no notice: the host found the same mods");
        }

        [Test]
        public void ANoticeOfAnOlderHostCountsEveryMissingModAsCityData()
        {
            var old = new MismatchReport { Kind = "notice", JoinedAnyway = true };
            old.Missing.Add(new ModEntry { Name = "AchievementFixer", Version = "1.3.1.0" });
            old.Missing.Add(new ModEntry { Name = "ZoningToolkit", Version = "1.0.0.0" });
            MismatchReport stop = CityDataRule.CheckBeforeLoad(old, new List<ModEntry> { Mod("AchievementFixer") });
            Assert.NotNull(stop, "an older host does not say which mods store data: fail closed");
            Assert.Equal(1, stop.CityData.Count);
            Assert.Equal("ZoningToolkit", stop.CityData[0].Name);
            Assert.False(stop.CityData[0].CityDataInCity, "not confirmed either way");

            var checkedEmpty = new MismatchReport { Kind = "notice", CityDataChecked = true, JoinedAnyway = true };
            checkedEmpty.Missing.Add(new ModEntry { Name = "AchievementFixer" });
            Assert.Null(CityDataRule.CheckBeforeLoad(checkedEmpty, new List<ModEntry>()), "a host that checked: harmless differences pass");
        }

        [Test]
        public void TheJoinerStopsWhenItRunsTheOldMultiplayerMod()
        {
            MismatchReport stop = CityDataRule.CheckBeforeLoad(null, new List<ModEntry> { Mod("CS2MultiplayerMod") });
            Assert.NotNull(stop);
            Assert.Equal("conflict", stop.Kind);
            Assert.NotNull(stop.ClientConflict);
        }

        // ------------------------------------------------------------------------------------------ in a session

        private static void HostRunsZoningToolkit(SessionRig rig)
        {
            rig.HostInfo.Mods.Add(new ModEntry { Name = "ZoningToolkit", Version = "1.0.0.0", StoresCityData = true, CityDataInCity = true });
        }

        [Test]
        public void JoinAnywayCannotPassAModWithCityData()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", RequireSameMods = true, AllowModMismatchOverride = true });
            HostRunsZoningToolkit(rig);
            var blocked = new List<string>();
            rig.Host.JoinerBlocked += (who, report) => blocked.Add(who + ": " + report.CityData[0].Name);
            var (client, loader, _) = rig.AddClient("Anna", joinAnyway: true);
            rig.Run(200);
            Assert.Equal(ClientEndKind.Refused, client.EndKind, client.EndReason);
            Assert.Equal(RefuseReason.Mismatch, client.RefusedFor);
            Assert.False(client.EndReport.Overridable);
            Assert.Equal("ZoningToolkit", client.EndReport.CityData[0].Name);
            Assert.True(client.EndReport.CityData[0].CityDataInCity);
            Assert.Contains("Enable these mods to join: ZoningToolkit (" + Crash + ")", client.EndReason);
            Assert.Null(loader.Target, "nothing downloaded");
            Assert.SequenceEqual(new[] { "Anna: ZoningToolkit" }, blocked);
            Assert.Contains("no option lets a player join without mods that store data in this city", string.Join("\n", rig.Log.Lines));
        }

        [Test]
        public void WithoutTheModRequirementTheJoinerIsStillRefused()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", RequireSameMods = false });
            HostRunsZoningToolkit(rig);
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(200);
            Assert.Equal(ClientEndKind.Refused, client.EndKind, client.EndReason);
            Assert.Equal("ZoningToolkit", client.EndReport.CityData[0].Name);
        }

        [Test]
        public void AJoinerWithTheCityDataModsAndHarmlessDifferencesGoesLive()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", RequireSameMods = false });
            HostRunsZoningToolkit(rig);
            var (client, _, _) = rig.AddClient("Anna", tweak: i =>
            {
                i.Mods.Clear(); // lacks Move It (no city data)
                i.Mods.Add(new ModEntry { Name = "ZoningToolkit", Version = "1.0.0.0" });
            });
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.True(client.JoinNotice.CityDataChecked);
            Assert.Equal("ZoningToolkit", client.JoinNotice.HostCityData[0].Name);
            Assert.Equal("Move It", client.JoinNotice.Missing[0].Name);
        }

        [Test]
        public void TheJoinerDoesNotLoadACityWhoseModItNoLongerHas()
        {
            // the host admitted a joiner that reported the mod; this game no longer has it when the notice arrives
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", RequireSameMods = false });
            HostRunsZoningToolkit(rig);
            var (client, loader, info) = rig.AddClient("Anna", tweak: i =>
            {
                i.Mods.Add(new ModEntry { Name = "ZoningToolkit", Version = "1.0.0.0" });
                i.Mods.Add(new ModEntry { Name = "OnlyMine", Version = "1.0" });
            });
            rig.Run(1);
            Assert.True(client.State != ClientState.Ended, client.EndReason);
            info.Mods.RemoveAll(m => m.Name == "ZoningToolkit");
            rig.Run(400);
            Assert.Equal(ClientEndKind.Refused, client.EndKind, client.EndReason);
            Assert.False(client.EnteredCity, "the city was never loaded");
            Assert.Equal("ZoningToolkit", client.EndReport.CityData[0].Name);
            Assert.False(client.EndReport.Overridable);
            Assert.Contains("not loading the host's city", string.Join("\n", rig.Log.Lines));
        }

        [Test]
        public void TheHostWithTheOldModRefusesEveryJoiner()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", RequireSameMods = false, AllowModMismatchOverride = true });
            rig.HostInfo.Mods.Add(new ModEntry { Name = "CS2MultiplayerMod", Version = "1.0.0.0" });
            var blocked = new List<MismatchReport>();
            rig.Host.JoinerBlocked += (who, report) => blocked.Add(report);
            var (client, _, _) = rig.AddClient("Anna", tweak: i => i.Mods.Add(new ModEntry { Name = "CS2MultiplayerMod", Version = "1.0.0.0" }), joinAnyway: true);
            rig.Run(200);
            Assert.Equal(ClientEndKind.Refused, client.EndKind, client.EndReason);
            Assert.Equal("conflict", client.EndReport.Kind);
            Assert.NotNull(client.EndReport.HostConflict);
            Assert.Equal(1, blocked.Count);
        }
    }
}
