using System.Collections.Generic;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core
{
    /// <summary>
    /// A tool-scoped gate (ARCH §6.3, §4.11.4): <see cref="Systems"/> are off on clients except while the tool
    /// <see cref="ToolTypeName"/> is active and for <see cref="LingerSeconds"/> after it was deactivated or last applied,
    /// and only while the named <see cref="Guard"/> is healthy. JSON (in <c>gates.json</c> and in adapters):
    /// <c>{ "tool": "Game.Tools.RouteToolSystem", "systems": ["Game.Routes.WaypointConnectionSystem", "Game.Routes.RoutePathSystem"], "lingerSeconds": 2, "guard": "RouteGuard" }</c>;
    /// <c>lingerSeconds</c> is optional (default <see cref="DefaultLingerSeconds"/>, range 0-600), <c>guard</c> is optional.
    /// </summary>
    public sealed class ToolScopedGateEntry
    {
        /// <summary>Default linger time (ARCH Appendix D).</summary>
        public const double DefaultLingerSeconds = 2.0;

        public string ToolTypeName;
        public IReadOnlyList<string> Systems;
        public double LingerSeconds;
        /// <summary>The guard that must be healthy for the window to open (null = no guard).</summary>
        public string Guard;

        public override string ToString() => (ToolTypeName ?? "?") + " -> " + (Systems == null ? "?" : string.Join(", ", Systems));

        internal static ToolScopedGateEntry Parse(ConfigReader r, JsonValue node, string path)
        {
            r.RequireObject(node, path);
            r.CheckKeys(node, path, "tool", "systems", "lingerSeconds", "guard");
            string tool = r.GetTypeName(node, path, "tool", true);
            IReadOnlyList<string> systems = r.GetStringList(node, path, "systems", true, true, false);
            foreach (string system in systems)
            {
                if (system == tool) throw r.Error(node["systems"], ConfigReader.Child(path, "systems"), "a tool cannot scope itself");
            }
            double linger = r.GetNumber(node, path, "lingerSeconds", false, DefaultLingerSeconds, 0, 600);
            string guard = r.GetString(node, path, "guard", false);
            if (guard != null) r.CheckIdentifier(node["guard"], ConfigReader.Child(path, "guard"), guard, "._-", 64);
            return new ToolScopedGateEntry { ToolTypeName = tool, Systems = systems, LingerSeconds = linger, Guard = guard };
        }
    }
}
