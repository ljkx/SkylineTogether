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
        private readonly ulong _modBuild;

        public GameInfoProvider()
        {
            byte[] mvid = typeof(GameInfoProvider).Assembly.ManifestModule.ModuleVersionId.ToByteArray();
            _modBuild = BitConverter.ToUInt64(mvid, 0) ^ BitConverter.ToUInt64(mvid, 8);
        }

        public ulong ModBuild => _modBuild;

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
                        list.Add(new ModEntry { Name = name.Name, Version = info.asset.version?.ToString() ?? "" });
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
