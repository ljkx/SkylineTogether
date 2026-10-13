using System;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Session
{
    /// <summary>The stages of one join, in order (loadspeed: <see cref="JoinTimeline"/>).</summary>
    public enum JoinStage : byte
    {
        /// <summary>Client: <c>Connect</c> was called. Host: the player was admitted.</summary>
        Start = 0,
        /// <summary>Client: the welcome arrived. Host: the welcome was sent (same moment as <see cref="Start"/>).</summary>
        Welcome,
        /// <summary>Host: the snapshot this player gets was requested (it may predate the admission when it is reused).</summary>
        SnapshotRequested,
        /// <summary>Host: that snapshot's file was complete.</summary>
        SnapshotReady,
        /// <summary>Client: the snapshot manifest arrived. Host: the manifest was sent and the chunks started.</summary>
        TransferStarted,
        /// <summary>Host: every chunk was handed to the transport (it may still be in flight).</summary>
        TransferSent,
        /// <summary>Client: every chunk was stored. Host: the client reported it is loading.</summary>
        Downloaded,
        /// <summary>Client: the digest was verified.</summary>
        Verified,
        /// <summary>Client: the game's load was started.</summary>
        LoadStarted,
        /// <summary>Client: the load finished. Host: the client's "load done" arrived.</summary>
        Loaded,
        /// <summary>The player is live.</summary>
        Live,
        /// <summary>Client: the replication backlog that queued up during the join is applied.</summary>
        CaughtUp,
    }

    /// <summary>
    /// loadspeed: precise stage timings of one join (host per player, and on the client), for the log, the diagnostics
    /// dump and the UI. Times are the session clock's seconds; a stage that was never reached is NaN, and so is every
    /// duration that needs it. The first mark of a stage wins (a later one is ignored unless it overwrites). Pure; main
    /// thread (not thread-safe).
    /// </summary>
    public sealed class JoinTimeline
    {
        private const int StageCount = (int)JoinStage.CaughtUp + 1;

        private readonly double[] _at = new double[StageCount];

        public JoinTimeline()
        {
            for (int i = 0; i < StageCount; i++) _at[i] = double.NaN;
        }

        /// <summary>Who this timeline is about (the player's name on the host, "you" on the client).</summary>
        public string Who = "";

        /// <summary>Size of the snapshot blob in bytes (0 until known).</summary>
        public long SnapshotBytes;

        /// <summary>Host: seconds the game took to write the snapshot (NaN when unknown, e.g. on the client).</summary>
        public double SaveSeconds = double.NaN;

        /// <summary>Host: milliseconds spent computing the blob digest for this player (0 when it was known already).</summary>
        public double DigestMilliseconds = double.NaN;

        /// <summary>Host: the digest came with the snapshot (computed while it was written) or from an earlier player.</summary>
        public bool DigestReused;

        /// <summary>Host: this player got a snapshot taken for an earlier player (nothing changed in between).</summary>
        public bool SnapshotReused;

        /// <summary>Client: replication messages buffered during the join and delivered when it went live.</summary>
        public int BacklogMessages;

        /// <summary>Client: bytes of those messages.</summary>
        public long BacklogBytes;

        /// <summary>Client: replication batches still waiting to be applied after going live (the "catching up" state).</summary>
        public int BacklogRemaining;

        /// <summary>Client: the backlog is being applied (between <see cref="JoinStage.Live"/> and <see cref="JoinStage.CaughtUp"/>).</summary>
        public bool CatchingUp => Has(JoinStage.Live) && !Has(JoinStage.CaughtUp) && BacklogRemaining > 0;

        /// <summary>Records <paramref name="stage"/> at <paramref name="seconds"/>; an earlier mark is kept unless <paramref name="overwrite"/>.</summary>
        public void Mark(JoinStage stage, double seconds, bool overwrite = false)
        {
            int i = (int)stage;
            if (i < 0 || i >= StageCount || double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
            if (!overwrite && !double.IsNaN(_at[i])) return;
            _at[i] = seconds;
        }

        public bool Has(JoinStage stage) => !double.IsNaN(At(stage));

        /// <summary>The time of a stage, NaN when it was not reached.</summary>
        public double At(JoinStage stage)
        {
            int i = (int)stage;
            return i >= 0 && i < StageCount ? _at[i] : double.NaN;
        }

        /// <summary>Seconds from <paramref name="from"/> to <paramref name="to"/>; NaN when either is missing or the order is reversed.</summary>
        public double Between(JoinStage from, JoinStage to)
        {
            double a = At(from), b = At(to);
            if (double.IsNaN(a) || double.IsNaN(b) || b < a) return double.NaN;
            return b - a;
        }

        /// <summary>Connecting and the handshake (client), 0 on the host.</summary>
        public double ConnectSeconds => Between(JoinStage.Start, JoinStage.Welcome);

        /// <summary>Waiting for the snapshot: the save plus any queueing, until the chunks started.</summary>
        public double WaitSeconds => Between(JoinStage.Welcome, JoinStage.TransferStarted);

        /// <summary>The download (client) or the time until the client reported it complete (host).</summary>
        public double TransferSeconds => Between(JoinStage.TransferStarted, JoinStage.Downloaded);

        /// <summary>Average download rate in bytes per second (NaN when unknown).</summary>
        public double TransferBytesPerSecond
        {
            get
            {
                double s = TransferSeconds;
                if (double.IsNaN(s) || SnapshotBytes <= 0) return double.NaN;
                return SnapshotBytes / Math.Max(s, 0.001);
            }
        }

        /// <summary>Client: the digest check after the last chunk.</summary>
        public double VerifySeconds => Between(JoinStage.Downloaded, JoinStage.Verified);

        /// <summary>The game's city load (client: load start to finish; host: download reported to load done).</summary>
        public double LoadSeconds => Has(JoinStage.LoadStarted) ? Between(JoinStage.LoadStarted, JoinStage.Loaded) : Between(JoinStage.Downloaded, JoinStage.Loaded);

        /// <summary>From the end of the load to live (the host's confirmation round trip).</summary>
        public double ConfirmSeconds => Between(JoinStage.Loaded, JoinStage.Live);

        /// <summary>Client: applying the backlog after going live.</summary>
        public double CatchUpSeconds => Between(JoinStage.Live, JoinStage.CaughtUp);

        /// <summary>Time to live: from the start (connect or admission) until live.</summary>
        public double TotalSeconds => Between(JoinStage.Start, JoinStage.Live);

        public JoinTimeline Clone()
        {
            var c = new JoinTimeline
            {
                Who = Who,
                SnapshotBytes = SnapshotBytes,
                SaveSeconds = SaveSeconds,
                DigestMilliseconds = DigestMilliseconds,
                DigestReused = DigestReused,
                SnapshotReused = SnapshotReused,
                BacklogMessages = BacklogMessages,
                BacklogBytes = BacklogBytes,
                BacklogRemaining = BacklogRemaining,
            };
            Array.Copy(_at, c._at, StageCount);
            return c;
        }

        /// <summary>One log line, e.g. "join of Anna: wait 4.20 s (save 3.90 s), transfer 12.3 s for 95.0 MB at 7.7 MB/s, ...".</summary>
        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append("join timings").Append(string.IsNullOrEmpty(Who) ? "" : " of " + Who).Append(':');
            Part(sb, "connect", ConnectSeconds);
            Part(sb, "wait for snapshot", WaitSeconds);
            if (!double.IsNaN(SaveSeconds)) sb.Append(" (save ").Append(SaveSeconds.ToString("0.00", ci)).Append(" s").Append(SnapshotReused ? ", reused" : "").Append(')');
            else if (SnapshotReused) sb.Append(" (reused snapshot)");
            if (!double.IsNaN(DigestMilliseconds)) sb.Append(", digest ").Append(DigestMilliseconds.ToString("0.0", ci)).Append(" ms").Append(DigestReused ? " (reused)" : "");
            Part(sb, "transfer", TransferSeconds);
            if (SnapshotBytes > 0) sb.Append(" for ").Append((SnapshotBytes / 1048576.0).ToString("0.0", ci)).Append(" MiB");
            double rate = TransferBytesPerSecond;
            if (!double.IsNaN(rate)) sb.Append(" at ").Append((rate / 1048576.0).ToString("0.00", ci)).Append(" MiB/s");
            Part(sb, "verify", VerifySeconds);
            Part(sb, "load", LoadSeconds);
            Part(sb, "confirm", ConfirmSeconds);
            Part(sb, "time to live", TotalSeconds);
            if (Has(JoinStage.Live) && (BacklogMessages > 0 || Has(JoinStage.CaughtUp)))
            {
                sb.Append(", backlog ").Append(BacklogMessages.ToString(ci)).Append(" message(s) / ").Append((BacklogBytes / 1024.0).ToString("0.0", ci)).Append(" KiB");
                Part(sb, "catch-up", CatchUpSeconds);
            }
            return sb.ToString();
        }

        private static void Part(StringBuilder sb, string name, double seconds)
        {
            if (double.IsNaN(seconds)) return;
            if (sb[sb.Length - 1] != ':') sb.Append(',');
            sb.Append(' ').Append(name).Append(' ').Append(seconds.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s");
        }

        /// <summary>Milliseconds rounded for the UI; -1 for NaN.</summary>
        public static long Ms(double seconds) => double.IsNaN(seconds) || double.IsInfinity(seconds) ? -1 : (long)Math.Round(seconds * 1000);
    }
}
