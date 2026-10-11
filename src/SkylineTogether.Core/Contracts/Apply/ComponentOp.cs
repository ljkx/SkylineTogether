namespace SkylineTogether.Core
{
    /// <summary>One component payload of a plan (spawn and struct payloads in <see cref="ApplyPlan.Components"/>, values).</summary>
    public struct ComponentOp
    {
        public TypeRank Type;
        /// <summary>Canonical bytes in the plan's <see cref="ApplyPlan.Arena"/>; empty for presence-only components.</summary>
        public PayloadRef Payload;
        public bool HasEnabledBit;
        public bool Enabled;
        public bool PresenceOnly;
    }
}
