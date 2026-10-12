using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Session
{
    /// <summary>A remote player's marker as the UI shows it: name tag position (0..1 of the screen) and activity.</summary>
    public sealed class PresenceTag
    {
        public byte Peer;
        public string Name;
        /// <summary>CSS colour, e.g. <c>#3fb6e8</c>.</summary>
        public string Color;
        /// <summary>Screen position of the tag anchor, 0..1 from the left and from the top.</summary>
        public float ScreenX, ScreenY;
        /// <summary>The cursor is over the world and in front of the camera.</summary>
        public bool OnScreen;
        /// <summary>"building Small Road", or empty while the player only looks around.</summary>
        public string Activity;
    }

    /// <summary>
    /// polish: decides whether a frame's tags differ enough from the ones the UI last got to be pushed again. The tags are
    /// projected every frame (the camera and the smoothed cursors move), but the UI's name tag layer and the player
    /// list re-render on every push, so a push happens only when a tag appears, disappears or changes its text, colour
    /// or visibility, or when a visible tag moved more than <see cref="MoveThreshold"/> (a fraction of the screen,
    /// about 3 px on a 1920 px wide screen) from where the UI shows it. Pure.
    /// </summary>
    public static class PresenceTagDiff
    {
        public const float MoveThreshold = 0.0015f;

        /// <summary>True when <paramref name="next"/> should replace <paramref name="shown"/> in the UI.</summary>
        public static bool Differs(IReadOnlyList<PresenceTag> shown, IReadOnlyList<PresenceTag> next, float threshold = MoveThreshold)
        {
            int a = shown?.Count ?? 0, b = next?.Count ?? 0;
            if (a != b) return true;
            for (int i = 0; i < b; i++)
            {
                PresenceTag x = shown[i], y = next[i];
                if (ReferenceEquals(x, y)) continue;
                if (x == null || y == null) return true;
                if (x.Peer != y.Peer || x.OnScreen != y.OnScreen) return true;
                if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal) || !string.Equals(x.Color, y.Color, StringComparison.Ordinal)
                    || !string.Equals(x.Activity ?? "", y.Activity ?? "", StringComparison.Ordinal))
                {
                    return true;
                }
                if (!y.OnScreen) continue; // a hidden tag's position is not shown
                if (Math.Abs(x.ScreenX - y.ScreenX) > threshold || Math.Abs(x.ScreenY - y.ScreenY) > threshold) return true;
            }
            return false;
        }
    }
}
