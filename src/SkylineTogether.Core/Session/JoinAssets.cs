using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Session
{
    /// <summary>What a bundled join asset is (the client loads prefabs into its prefab system).</summary>
    public enum JoinAssetKind : byte
    {
        Other = 0,
        Prefab = 1,
        Texture = 2,
    }

    /// <summary>One asset the host stored for the joiner (R12 §4.3 gap): its asset type, name, guid and file bytes.</summary>
    public sealed class JoinAssetEntry
    {
        public JoinAssetKind Kind;
        /// <summary>Full name of the asset data type (e.g. <c>Colossal.IO.AssetDatabase.PrefabAsset</c>).</summary>
        public string TypeName;
        public string Name;
        /// <summary>The asset guid as text (32 hex digits); the save refers to the prefab by it.</summary>
        public string Guid;
        public byte[] Data;
    }

    /// <summary>
    /// The container of package-bundled prefabs in front of the join snapshot (R12 §4.3 "bundled render/climate
    /// prefabs"). A normal save stores the city's climate, water-render and terrain-render prefabs into its package when
    /// they came from the map's or the save's package (<c>PrefabSystem.SavePrefabAssets</c>); the bare snapshot did not,
    /// so a joiner without that package got <c>[Missing]</c> prefabs. The host now writes these assets (and their
    /// textures) here and the joiner adds them before it loads the city.
    /// <para>Layout: <c>"STJA" | u8 version | u32 bodyLength</c>, then the body <c>varint count</c> and per entry
    /// <c>u8 kind | string type | string name | string guid | varint length | bytes</c>. Pure.</para>
    /// </summary>
    public static class JoinAssetBundle
    {
        public const byte Version = 1;
        public const int HeaderSize = 9;
        public const int MaxEntries = 64;
        public const int MaxBodyBytes = 256 * 1024 * 1024;
        public const int MaxStringBytes = 1024;

        private static readonly byte[] Magic = { (byte)'S', (byte)'T', (byte)'J', (byte)'A' };

        /// <summary>Longest asset name a joiner accepts from a host.</summary>
        public const int MaxNameChars = 128;

        /// <summary>
        /// The asset data types a joiner adds from a host's bundle: the render prefabs and their textures
        /// (<c>PrefabSystem.SavePrefabAssets</c>). Anything else, executable or UI module assets above all, is skipped.
        /// </summary>
        public static readonly IReadOnlyList<string> AllowedTypeNames = new[]
        {
            "Colossal.IO.AssetDatabase.PrefabAsset",
            "Colossal.IO.AssetDatabase.TextureAsset",
        };

        /// <summary>True for an asset type a joiner accepts from a host's bundle (<see cref="AllowedTypeNames"/>).</summary>
        public static bool IsAllowedType(string typeName)
        {
            foreach (string t in AllowedTypeNames)
            {
                if (string.Equals(t, typeName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// True for an asset name a joiner may hand to its asset database: a plain file name (no path separator, drive
        /// colon, wildcard, quote, angle bracket, pipe or control character), no <c>..</c>, no device name, not starting
        /// or ending with a dot or space, at most <see cref="MaxNameChars"/> characters. A hostile host's bundle must not be
        /// able to write outside the joiner's temporary folder.
        /// </summary>
        public static bool IsSafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameChars) return false;
            if (name.Contains("..")) return false;
            char first = name[0], last = name[name.Length - 1];
            if (first == '.' || char.IsWhiteSpace(first) || last == '.' || char.IsWhiteSpace(last)) return false;
            foreach (char c in name)
            {
                if (c < 0x20 || c == 0x7F || (c >= 0x80 && c < 0xA0)) return false;
                switch (c)
                {
                    case '/': case '\\': case ':': case '*': case '?': case '"': case '<': case '>': case '|':
                    case '\u2028': case '\u2029': case '\u202E': case '\uFEFF':
                        return false;
                }
            }
            string stem = name;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            switch (stem.Trim().ToUpperInvariant())
            {
                case "CON": case "PRN": case "AUX": case "NUL":
                case "COM1": case "COM2": case "COM3": case "COM4": case "COM5": case "COM6": case "COM7": case "COM8": case "COM9":
                case "LPT1": case "LPT2": case "LPT3": case "LPT4": case "LPT5": case "LPT6": case "LPT7": case "LPT8": case "LPT9":
                    return false;
            }
            return true;
        }

        /// <summary>True when <paramref name="path"/> resolves to a location strictly inside <paramref name="folder"/>.</summary>
        public static bool IsInside(string folder, string path)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(path)) return false;
            try
            {
                string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder, path));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>
        /// Why an entry of a host's bundle is not added (null: it may be): an unsafe name, a type other than
        /// <see cref="AllowedTypeNames"/>, or a guid that is not 32 hex digits.
        /// </summary>
        public static string Reject(JoinAssetEntry e)
        {
            if (e == null) return "empty entry";
            if (!IsSafeName(e.Name)) return "unsafe name";
            if (!IsAllowedType(e.TypeName)) return "type not allowed";
            string guid = e.Guid ?? "";
            if (guid.Length != 32) return "invalid guid";
            foreach (char c in guid)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return "invalid guid";
            }
            return null;
        }

        /// <summary>Header and body of a bundle (an empty bundle is a valid 10-byte container).</summary>
        public static byte[] Write(IReadOnlyList<JoinAssetEntry> entries)
        {
            int count = entries?.Count ?? 0;
            if (count > MaxEntries) throw new ArgumentOutOfRangeException(nameof(entries), "more than " + MaxEntries + " join assets");
            var w = new ByteWriter(4096);
            w.WriteBytes(Magic, 0, Magic.Length);
            w.WriteByte(Version);
            int lengthAt = w.Reserve(4);
            w.WriteVarUInt32((uint)count);
            for (int i = 0; i < count; i++)
            {
                JoinAssetEntry e = entries[i] ?? throw new ArgumentException("null join asset", nameof(entries));
                byte[] data = e.Data ?? Array.Empty<byte>();
                w.WriteByte((byte)e.Kind);
                w.WriteString(e.TypeName ?? "", MaxStringBytes);
                w.WriteString(e.Name ?? "", MaxStringBytes);
                w.WriteString(e.Guid ?? "", MaxStringBytes);
                w.WriteVarUInt32((uint)data.Length);
                w.WriteBytes(data, 0, data.Length);
                if (w.Length - HeaderSize > MaxBodyBytes) throw new ArgumentOutOfRangeException(nameof(entries), "join assets above " + MaxBodyBytes + " bytes");
            }
            w.PatchUInt32(lengthAt, (uint)(w.Length - HeaderSize));
            return w.ToArray();
        }

        /// <summary>
        /// Reads a header: true with the body length when <paramref name="header"/> starts with the container's magic
        /// and version; false when it is not a bundle (a host without bundles sends the plain save). Throws on a bundle
        /// with an unknown version or an implausible length.
        /// </summary>
        public static bool TryReadHeader(byte[] header, int offset, out int bodyLength)
        {
            bodyLength = 0;
            if (header == null || header.Length - offset < HeaderSize) return false;
            for (int i = 0; i < Magic.Length; i++)
            {
                if (header[offset + i] != Magic[i]) return false;
            }
            if (header[offset + 4] != Version) throw new DecodeException("join assets: version " + header[offset + 4]);
            uint length = (uint)(header[offset + 5] | header[offset + 6] << 8 | header[offset + 7] << 16 | header[offset + 8] << 24);
            if (length > MaxBodyBytes) throw new DecodeException("join assets: body of " + length + " bytes");
            bodyLength = (int)length;
            return true;
        }

        /// <summary>The entries of a body (exact length, bounded counts and sizes).</summary>
        public static List<JoinAssetEntry> ReadBody(byte[] body, int offset, int count)
        {
            var r = new ByteReader(body, offset, count);
            try
            {
                uint n = r.ReadVarUInt32();
                if (n > MaxEntries) throw new DecodeException("join assets: " + n + " entries");
                var list = new List<JoinAssetEntry>((int)n);
                for (uint i = 0; i < n; i++)
                {
                    var e = new JoinAssetEntry
                    {
                        Kind = (JoinAssetKind)r.ReadByte(),
                        TypeName = r.ReadString(MaxStringBytes),
                        Name = r.ReadString(MaxStringBytes),
                        Guid = r.ReadString(MaxStringBytes),
                    };
                    uint length = r.ReadVarUInt32();
                    if (length > (uint)r.Remaining) throw new DecodeException("join assets: truncated entry");
                    e.Data = new byte[length];
                    r.ReadBytes(e.Data, 0, (int)length);
                    list.Add(e);
                }
                if (r.Remaining != 0) throw new DecodeException("join assets: " + r.Remaining + " trailing bytes");
                return list;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException || ex is EndOfStreamException)
            {
                throw new DecodeException("join assets: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// The joiner's download target. Takes the snapshot blob as it arrives (any chunking) and forwards everything
    /// after a leading <see cref="JoinAssetBundle"/> to the save file; the bundle is kept in memory
    /// (<see cref="Bundle"/>). A blob without the container's magic (a host of an older build) passes through unchanged.
    /// Write-only.
    /// </summary>
    public sealed class JoinBlobSplitter : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _header = new byte[JoinAssetBundle.HeaderSize];
        private int _headerFill;
        private byte[] _body;
        private int _bodyFill;
        private bool _passThrough;
        private long _written;

        public JoinBlobSplitter(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>The bundled assets once the bundle was received completely; null before or without one.</summary>
        public List<JoinAssetEntry> Bundle { get; private set; }

        /// <summary>True once the bundle part is over (or there is none) and the save data flows to the inner stream.</summary>
        public bool InSaveData => _passThrough;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
            while (count > 0 && !_passThrough)
            {
                if (_body == null)
                {
                    int take = Math.Min(count, _header.Length - _headerFill);
                    Buffer.BlockCopy(buffer, offset, _header, _headerFill, take);
                    _headerFill += take;
                    offset += take;
                    count -= take;
                    if (_headerFill < _header.Length) return;
                    if (!JoinAssetBundle.TryReadHeader(_header, 0, out int length))
                    {
                        _passThrough = true; // not a bundle: the header bytes were save data
                        _inner.Write(_header, 0, _header.Length);
                        break;
                    }
                    _body = new byte[length];
                    if (length == 0) Complete();
                    continue;
                }
                int part = Math.Min(count, _body.Length - _bodyFill);
                Buffer.BlockCopy(buffer, offset, _body, _bodyFill, part);
                _bodyFill += part;
                offset += part;
                count -= part;
                if (_bodyFill == _body.Length) Complete();
            }
            if (count > 0) _inner.Write(buffer, offset, count);
        }

        private void Complete()
        {
            Bundle = JoinAssetBundle.ReadBody(_body, 0, _body.Length);
            _body = Array.Empty<byte>();
            _bodyFill = 0;
            _passThrough = true;
        }

        public override void Flush() => _inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // a blob shorter than a header was save data after all
                if (!_passThrough && _body == null && _headerFill > 0) _inner.Write(_header, 0, _headerFill);
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// A render prefab the joiner must resolve (manifest <c>RenderPrefabIds</c>): the <c>PrefabID</c> the snapshot
    /// writes for the city's climate, water-render and terrain-render prefab as <c>type|name|hash</c> (hash empty when
    /// the id has none). Type names and hashes contain no '|', so the name is everything between the first and the last.
    /// </summary>
    public static class RenderPrefabRef
    {
        public static string Encode(string type, string name, string hash) => (type ?? "") + "|" + (name ?? "") + "|" + (hash ?? "");

        public static bool TryDecode(string value, out string type, out string name, out string hash)
        {
            type = name = hash = null;
            if (string.IsNullOrEmpty(value)) return false;
            int first = value.IndexOf('|');
            int last = value.LastIndexOf('|');
            if (first <= 0 || last == first) return false;
            type = value.Substring(0, first);
            name = value.Substring(first + 1, last - first - 1);
            hash = value.Substring(last + 1);
            return name.Length > 0;
        }
    }
}
