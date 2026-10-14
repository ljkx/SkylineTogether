using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Modding
{
    /// <summary>What a mod declares about one of its types, systems, tools or settings (docs/MOD-INTEGRATION.md).</summary>
    public enum DeclarationRole : byte
    {
        /// <summary>Component: must replicate (only valid for a type the game saves).</summary>
        Replicate = 1,
        /// <summary>Component: never sent; the entities carrying it still replicate.</summary>
        NeverReplicate = 2,
        /// <summary>Component: marks tool or UI entities; an entity carrying it never replicates.</summary>
        ToolLocal = 3,
        /// <summary>System: runs on the host only (off on clients for the whole session).</summary>
        HostOnly = 4,
        /// <summary>System: keeps running on clients, even in a simulation phase (it writes no replicated state).</summary>
        Everywhere = 5,
        /// <summary>System: off on a client from the start of its join (a load-time mutator driven by the player's own settings).</summary>
        JoinOff = 6,
        /// <summary>Tool: writes the world directly (the default for every tool of a mod).</summary>
        DirectWrite = 7,
        /// <summary>Tool: changes the world only through the game's Temp/apply pipeline.</summary>
        Pipeline = 8,
        /// <summary>Setting: the host's value applies to everyone in a session.</summary>
        HostSetting = 9,
        /// <summary>Mod: only affects its own player; never blocks a join.</summary>
        ClientLocal = 10,
        /// <summary>Mod: changes the shared world; must be on every peer in the same version.</summary>
        World = 11,
        /// <summary>
        /// Mod channel: viewers may send messages whose first payload byte is this kind (target <c>0</c> ... <c>255</c>), or
        /// any message (target <c>*</c>). Without it a viewer only receives (<see cref="ModChannelPermissions"/>).
        /// </summary>
        ViewerMessage = 12,
    }

    /// <summary>One declaration with the place it came from (for the report).</summary>
    public sealed class ModDeclaration
    {
        public DeclarationRole Role;
        /// <summary>Full type name, <c>SettingsType.Property</c> for a setting, empty for a mod-level role.</summary>
        public string Target;
        /// <summary>Where it was found, e.g. <c>attribute on MyMod.GrowthSystem</c> or <c>Declarations[2]</c>.</summary>
        public string Source;

        public override string ToString() => ModDeclarations.RoleName(Role) + (string.IsNullOrEmpty(Target) ? "" : ": " + Target);
    }

    /// <summary>
    /// The declarations of one mod, gathered from its <c>SkylineTogether</c> attributes and the <c>Declarations</c> lines
    /// of its <c>SkylineTogetherIntegration</c> class (docs/MOD-INTEGRATION.md). Parsing never throws: an unreadable
    /// declaration becomes a <see cref="Problems"/> line and is ignored. Whether a target exists and suits its role is
    /// checked by <see cref="ModClassifier"/> against the scanned facts. Not thread-safe (built once per mod).
    /// </summary>
    public sealed class ModDeclarations
    {
        /// <summary>The integration API version this Skyline Together implements.</summary>
        public const int SupportedApiVersion = 1;

        /// <summary>Declarations read per mod at most (a runaway list is cut, not trusted).</summary>
        public const int MaxDeclarations = 1024;

        /// <summary>Characters of one target name at most.</summary>
        public const int MaxTargetChars = 512;

        private static readonly Dictionary<string, DeclarationRole> Roles = new Dictionary<string, DeclarationRole>(StringComparer.OrdinalIgnoreCase)
        {
            { "replicate", DeclarationRole.Replicate },
            { "never-replicate", DeclarationRole.NeverReplicate },
            { "tool-local", DeclarationRole.ToolLocal },
            { "host-only", DeclarationRole.HostOnly },
            { "everywhere", DeclarationRole.Everywhere },
            { "join-off", DeclarationRole.JoinOff },
            { "direct-write", DeclarationRole.DirectWrite },
            { "pipeline", DeclarationRole.Pipeline },
            { "host-setting", DeclarationRole.HostSetting },
            { "client-local", DeclarationRole.ClientLocal },
            { "world", DeclarationRole.World },
            { "viewer-message", DeclarationRole.ViewerMessage },
        };

        public readonly List<ModDeclaration> Items = new List<ModDeclaration>();
        public readonly List<string> Problems = new List<string>();
        private bool _cut;

        /// <summary>The API version the mod's integration class names (0: no integration class or no version).</summary>
        public int ApiVersion;

        /// <summary>The mod has a <c>SkylineTogetherIntegration</c> class.</summary>
        public bool HasIntegrationClass;

        public bool IsEmpty => Items.Count == 0 && Problems.Count == 0 && !HasIntegrationClass;

        /// <summary>The canonical text of a role (as mods write it).</summary>
        public static string RoleName(DeclarationRole role)
        {
            foreach (KeyValuePair<string, DeclarationRole> pair in Roles)
            {
                if (pair.Value == role) return pair.Key;
            }
            return role.ToString();
        }

        /// <summary>Parses a role; spaces and underscores count as dashes (<c>host_only</c>, <c>Host Only</c>).</summary>
        public static bool TryParseRole(string text, out DeclarationRole role)
        {
            role = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string key = text.Trim().Replace('_', '-').Replace(' ', '-');
            return Roles.TryGetValue(key, out role);
        }

        /// <summary>True for the roles that name the mod itself (no target).</summary>
        public static bool IsModLevel(DeclarationRole role) => role == DeclarationRole.ClientLocal || role == DeclarationRole.World;

        /// <summary>Adds one declaration from its parts (attribute argument and the member it sits on).</summary>
        public void Add(string role, string target, string source)
        {
            if (Items.Count >= MaxDeclarations)
            {
                if (!_cut) Problems.Add("more than " + MaxDeclarations.ToString(CultureInfo.InvariantCulture) + " declarations; the rest is ignored");
                _cut = true;
                return;
            }
            if (!TryParseRole(role, out DeclarationRole parsed))
            {
                Problems.Add(source + ": unknown role \"" + Clip(role) + "\" (known: " + string.Join(", ", Roles.Keys) + ")");
                return;
            }
            target = (target ?? "").Trim();
            if (target.Length > MaxTargetChars)
            {
                Problems.Add(source + ": target name longer than " + MaxTargetChars.ToString(CultureInfo.InvariantCulture) + " characters");
                return;
            }
            if (IsModLevel(parsed))
            {
                target = "";
            }
            else if (parsed == DeclarationRole.ViewerMessage)
            {
                if (!ViewerMessageKinds.TryParseKind(target, out string kind))
                {
                    Problems.Add(source + ": \"" + RoleName(parsed) + "\" needs a message kind: the first payload byte (0 ... 255), or * for every message");
                    return;
                }
                target = kind;
            }
            else if (target.Length == 0)
            {
                Problems.Add(source + ": \"" + RoleName(parsed) + "\" needs a target (a full type name, or SettingsType.Property)");
                return;
            }
            foreach (ModDeclaration d in Items)
            {
                if (d.Role == parsed && string.Equals(d.Target, target, StringComparison.Ordinal)) return; // the same twice
            }
            Items.Add(new ModDeclaration { Role = parsed, Target = target, Source = source });
        }

        /// <summary>
        /// Adds one <c>"role: target"</c> line (or just <c>"role"</c> for a mod-level role) of the integration class's
        /// <c>Declarations</c> array.
        /// </summary>
        public void AddLine(string line, string source)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Problems.Add(source + ": empty declaration");
                return;
            }
            int colon = line.IndexOf(':');
            if (colon < 0) Add(line, "", source);
            else Add(line.Substring(0, colon), line.Substring(colon + 1), source);
        }

        /// <summary>The declarations with a role.</summary>
        public IEnumerable<ModDeclaration> With(DeclarationRole role)
        {
            foreach (ModDeclaration d in Items)
            {
                if (d.Role == role) yield return d;
            }
        }

        public bool Has(DeclarationRole role)
        {
            foreach (ModDeclaration d in Items)
            {
                if (d.Role == role) return true;
            }
            return false;
        }

        private static string Clip(string s)
        {
            s = s ?? "";
            return s.Length > 64 ? s.Substring(0, 64) + "..." : s;
        }
    }
}
