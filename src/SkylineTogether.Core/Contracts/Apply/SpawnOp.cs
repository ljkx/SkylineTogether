namespace SkylineTogether.Core
{
    /// <summary>
    /// A SPAWN in a plan: its components are <see cref="ApplyPlan.Components"/>[<see cref="FirstComponent"/> ...
    /// <see cref="FirstComponent"/> + <c>Header.ComponentCount</c> - 1].
    /// </summary>
    public struct SpawnOp
    {
        public SpawnHeader Header;
        public int FirstComponent;
        /// <summary>Enabled bits by ordinal in the record's type set (ordinals are below 64).</summary>
        public ulong EnabledBits;
    }
}
