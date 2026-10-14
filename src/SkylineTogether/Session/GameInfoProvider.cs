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
        /// SkylineTogether.dll file, the value the installer prints. Only when the file cannot be read (no location)
        /// it falls back to the assembly's MVID, and <see cref="BuildSource"/> says so. Computed once.
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

        /// <summary>The build id as 12 hex digits, e.g. "2f076aad4234" (with " (mvid)" for the fallback).</summary>
        public static string BuildText => Core.Session.BuildId.Format(Build) + (BuildSource == "dll" ? "" : " (" + BuildSource + ")");

        private static void ComputeBuild()
        {
            Assembly own = typeof(GameInfoProvider).Assembly;
            try
            {
                string path = own.Location;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    _build = Core.Session.BuildId.OfBytes(System.IO.File.ReadAllBytes(path));
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
