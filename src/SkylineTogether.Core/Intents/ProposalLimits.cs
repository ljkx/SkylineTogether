using System;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// The per-part and per-proposal caps the codec and the assembler enforce. Production code always uses
    /// <see cref="Default"/> (the constants of <see cref="Limits"/> and <see cref="ProposalFormat"/>); tests construct
    /// smaller limits to exercise splitting and violations without building 64 MB proposals.
    /// </summary>
    internal sealed class ProposalLimits
    {
        internal static readonly ProposalLimits Default = new ProposalLimits(
            Limits.MaxProposalBytes, Limits.MaxProposalTotalBytes, Limits.MaxProposalEntities, ProposalFormat.MaxTouches,
            ProposalFormat.MaxStamps, ProposalFormat.MaxIntents, ProposalFormat.MaxComponentsTotal, ProposalFormat.MaxZoneCellsTotal);

        internal ProposalLimits(int maxPartBytes, long maxTotalBytes, int maxEntities, int maxTouches, int maxStamps, int maxIntents,
                                int maxComponentsTotal, int maxZoneCellsTotal)
        {
            if (maxPartBytes < 64) throw new ArgumentOutOfRangeException(nameof(maxPartBytes));
            if (maxTotalBytes < maxPartBytes) throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));
            if (maxEntities < 0 || maxTouches < 0 || maxStamps < 0 || maxIntents < 0 || maxComponentsTotal < 0 || maxZoneCellsTotal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntities), "item caps must not be negative");
            }
            MaxPartBytes = maxPartBytes;
            MaxTotalBytes = maxTotalBytes;
            MaxEntities = maxEntities;
            MaxTouches = maxTouches;
            MaxStamps = maxStamps;
            MaxIntents = maxIntents;
            MaxComponentsTotal = maxComponentsTotal;
            MaxZoneCellsTotal = maxZoneCellsTotal;
        }

        /// <summary>Bytes of one part (one message body).</summary>
        internal int MaxPartBytes { get; }

        /// <summary>Bytes of all parts of one proposal.</summary>
        internal long MaxTotalBytes { get; }

        /// <summary>Creates + updates + deletes + zone edits of one proposal (<see cref="Proposal.EntityCount"/>).</summary>
        internal int MaxEntities { get; }

        internal int MaxTouches { get; }

        internal int MaxStamps { get; }

        internal int MaxIntents { get; }

        internal int MaxComponentsTotal { get; }

        internal int MaxZoneCellsTotal { get; }
    }
}
