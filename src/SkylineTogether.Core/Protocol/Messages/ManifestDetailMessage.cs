using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The payload of <see cref="ControlKind.ManifestDetail"/>: the full <see cref="HandshakeManifest"/> behind the
    /// handshake digests, exchanged for mismatching categories (ARCH §7.9). Every list is <c>varint (count + 1)</c>
    /// with 0 = null, so "not provided" (for example <see cref="HandshakeManifest.PrefabIds"/>, filled only on a prefab
    /// mismatch) survives the round trip.
    /// <code>
    /// digests (as in Hello)
    /// | list mods      (≤ 4096):   string name (≤ 512 B) | string version (≤ 128 B) | string platformId (≤ 128 B) | string dllHash (≤ 256 B)
    ///                              | u8 flags {bit 0 IsUiModule, bit 1 RegistersTypesOrSystemsOrPrefabs}
    /// | list content   (≤ 16384):  string (≤ 512 B)
    /// | list formatTagNames (≤ 4096): string (≤ 256 B)
    /// | list prefabIds (≤ 262144): string (≤ 1024 B)
    /// | list settings  (≤ 65536):  string mod (≤ 512 B) | string key (≤ 512 B) | string value (≤ 16 KiB) | u8 flags {bit 0 SimulationRelevant, bit 1 Pushable}
    /// | list dataFiles (≤ 256):    string file (≤ 256 B) | u64 digest | u8 flags {bit 0 Overridden}
    /// </code>
    /// Identifiers are returned verbatim (they are compared exactly; the UI escapes them). The prefab list is the only
    /// part that is large in practice (one id per prefab): when its encoding would exceed <see cref="MaxPrefabListBytes"/>,
    /// or the whole payload <see cref="MaxEncodedBytes"/>, the writer sends null instead, so the compatibility report says
    /// the list was unavailable rather than listing a misleading partial difference. A payload that is still above
    /// <see cref="MaxEncodedBytes"/> without the prefab list is refused with <see cref="ArgumentException"/> (nothing is
    /// left in the writer then). The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ManifestDetailMessage
    {
        /// <summary>Largest encoded payload: it travels as the payload of one control message.</summary>
        public const int MaxEncodedBytes = ControlMessage.MaxPayloadBytes;

        public const int MaxMods = 4096;
        public const int MaxContent = 16384;
        public const int MaxFormatTags = 4096;
        public const int MaxPrefabIds = 262144;
        public const int MaxSettings = 65536;
        public const int MaxDataFiles = 256;
        public const int MaxModNameBytes = 512;
        public const int MaxVersionBytes = 128;
        public const int MaxPlatformIdBytes = 128;
        public const int MaxDllHashBytes = 256;
        public const int MaxContentBytes = 512;
        public const int MaxFormatTagBytes = 256;
        public const int MaxPrefabIdBytes = 1024;
        public const int MaxSettingKeyBytes = 512;
        public const int MaxSettingValueBytes = 16 * 1024;
        public const int MaxFileNameBytes = 256;

        /// <summary>Encoded size above which the prefab list is sent as null (6 MiB).</summary>
        public const int MaxPrefabListBytes = 6 * 1024 * 1024;

        private const byte ModUiModule = 1;
        private const byte ModRegisters = 2;
        private const byte SettingSimulationRelevant = 1;
        private const byte SettingPushable = 2;
        private const byte DataFileOverridden = 1;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        public HandshakeManifest Manifest;

        public static void Write(ByteWriter w, ManifestDetailMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            HandshakeManifest manifest = m.Manifest ?? throw new ArgumentException("Manifest is null", nameof(m));
            List<string> prefabs = manifest.PrefabIds;
            if (prefabs != null && (prefabs.Count > MaxPrefabIds || EncodedSize(prefabs) > MaxPrefabListBytes)) prefabs = null;

            int start = w.Length;
            try
            {
                WriteManifest(w, manifest, prefabs);
                if (w.Length - start <= MaxEncodedBytes) return;
                if (prefabs != null)
                {
                    w.Truncate(start);
                    WriteManifest(w, manifest, null);
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
                                        + " bytes without its prefab list, more than the control payload limit of "
                                        + MaxEncodedBytes.ToString(CultureInfo.InvariantCulture), nameof(m));
        }

        private static void WriteManifest(ByteWriter w, HandshakeManifest manifest, List<string> prefabs)
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
                    w.WriteByte((byte)((mod.IsUiModule ? ModUiModule : 0) | (mod.RegistersTypesOrSystemsOrPrefabs ? ModRegisters : 0)));
                }
            }

            MessageFields.WriteStringList(w, manifest.Content, MaxContent, MaxContentBytes, "Content");
            MessageFields.WriteStringList(w, manifest.FormatTagNames, MaxFormatTags, MaxFormatTagBytes, "FormatTagNames");
            MessageFields.WriteStringList(w, prefabs, MaxPrefabIds, MaxPrefabIdBytes, "PrefabIds");

            List<SettingEntry> settings = manifest.Settings;
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

            List<DataFileDigestEntry> files = manifest.DataFiles;
            MessageFields.WriteNullableCount(w, files?.Count ?? 0, files == null, MaxDataFiles, "DataFiles");
            if (files != null)
            {
                foreach (DataFileDigestEntry file in files)
                {
                    if (file == null) throw new ArgumentException("DataFiles contains null", nameof(manifest));
                    MessageFields.WriteString(w, file.File, MaxFileNameBytes, "DataFileDigestEntry.File");
                    w.WriteUInt64(file.Digest);
                    w.WriteByte(file.Overridden ? DataFileOverridden : (byte)0);
                }
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
                byte flags = MessageFields.ReadFlags(r, ModUiModule | ModRegisters, "ModEntry flags");
                mod.IsUiModule = (flags & ModUiModule) != 0;
                mod.RegistersTypesOrSystemsOrPrefabs = (flags & ModRegisters) != 0;
                manifest.Mods.Add(mod);
            }

            manifest.Content = MessageFields.ReadStringList(r, MaxContent, MaxContentBytes, "Manifest.Content");
            manifest.FormatTagNames = MessageFields.ReadStringList(r, MaxFormatTags, MaxFormatTagBytes, "Manifest.FormatTagNames");
            manifest.PrefabIds = MessageFields.ReadStringList(r, MaxPrefabIds, MaxPrefabIdBytes, "Manifest.PrefabIds");

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

            int fileCount = MessageFields.ReadNullableCount(r, MaxDataFiles, 10, "Manifest.DataFiles");
            manifest.DataFiles = fileCount < 0 ? null : new List<DataFileDigestEntry>(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                var file = new DataFileDigestEntry
                {
                    File = MessageFields.ReadString(r, MaxFileNameBytes, "DataFileDigestEntry.File"),
                    Digest = r.ReadUInt64()
                };
                file.Overridden = (MessageFields.ReadFlags(r, DataFileOverridden, "DataFileDigestEntry flags") & DataFileOverridden) != 0;
                manifest.DataFiles.Add(file);
            }
            return new ManifestDetailMessage { Manifest = manifest };
        }

        /// <summary>Reads a whole payload: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static ManifestDetailMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            ManifestDetailMessage m = Read(r);
            MessageFields.EnsureEnd(r, "ManifestDetail");
            return m;
        }

        private static long EncodedSize(List<string> list)
        {
            long size = 0;
            foreach (string s in list)
            {
                int bytes = string.IsNullOrEmpty(s) ? 0 : Utf8.GetByteCount(s);
                size += WireFormat.VarUInt32Size((uint)bytes) + bytes;
            }
            return size;
        }
    }
}
