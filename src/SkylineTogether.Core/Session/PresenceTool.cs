using System;
using System.Globalization;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The tool a player has active, as carried in <see cref="Protocol.PresenceMessage.ToolKind"/> (low 7 bits; bit 7
    /// is <see cref="PresenceToolKind.CursorHidden"/>). Values travel on the wire and never change.
    /// </summary>
    public enum PresenceTool : byte
    {
        None = 0,
        /// <summary>The default tool (selecting, inspecting).</summary>
        Select,
        Net,
        Object,
        Zone,
        Area,
        Terrain,
        Bulldoze,
        Route,
        Upgrade,
        Water,
        /// <summary>The area selection tool.</summary>
        Selection,
        Other
    }

    /// <summary>Packing of <see cref="PresenceTool"/> and the cursor visibility into the presence tool byte.</summary>
    public static class PresenceToolKind
    {
        /// <summary>Set when the player's cursor is not over the world (over the UI, or no raycast hit).</summary>
        public const byte CursorHidden = 0x80;

        private const byte ToolMask = 0x7F;

        public static byte Pack(PresenceTool tool, bool cursorVisible) => (byte)(((byte)tool & ToolMask) | (cursorVisible ? 0 : CursorHidden));

        public static PresenceTool Tool(byte kind)
        {
            int t = kind & ToolMask;
            return t <= (int)PresenceTool.Other ? (PresenceTool)t : PresenceTool.Other;
        }

        public static bool CursorVisible(byte kind) => (kind & CursorHidden) == 0;

        /// <summary>A short English verb phrase for the panel ("building", "zoning", ...); null for no tool.</summary>
        public static string Verb(PresenceTool tool)
        {
            switch (tool)
            {
                case PresenceTool.Net: return "building";
                case PresenceTool.Object: return "placing";
                case PresenceTool.Zone: return "zoning";
                case PresenceTool.Area: return "drawing";
                case PresenceTool.Terrain: return "shaping terrain";
                case PresenceTool.Bulldoze: return "bulldozing";
                case PresenceTool.Route: return "editing a line";
                case PresenceTool.Upgrade: return "upgrading";
                case PresenceTool.Water: return "placing water";
                case PresenceTool.Selection: return "selecting an area";
                case PresenceTool.Other: return "using a tool";
                default: return null;
            }
        }

        /// <summary>
        /// The tool elevation as the name tag shows it: "+10 m", "-8 m", "+2.5 m", "+1.25 m" (at most two decimals);
        /// null below 1/32 m (on the ground) or for a value that is not finite.
        /// </summary>
        public static string ElevationLabel(float metres)
        {
            if (ElevationKey(metres) == 0) return null;
            double rounded = Math.Round(metres, 2, MidpointRounding.AwayFromZero);
            return (rounded > 0 ? "+" : "") + rounded.ToString("0.##", CultureInfo.InvariantCulture) + " m";
        }

        /// <summary>The tool elevation in 1/16 m steps (what the wire carries), 0 for none: a cache key for the label.</summary>
        public static int ElevationKey(float metres)
        {
            if (float.IsNaN(metres) || float.IsInfinity(metres)) return 0;
            return (int)Math.Round(Math.Max(-1e6, Math.Min(1e6, metres * 16.0)));
        }
    }
}
