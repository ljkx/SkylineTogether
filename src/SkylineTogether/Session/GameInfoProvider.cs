using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Modding;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Session
{
    /// <summary>
    /// This game's handshake data: protocol version, Skyline Together build, game version and the list of loaded code
    /// mods (name and version), which the host compares to refuse joiners whose cities would not load the same.
    /// </summary>
    public sealed class GameInfoProvider : IGameInfoProvider
    {
        private static ulong _build;
        private static string _buildSource;

        private readonly ulong _modBuild;

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

        public HandshakeDigests GetDigests()
        {
            return new HandshakeDigests
            {
                ProtocolVersion = ProtocolVersion.Current,
                ModBuild = _modBuild,
                GameVersion = Game.Version.current.shortVersion,
                GameFullVersion = Game.Version.current.fullVersion,
                Locale = GameManager.instance?.localizationManager?.activeLocaleId ?? "",
            };
        }

        public HandshakeManifest BuildManifest(bool includePrefabList)
        {
            return new HandshakeManifest { Digests = GetDigests(), Mods = new List<ModEntry>(Mods()) };
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
    }
}
