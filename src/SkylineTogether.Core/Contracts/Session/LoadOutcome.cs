using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>Result of a client snapshot load and its post-load checks (ARCHITECTURE.md#snapshot-join).</summary>
    public sealed class LoadOutcome
    {
        public bool Success;
        /// <summary>Locale key of the failure message (e.g. missing content).</summary>
        public string FailureKey;
        public string FailureDetail;
        /// <summary>Instances of placeholder prefabs (must be 0).</summary>
        public int Placeholders;
        /// <summary>Fraction of tracked entities bound by the <c>NetIdSystem</c> block (must be at least 0.999).</summary>
        public float SlotCoverage;
        /// <summary>Types whose load-parity census differs from the host's (diagnostics mode).</summary>
        public IReadOnlyList<string> CensusDifferences;
    }
}
