using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Colossal;
using Colossal.PSI.Common;
using Game.Modding;
using Game.Prefabs;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using Unity.Entities;

namespace SkylineTogether.Session
{
    /// <summary>
    /// This game's handshake data (ARCHITECTURE.md#handshake-and-admission): protocol version, Skyline Together build, game
    /// version, and the lists the host compares after the password, each with its digest
    /// (<see cref="CompatibilityDigests"/>): loaded code mods, DLC and content packs whose prefabs are available, asset
    /// packs (Paradox Mods packages that add prefabs, <see cref="Compat.AssetSources.Packages"/>) and the current values
    /// of the simulation-relevant mod settings the host pushes (<see cref="Compat.ModSettingsSync.CurrentValues"/>).
    /// The DLC and asset pack lists are cached until the number of prefabs changes. Main thread; every read is guarded
    /// (a list that cannot be read is sent empty, never breaks hosting or joining).
    /// </summary>
    public sealed class GameInfoProvider : IGameInfoProvider
    {
        private static ulong _build;
        private static string _buildSource;
        private static string _buildSourceDetail;

        private readonly ulong _modBuild;
        private int _cachedPrefabCount = int.MinValue;
        private List<ContentEntry> _content = new List<ContentEntry>();
        private List<ContentEntry> _packs = new List<ContentEntry>();

        public GameInfoProvider()
        {
            _modBuild = Build;
        }

        public ulong ModBuild => _modBuild;

        /// <summary>
        /// The build id (<see cref="Core.Session.BuildId"/>): the first 48 bits of the SHA-256 of the loaded
        /// SkylineTogether.dll file, the value the installer prints. The game loads mods from the DLL's bytes, so
        /// <see cref="Assembly.Location"/> is empty; the file is found through the game's mod manager
        /// (<see cref="Diagnostics.InstallInfo.ReadOwnDll"/>), in the local Mods folder and in a Paradox Mods subscription
        /// alike. Only when no file can be read it falls back to the assembly's MVID, and <see cref="BuildSource"/> says so
        /// (the panel then shows "(mvid)" and the id differs from the installer's). Computed once, on first use: the first
        /// use is in <c>Mod.OnLoad</c>, when the mod manager already knows this mod.
        /// </summary>
        public static ulong Build
        {
            get
            {
                if (_buildSource == null) ComputeBuild();
                return _build;
            }
        }

        /// <summary>"dll" (SHA-256 of the file, as the installer) or "mvid" (fallback).</summary>
        public static string BuildSource
        {
            get
            {
                if (_buildSource == null) ComputeBuild();
                return _buildSource;
            }
        }

        /// <summary>For the log: how the build id was found ("the DLL file (mod manager)", "the assembly's MVID (...)").</summary>
        public static string BuildSourceDetail
        {
            get
            {
                if (_buildSource == null) ComputeBuild();
                return _buildSourceDetail;
            }
        }

        /// <summary>The build id as 12 hex digits, e.g. "2f076aad4234" (with " (mvid)" for the fallback).</summary>
        public static string BuildText => Core.Session.BuildId.Format(Build) + (BuildSource == "dll" ? "" : " (" + BuildSource + ")");

        private static void ComputeBuild()
        {
            Assembly own = typeof(GameInfoProvider).Assembly;
            try
            {
                byte[] dll = Diagnostics.InstallInfo.ReadOwnDll(out string how);
                if (dll != null)
                {
                    _build = Core.Session.BuildId.OfBytes(dll);
                    _buildSourceDetail = "the DLL file (" + how + ")";
                    _buildSource = "dll";
                    return;
                }
            }
            catch (Exception)
            {
                // fall back to the MVID below
            }
            byte[] mvid = own.ManifestModule.ModuleVersionId.ToByteArray();
            _build = Core.Session.BuildId.FromHash(mvid);
            _buildSourceDetail = "the assembly's MVID (the DLL file could not be found; this id differs from the installer's)";
            _buildSource = "mvid";
        }

