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
    }
}
