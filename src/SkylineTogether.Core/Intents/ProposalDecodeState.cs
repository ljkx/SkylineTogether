namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// The running totals of one proposal while its parts are decoded (shared by <c>Proposal.Read</c> and
    /// <see cref="ProposalAssembler"/>): the target proposal the items are appended to, the last item tag (tags never
    /// decrease across parts), bytes, components and zone cells so far, and the first cap violation.
    /// </summary>
    internal sealed class ProposalDecodeState
    {
        private ushort[] _zoneScratch;

        internal ProposalDecodeState(ProposalLimits limits, Proposal target)
        {
            Limits = limits;
            Target = target;
        }

        internal ProposalLimits Limits { get; }

        internal Proposal Target { get; }

        internal byte LastTag;

        /// <summary>Bytes of the parts decoded so far (the measure of <see cref="ProposalLimits.MaxTotalBytes"/>).</summary>
        internal long Bytes;

        internal int Components;

        internal int ZoneCells;

        /// <summary>The first per-proposal cap that was exceeded, or null.</summary>
        internal string Violation;

        /// <summary>Decode target of one zone edit, sized for the largest edit the format allows (allocated on first use).</summary>
        internal ushort[] ZoneScratch => _zoneScratch ?? (_zoneScratch = new ushort[ProposalFormat.MaxZoneCellsPerEdit]);

        /// <summary>True when one more entity fits; otherwise records the violation and returns false.</summary>
        internal bool ReserveEntity()
        {
            if (Target.EntityCount < Limits.MaxEntities) return true;
            Violate("the proposal addresses more than " + ProposalCodec.Format(Limits.MaxEntities) + " entities");
            return false;
        }

        internal void Violate(string detail)
        {
            if (Violation == null) Violation = detail;
        }
    }
}
