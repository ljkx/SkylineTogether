using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The payload of <see cref="ControlKind.ManifestDetail"/>: the <see cref="HandshakeManifest"/> behind the handshake
    /// digests, sent by the client right after its hello and compared by the host after the password
    /// (ARCHITECTURE.md#handshake-and-admission). Every list is <c>varint (count + 1)</c> with 0 = null ("not provided").
    /// <code>
    /// digests (as in Hello)
    /// | list mods       (≤ 4096):  string name (≤ 512 B) | string version (≤ 128 B) | string platformId (≤ 128 B) | string dllHash (≤ 256 B)
    ///                              | u8 flags {bit 0 IsUiModule, bit 1 RegistersTypesOrSystemsOrPrefabs, bit 2 ClientLocalHint}
    /// | list content    (≤ 1024):  entry
    /// | list assetPacks (≤ 16384): entry
    /// | list settings   (≤ 4096):  string mod (≤ 512 B) | string key (≤ 512 B) | string value (≤ 4 KiB) | u8 flags {bit 0 SimulationRelevant, bit 1 Pushable}
    /// entry = string id (≤ 128 B) | string name (≤ 256 B) | string version (≤ 128 B) | u64 digest | varint prefabs
    /// </code>
    /// Identifiers are returned verbatim (they are compared exactly; the UI escapes them). The message arrives before the
    /// joiner is admitted, when the host accepts bodies of at most 1 MiB (<c>HostOptions.UnadmittedMaxBodyBytes</c>), so
    /// the writer keeps it within <see cref="MaxEncodedBytes"/>: when the whole payload is larger it sends the asset
    /// pack list as null, then the settings list too (the host then reports the category as not comparable rather than
    /// a misleading partial difference). A payload still above the limit is refused with
    /// <see cref="ArgumentException"/> (nothing is left in the writer then). The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ManifestDetailMessage
    {
        /// <summary>Largest encoded payload: below the host's 1 MiB limit for messages before admission, with room for the envelope.</summary>
        public const int MaxEncodedBytes = 1024 * 1024 - 4 * 1024;

        public const int MaxMods = 4096;
        public const int MaxContent = 1024;
        public const int MaxAssetPacks = 16384;
        public const int MaxSettings = 4096;
        public const int MaxModNameBytes = 512;
        public const int MaxVersionBytes = 128;
        public const int MaxPlatformIdBytes = 128;
        public const int MaxDllHashBytes = 256;
        public const int MaxEntryIdBytes = 128;
        public const int MaxEntryNameBytes = 256;
        public const int MaxEntryVersionBytes = 128;
        public const int MaxSettingKeyBytes = 512;
        public const int MaxSettingValueBytes = 4 * 1024;

        private const byte ModUiModule = 1;
        private const byte ModRegisters = 2;
        private const byte ModClientLocal = 4;
        private const byte SettingSimulationRelevant = 1;
        private const byte SettingPushable = 2;

        public HandshakeManifest Manifest;

        public static void Write(ByteWriter w, ManifestDetailMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            HandshakeManifest manifest = m.Manifest ?? throw new ArgumentException("Manifest is null", nameof(m));
            int start = w.Length;
            try
            {
                List<ContentEntry> packs = manifest.AssetPacks;
                List<SettingEntry> settings = manifest.Settings;
                WriteManifest(w, manifest, packs, settings);
                if (w.Length - start <= MaxEncodedBytes) return;
                if (packs != null)
                {
                    w.Truncate(start);
                    packs = null;
                    WriteManifest(w, manifest, null, settings);
                    if (w.Length - start <= MaxEncodedBytes) return;
                }
                if (settings != null)
                {
                    w.Truncate(start);
                    WriteManifest(w, manifest, packs, null);
                    if (w.Length - start <= MaxEncodedBytes) return;
                }
            }
            catch (ArgumentException)
            {
                w.Truncate(start);
                throw;
            }
            int size = w.Length - start;
            w.Truncate(start);
            throw new ArgumentException("the manifest needs " + size.ToString(CultureInfo.InvariantCulture)
                                        + " bytes without its asset pack and setting lists, more than the limit of "
                                        + MaxEncodedBytes.ToString(CultureInfo.InvariantCulture), nameof(m));
        }

        private static void WriteManifest(ByteWriter w, HandshakeManifest manifest, List<ContentEntry> packs, List<SettingEntry> settings)
        {
            HandshakeCodec.WriteDigests(w, manifest.Digests);

            List<ModEntry> mods = manifest.Mods;
            MessageFields.WriteNullableCount(w, mods?.Count ?? 0, mods == null, MaxMods, "Mods");
            if (mods != null)
            {
                foreach (ModEntry mod in mods)
                {
                    if (mod == null) throw new ArgumentException("Mods contains null", nameof(manifest));
                    MessageFields.WriteString(w, mod.Name, MaxModNameBytes, "ModEntry.Name");
                    MessageFields.WriteString(w, mod.Version, MaxVersionBytes, "ModEntry.Version");
                    MessageFields.WriteString(w, mod.PlatformId, MaxPlatformIdBytes, "ModEntry.PlatformId");
                    MessageFields.WriteString(w, mod.DllHash, MaxDllHashBytes, "ModEntry.DllHash");
                    w.WriteByte((byte)((mod.IsUiModule ? ModUiModule : 0) | (mod.RegistersTypesOrSystemsOrPrefabs ? ModRegisters : 0) | (mod.ClientLocalHint ? ModClientLocal : 0)));
                }
            }

            WriteEntries(w, manifest.Content, MaxContent, "Content");
            WriteEntries(w, packs, MaxAssetPacks, "AssetPacks");

            MessageFields.WriteNullableCount(w, settings?.Count ?? 0, settings == null, MaxSettings, "Settings");
            if (settings != null)
            {
                foreach (SettingEntry setting in settings)
                {
                    if (setting == null) throw new ArgumentException("Settings contains null", nameof(manifest));
                    MessageFields.WriteString(w, setting.Mod, MaxSettingKeyBytes, "SettingEntry.Mod");
                    MessageFields.WriteString(w, setting.Key, MaxSettingKeyBytes, "SettingEntry.Key");
                    MessageFields.WriteString(w, setting.Value, MaxSettingValueBytes, "SettingEntry.Value");
                    w.WriteByte((byte)((setting.SimulationRelevant ? SettingSimulationRelevant : 0) | (setting.Pushable ? SettingPushable : 0)));
                }
            }
        }

        private static void WriteEntries(ByteWriter w, List<ContentEntry> entries, int max, string field)
        {
            MessageFields.WriteNullableCount(w, entries?.Count ?? 0, entries == null, max, field);
            if (entries == null) return;
            foreach (ContentEntry e in entries)
            {
                if (e == null) throw new ArgumentException(field + " contains null", nameof(entries));
                if (e.Prefabs < 0) throw new ArgumentException(field + ": negative prefab count", nameof(entries));
                MessageFields.WriteString(w, e.Id, MaxEntryIdBytes, field + ".Id");
                MessageFields.WriteString(w, e.Name, MaxEntryNameBytes, field + ".Name");
                MessageFields.WriteString(w, e.Version, MaxEntryVersionBytes, field + ".Version");
                w.WriteUInt64(e.Digest);
                w.WriteVarUInt32((uint)e.Prefabs);
            }
        }

        public static ManifestDetailMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            var manifest = new HandshakeManifest { Digests = HandshakeCodec.ReadDigests(r) };

            int modCount = MessageFields.ReadNullableCount(r, MaxMods, 5, "Manifest.Mods");
            manifest.Mods = modCount < 0 ? null : new List<ModEntry>(modCount);
            for (int i = 0; i < modCount; i++)
            {
                var mod = new ModEntry
                {
                    Name = MessageFields.ReadString(r, MaxModNameBytes, "ModEntry.Name"),
                    Version = MessageFields.ReadString(r, MaxVersionBytes, "ModEntry.Version"),
                    PlatformId = MessageFields.ReadString(r, MaxPlatformIdBytes, "ModEntry.PlatformId"),
                    DllHash = MessageFields.ReadString(r, MaxDllHashBytes, "ModEntry.DllHash")
                };
                byte flags = MessageFields.ReadFlags(r, ModUiModule | ModRegisters | ModClientLocal, "ModEntry flags");
                mod.IsUiModule = (flags & ModUiModule) != 0;
                mod.RegistersTypesOrSystemsOrPrefabs = (flags & ModRegisters) != 0;
                mod.ClientLocalHint = (flags & ModClientLocal) != 0;
                manifest.Mods.Add(mod);
            }

            manifest.Content = ReadEntries(r, MaxContent, "Manifest.Content");
            manifest.AssetPacks = ReadEntries(r, MaxAssetPacks, "Manifest.AssetPacks");

            int settingCount = MessageFields.ReadNullableCount(r, MaxSettings, 4, "Manifest.Settings");
            manifest.Settings = settingCount < 0 ? null : new List<SettingEntry>(settingCount);
            for (int i = 0; i < settingCount; i++)
            {
                var setting = new SettingEntry
                {
                    Mod = MessageFields.ReadString(r, MaxSettingKeyBytes, "SettingEntry.Mod"),
                    Key = MessageFields.ReadString(r, MaxSettingKeyBytes, "SettingEntry.Key"),
                    Value = MessageFields.ReadString(r, MaxSettingValueBytes, "SettingEntry.Value")
                };
                byte flags = MessageFields.ReadFlags(r, SettingSimulationRelevant | SettingPushable, "SettingEntry flags");
                setting.SimulationRelevant = (flags & SettingSimulationRelevant) != 0;
                setting.Pushable = (flags & SettingPushable) != 0;
                manifest.Settings.Add(setting);
            }
            return new ManifestDetailMessage { Manifest = manifest };
        }

        // an entry is at least 3 empty strings + 8 digest bytes + 1 count byte
        private static List<ContentEntry> ReadEntries(ByteReader r, int max, string field)
        {
            int count = MessageFields.ReadNullableCount(r, max, 12, field);
            if (count < 0) return null;
            var list = new List<ContentEntry>(count);
            for (int i = 0; i < count; i++)
            {
                var e = new ContentEntry
                {
                    Id = MessageFields.ReadString(r, MaxEntryIdBytes, field + ".Id"),
                    Name = MessageFields.ReadString(r, MaxEntryNameBytes, field + ".Name"),
                    Version = MessageFields.ReadString(r, MaxEntryVersionBytes, field + ".Version"),
                    Digest = r.ReadUInt64()
                };
                uint prefabs = r.ReadVarUInt32();
                if (prefabs > int.MaxValue) throw new DecodeException(field + ": prefab count " + prefabs.ToString(CultureInfo.InvariantCulture) + " exceeds int.MaxValue");
                e.Prefabs = (int)prefabs;
                list.Add(e);
            }
            return list;
        }

        /// <summary>Reads a whole payload: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static ManifestDetailMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            ManifestDetailMessage m = Read(r);
            MessageFields.EnsureEnd(r, "ManifestDetail");
            return m;
        }
    }
}
