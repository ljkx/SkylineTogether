using System;
using System.Globalization;
using SkylineTogether.Core.Session;

namespace SkylineTogether.Core.Modding
{
    /// <summary>
    /// The message kinds a mod lets viewers send on its channel (<see cref="DeclarationRole.ViewerMessage"/>; the kind of a
    /// message is its first payload byte). Immutable once built; thread-safe to read.
    /// </summary>
    public sealed class ViewerMessageKinds
    {
        /// <summary>No kind allowed (the default of every mod).</summary>
        public static readonly ViewerMessageKinds None = new ViewerMessageKinds(new bool[256], false);

        private readonly bool[] _kinds;

        private ViewerMessageKinds(bool[] kinds, bool all)
        {
            _kinds = kinds;
            All = all;
        }

        /// <summary>Every message is allowed (<c>viewer-message: *</c>).</summary>
        public bool All { get; }

        /// <summary>True when the mod declared at least one kind.</summary>
        public bool Any => All || Array.IndexOf(_kinds, true) >= 0;

        public bool Allows(byte kind) => All || _kinds[kind];

        /// <summary>True when a viewer may send <paramref name="payload"/> (an empty payload only with <see cref="All"/>).</summary>
        public bool Allows(byte[] payload) => All || (payload != null && payload.Length > 0 && _kinds[payload[0]]);

        /// <summary>The kinds the <c>viewer-message</c> declarations of a mod name.</summary>
        public static ViewerMessageKinds From(ModDeclarations declarations)
        {
            if (declarations == null || !declarations.Has(DeclarationRole.ViewerMessage)) return None;
            var kinds = new bool[256];
            bool all = false;
            foreach (ModDeclaration d in declarations.With(DeclarationRole.ViewerMessage))
            {
                if (d.Target == "*") all = true;
                else if (byte.TryParse(d.Target, NumberStyles.None, CultureInfo.InvariantCulture, out byte kind)) kinds[kind] = true;
            }
            return new ViewerMessageKinds(kinds, all);
        }

        /// <summary>
        /// Parses a <c>viewer-message</c> target: <c>*</c>, or a kind 0 ... 255 in decimal or <c>0x</c> hex. The canonical
        /// text (<c>*</c> or decimal) comes back in <paramref name="canonical"/>.
        /// </summary>
        public static bool TryParseKind(string text, out string canonical)
        {
            canonical = null;
            text = (text ?? "").Trim();
            if (text == "*")
            {
                canonical = "*";
                return true;
            }
            int value;
            bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
                : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            if (!ok || value < 0 || value > 255) return false;
            canonical = value.ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }

    /// <summary>
    /// Who may send on the mod channel (docs/MOD-INTEGRATION.md): every role that may use mod tools
    /// (<see cref="Permission.ModTools"/>: host, co-host, builder); a viewer only the message kinds the mod declared
    /// viewer-allowed. Everyone receives. The sending client refuses locally and the host drops what a client may not send,
    /// judged by the host's own declarations of the mod. Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ModChannelPermissions
    {
        public static bool MaySend(SessionRole role, byte[] payload, ViewerMessageKinds viewerKinds)
        {
            if (RolePermissions.Allows(role, Permission.ModTools)) return true;
            return viewerKinds != null && viewerKinds.Allows(payload);
        }
    }
}
