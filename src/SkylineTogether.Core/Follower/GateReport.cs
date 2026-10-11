using System.Collections.Generic;
using System.Text;

namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// The checks of a gate plan (ARCH §6.3, self-test item (b) and (h) of §13.4). Every list holds full type names or
    /// one-line messages in ordinal order, so two reports of the same input are identical.
    /// </summary>
    public sealed class GateReport
    {
        /// <summary>Every name of the gate table, the adapter entries, the tool-scoped entries (systems and tools) and the
        /// keep list that does not resolve to a system; includes the critical ones.</summary>
        public IReadOnlyList<string> Unresolved;

        /// <summary>The critical entries among <see cref="Unresolved"/>: the client refuses to join (ARCH §6.3).</summary>
        public IReadOnlyList<string> CriticalUnresolved;

        /// <summary>
        /// One message per broken producer/consumer rule (ARCH §6.3, R11 §4): a consumer that the plan turns off while
        /// one of its unguarded producers keeps running, or a system that must stay on (<c>IconCommandSystem</c>) but is
        /// gated or currently disabled.
        /// </summary>
        public IReadOnlyList<string> ProducerConsumerViolations;

        /// <summary>Game-assembly systems of the world-writing phases that are neither kept nor gated (maintenance
        /// information after a patch, not an error; ARCH §6.3).</summary>
        public IReadOnlyList<string> Unclassified;

        /// <summary>
        /// Overlapping classifications and ignored entries, each resolved by the documented precedence of
        /// <see cref="GatePlanner"/> (informational; they do not affect <see cref="Ok"/>).
        /// </summary>
        public IReadOnlyList<string> Conflicts;

        /// <summary>True when every critical entry resolved and no producer/consumer rule is violated. Unresolved
        /// optional entries, unclassified systems and conflicts are warnings.</summary>
        public bool Ok => Count(CriticalUnresolved) == 0 && Count(ProducerConsumerViolations) == 0;

        /// <summary>A multi-line description for the log and the diagnostics dump.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(ToString());
            AppendList(sb, "critical unresolved", CriticalUnresolved);
            AppendList(sb, "producer/consumer violations", ProducerConsumerViolations);
            AppendList(sb, "unresolved", Unresolved);
            AppendList(sb, "conflicts", Conflicts);
            AppendList(sb, "unclassified", Unclassified);
            return sb.ToString();
        }

        public override string ToString()
        {
            return "gate report: " + (Ok ? "ok" : "FAILED") + ", " + Count(CriticalUnresolved) + " critical unresolved, "
                   + Count(Unresolved) + " unresolved, " + Count(ProducerConsumerViolations) + " producer/consumer violation(s), "
                   + Count(Conflicts) + " conflict(s), " + Count(Unclassified) + " unclassified";
        }

        private static int Count(IReadOnlyList<string> list) => list == null ? 0 : list.Count;

        private static void AppendList(StringBuilder sb, string title, IReadOnlyList<string> list)
        {
            if (list == null || list.Count == 0) return;
            sb.Append('\n').Append(title).Append(" (").Append(list.Count).Append("):");
            foreach (string item in list) sb.Append("\n  ").Append(item);
        }
    }
}