        public HandshakeDigests GetDigests() => BuildManifest().Digests;

        public HandshakeManifest BuildManifest()
        {
            RefreshContent();
            var manifest = new HandshakeManifest
            {
                Digests = new HandshakeDigests
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    ModBuild = _modBuild,
                    GameVersion = Game.Version.current.shortVersion,
                    GameFullVersion = Game.Version.current.fullVersion,
                    Locale = GameManager.instance?.localizationManager?.activeLocaleId ?? "",
                },
                Mods = new List<ModEntry>(Mods()),
                Content = new List<ContentEntry>(_content),
                AssetPacks = new List<ContentEntry>(_packs),
                Settings = CurrentSettings(),
            };
            CompatibilityDigests.Fill(manifest);
            return manifest;
        }

        /// <summary>Loaded code mods other than Skyline Together.</summary>
        public IReadOnlyList<ModEntry> Mods()
        {
            var list = new List<ModEntry>();
            string own = typeof(GameInfoProvider).Assembly.GetName().Name;
            ModManager manager = GameManager.instance?.modManager;
            if (manager != null)
            {
                foreach (ModManager.ModInfo info in manager)
                {
                    try
                    {
                        if (!info.isValid || !info.isLoaded || info.asset == null) continue;
                        var name = new AssemblyName(info.asset.fullName);
                        if (string.Equals(name.Name, own, StringComparison.OrdinalIgnoreCase)) continue;
                        var entry = new ModEntry { Name = name.Name, Version = info.asset.version?.ToString() ?? "" };
                        // the old multiplayer mod is also recognised by its Paradox Mods id (only that id is sent: the
                        // other entries keep their name as their identity)
                        if (string.Equals(PlatformIdOf(info), CityDataRule.OldMultiplayerModId, StringComparison.Ordinal)) entry.PlatformId = CityDataRule.OldMultiplayerModId;
                        // mod compatibility: whether the mod can touch shared state (client-local mods never block a join)
                        try
                        {
                            bool registers = Compat.ModAssemblyScanner.RegistersSharedState(info.asset.assembly);
                            entry.RegistersTypesOrSystemsOrPrefabs = registers;
                            entry.ClientLocalHint = !registers;
                        }
                        catch (Exception)
                        {
                            entry.RegistersTypesOrSystemsOrPrefabs = true; // fail closed
                        }
                        // mod compatibility: whether the mod stores data in the city (a joiner without it is always
                        // refused; local to this peer, never sent)
                        try
                        {
                            Compat.CityDataProbe.Fill(entry, info.asset.assembly);
                        }
                        catch (Exception)
                        {
                            entry.StoresCityData = true; // fail closed
                        }
                        list.Add(entry);
                    }
                    catch (Exception)
                    {
                        // a broken entry must not break hosting
                    }
                }
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static string _conflictingMod;

        /// <summary>
        /// The old "CS2 Multiplayer Mod" when it is loaded in this game ("name version"), else null
        /// (<see cref="CityDataRule.IsOldMultiplayerMod"/>, by assembly name or Paradox Mods id). Players cannot join a
        /// host that runs it, and a joiner that runs it is refused; the panel warns while it is loaded. Cached once the
        /// game's mod list exists (it does not change while the game runs).
        /// </summary>
        public static string ConflictingMod()
        {
            if (_conflictingMod != null) return _conflictingMod.Length > 0 ? _conflictingMod : null;
            ModManager manager = GameManager.instance?.modManager;
            if (manager == null) return null;
            string found = "";
            try
            {
                foreach (ModManager.ModInfo info in manager)
                {
                    try
                    {
                        if (!info.isValid || !info.isLoaded || info.asset == null) continue;
                        var entry = new ModEntry { Name = new AssemblyName(info.asset.fullName).Name, Version = info.asset.version?.ToString() ?? "", PlatformId = PlatformIdOf(info) };
                        if (CityDataRule.IsOldMultiplayerMod(entry)) found = (entry.Name + " " + entry.Version).Trim();
                    }
                    catch (Exception)
                    {
                        // a broken entry is no conflicting mod
                    }
                }
            }
            catch (Exception)
            {
                return null; // try again later
            }
            _conflictingMod = found;
            return found.Length > 0 ? found : null;
        }

        // the Paradox Mods id of a loaded mod, or null (never a path)
        private static string PlatformIdOf(ModManager.ModInfo info)
        {
            try
            {
                return info.asset?.GetMeta().platformID;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Re-reads the DLC and asset pack lists when the prefab system's size changed (a mod added prefabs).</summary>
        private void RefreshContent()
        {
            PrefabSystem prefabs;
            try
            {
                prefabs = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<PrefabSystem>();
            }
            catch (Exception)
            {
                prefabs = null;
            }
            if (prefabs == null) return;
            int count = Compat.AssetSources.PrefabCount(prefabs);
            if (count == _cachedPrefabCount && count >= 0) return;
            _cachedPrefabCount = count;
            _content = ContentPacks(prefabs);
            try
            {
                _packs = Compat.AssetSources.Packages(prefabs);
            }
            catch (Exception)
            {
                _packs = new List<ContentEntry>();
            }
        }

        /// <summary>
        /// DLC and content packs whose prefabs are available here (<c>PrefabSystem.GetAvailableContentPrefabs</c>: owned and
        /// installed, referenced by at least one prefab), by DLC id; content that needs a Paradox account login by name.
        /// The content prefabs of Paradox Mods packages are left to the asset pack list.
        /// </summary>
        private static List<ContentEntry> ContentPacks(PrefabSystem prefabs)
        {
            var list = new List<ContentEntry>();
            try
            {
                foreach (ContentPrefab content in prefabs.GetAvailableContentPrefabs())
                {
                    try
                    {
                        if (content == null) continue;
                        if (content.TryGet(out DlcRequirement dlc))
                        {
                            string name = null;
                            try
                            {
                                name = PlatformManager.instance.GetDlcName(dlc.m_Dlc)?.Nicify();
                            }
                            catch (Exception)
                            {
                                name = null;
                            }
                            list.Add(new ContentEntry { Id = dlc.m_Dlc.id.ToString(CultureInfo.InvariantCulture), Name = Clip(string.IsNullOrEmpty(name) ? content.name : name, ManifestDetailMessage.MaxEntryNameBytes) });
                        }
                        else if (content.Has<PdxLoginRequirement>())
                        {
                            list.Add(new ContentEntry { Name = Clip(content.name, ManifestDetailMessage.MaxEntryNameBytes) });
                        }
                    }
                    catch (Exception)
                    {
                        // an entry that cannot be read is left out on both sides alike
                    }
                }
            }
            catch (Exception)
            {
                return new List<ContentEntry>();
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }

        /// <summary>The current values of the pushed mod settings, clipped to the manifest's limits.</summary>
        private static List<SettingEntry> CurrentSettings()
        {
            try
            {
                List<SettingEntry> list = Compat.ModSettingsSync.CurrentValues();
                foreach (SettingEntry s in list)
                {
                    s.Mod = Clip(s.Mod, ManifestDetailMessage.MaxSettingKeyBytes);
                    s.Key = Clip(s.Key, ManifestDetailMessage.MaxSettingKeyBytes);
                    s.Value = Clip(s.Value, ManifestDetailMessage.MaxSettingValueBytes);
                }
                return list;
            }
            catch (Exception)
            {
                return new List<SettingEntry>();
            }
        }

        private static string Clip(string s, int maxBytes) => ProtocolText.Truncate(s ?? "", maxBytes / ProtocolText.MaxUtf8BytesPerChar);
    }
}
