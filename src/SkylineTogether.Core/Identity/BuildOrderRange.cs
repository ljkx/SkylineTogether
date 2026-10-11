using System.Globalization;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// One <c>Game.Net.BuildOrder</c> value (<c>m_Start</c>, <c>m_End</c>; <c>Game.Net/BuildOrder.cs</c>). An inverted
    /// edge has <c>End &lt; Start</c>; its width is <c>|End − Start|</c> (the convention of
    /// <c>ResetBuildOrderSystem</c>, <c>Game.Serialization/ResetBuildOrderSystem.cs:62-148</c>).
    /// </summary>
    public struct BuildOrderRange
    {
        public uint Start;
        public uint End;

        public BuildOrderRange(uint start, uint end)
        {
            Start = start;
            End = end;
        }

        /// <summary>The smaller bound.</summary>
        public uint Min => Start <= End ? Start : End;

        /// <summary>The larger bound.</summary>
        public uint Max => Start <= End ? End : Start;

        /// <summary><c>Max − Min</c>.</summary>
        public uint Width => Max - Min;

        /// <summary>True for an inverted range (<c>End &lt; Start</c>).</summary>
        public bool IsInverted => End < Start;

        public override string ToString()
        {
            return "[" + Start.ToString(CultureInfo.InvariantCulture) + ", " + End.ToString(CultureInfo.InvariantCulture) + "]";
        }
    }
}
