using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// The host side of the motion channel (ARCHITECTURE.md#movers): per peer it keeps the interest table, the reference frame of
    /// every mover, the frames sampled since the last flush and the motion budget, and it builds that peer's
    /// <c>MotionControl</c> messages (lane R) and per-tick Q-B datagrams (lane U, at most 1200 bytes).
    /// <para>
    /// <b>Flow.</b> The motion sampler (game-motion, inside the capture flush on the main thread) calls
    /// <see cref="EnterInterest"/> with the four <c>TransformFrame</c> slots of a mover entering the peer's region,
    /// <see cref="AddFrame"/> for every sampled tick of a mover in interest and <see cref="ExitInterest"/> when it leaves.
    /// The scheduler grants a byte budget (<see cref="SetMotionBudget"/>) and a worker calls <see cref="Flush"/>, which
    /// sends, in this order: releases (always), assignments with their Q-A baselines, and the frames by priority
    /// (followed/inspected, near field, far field) while the budget lasts. Sequence numbers of <c>MotionControl</c>
    /// messages are assigned when the message is built, so every table operation is bound to the message that carries
    /// it (a release frees its interest slot only when the client acknowledged exactly that message).
    /// </para>
    /// <para>
    /// <b>Keyframes and deltas.</b> The newest baseline frame of an assignment is the first reference. A frame is sent
    /// as a delta against the current reference while the reference is less than 64 ticks old (normally three deltas
    /// after every keyframe), otherwise, and whenever the position would need the absolute escape, as a keyframe that
    /// becomes the new reference. References are stored exactly as the client decodes them (the host decodes its own
    /// records), so both sides compute identical deltas. Frames dropped for the budget never change the reference.
    /// </para>
    /// <para>
    /// <b>Degradation</b> (ARCHITECTURE.md#live-session): with <see cref="MotionQuality.FarHalf"/> far-field movers are sent every second
    /// sample, with <see cref="MotionQuality.FarQuarter"/> and <see cref="MotionQuality.Reduced"/> every fourth (the
    /// phase is spread over the movers); the client extrapolates or interpolates the skipped frames. Bytes beyond the
    /// grant (releases and followed movers are never throttled) are carried as debt into the next frames.
    /// </para>
    /// <para>
    /// <b>Threads.</b> Every member is thread-safe: each peer's state has its own lock, so flushes of different peers
    /// can run on different workers while the main thread samples (the engine phases normally keep them apart anyway).
    /// Unknown peers are ignored. <see cref="Flush"/> never throws for data problems: an unexpected failure is logged
    /// and the peer's interest table is reset (release-all and fresh assignments), so the client recovers.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class MotionHostEncoder : IMotionBudgetSink
    {
        /// <summary>Assignments sent per flush even when the budget is exhausted, so interest entries always progress.</summary>
        public const int MinAssignsPerFlush = 16;

        /// <summary>Share of the remaining budget assignments may use while frames are waiting.</summary>
        private const double AssignBudgetShare = 0.5;

        /// <summary>Frames queued per peer between two flushes (a peer that is not flushed must not grow without bound).</summary>
        private const int MaxPendingFrames = 1 << 16;

        /// <summary>Debt is capped at this many per-frame grants.</summary>
        private const int MaxDebtFrames = 4;

        private const byte PriorityExact = 0, PriorityNear = 1, PriorityFar = 2;

        private readonly ILog _log;
        private readonly object _peersLock = new object();
        private readonly PeerState[] _peers = new PeerState[256];

        public MotionHostEncoder(ILog log)
        {
            _log = log;
        }

        // ------------------------------------------------------------------------------------------------ peers

        /// <summary>Starts a fresh motion stream for <paramref name="peer"/> (an existing one is replaced).</summary>
        public void AddPeer(PeerId peer)
        {
            var state = new PeerState(peer);
            bool replaced;
            lock (_peersLock)
            {
                replaced = _peers[peer.Value] != null;
                Volatile.Write(ref _peers[peer.Value], state);
            }
            if (replaced) _log.Debug(LogCategory.Motion, "motion: " + peer + " added again; its motion stream starts over");
        }

        public void RemovePeer(PeerId peer)
        {
            lock (_peersLock)
            {
                Volatile.Write(ref _peers[peer.Value], null);
            }
        }

        /// <summary>
        /// Short-drop resume (ARCHITECTURE.md#leaving-kicks-and-recovery): the next flush sends <c>ReleaseAll</c> and then re-enters every mover of the
        /// current interest with a fresh interest slot, generation and Q-A baseline built from its newest four samples.
        /// Movers re-entered by the caller through <see cref="EnterInterest"/> merge their fresh frames into that baseline.
        /// </summary>
        public void ResetPeer(PeerId peer)
        {
            PeerState state = GetPeer(peer);
            if (state == null) return;
            lock (state.Sync)
            {
                ResetPeerLocked(state);
                state.Stats.Resets++;
            }
            _log.Info(LogCategory.Motion, "motion: interest table of " + peer + " reset (release-all and fresh baselines)");
        }

        // ------------------------------------------------------------------------------------------------ interest

        /// <summary>
        /// A mover enters the peer's interest: the next flush assigns it an interest slot and sends the Q-A baseline of
        /// its four <c>TransformFrame</c> slots (<paramref name="fourTicks"/>: their host ticks, ≡
        /// <paramref name="updateFrame"/> mod 16). A mover already in interest only merges the frames into its history
        /// (or, if its update frame changed, is released and entered again).
        /// </summary>
        /// <returns>False when the entry was refused (an unknown peer, or a baseline that is not valid): the mover is then not in the peer's interest.</returns>
        public bool EnterInterest(PeerId peer, NetSlot slot, byte updateFrame, MotionFrame[] fourSlots, uint[] fourTicks)
        {
            if (slot.IsNone) throw new ArgumentException("a mover slot must not be none", nameof(slot));
            if (updateFrame > 15) throw new ArgumentOutOfRangeException(nameof(updateFrame), updateFrame, "UpdateFrame is 0..15");
            if (fourSlots == null) throw new ArgumentNullException(nameof(fourSlots));
            if (fourTicks == null) throw new ArgumentNullException(nameof(fourTicks));
            if (fourSlots.Length < 4 || fourTicks.Length < 4) throw new ArgumentException("four frames and four ticks are required");
            PeerState state = GetPeer(peer);
            if (state == null) return false;

            string problem = MotionCodec.DescribeBaselineProblem(updateFrame, fourTicks);
            for (int i = 0; problem == null && i < 4; i++)
            {
                if (!MotionMath.IsFinite(fourSlots[i])) problem = "a baseline frame is not finite";
            }
            lock (state.Sync)
            {
                if (problem != null)
                {
                    state.Stats.EntriesRejected++;
                    WarnOnce(state, PeerWarning.Baseline, "motion: interest entry of " + slot + " for " + state.Peer + " refused: " + problem);
                    return false;
                }
                if (state.TrackOf.TryGetValue(slot.Value, out int existing))
                {
                    if (state.Tracks[existing].UpdateFrame == updateFrame)
                    {
                        for (int i = 0; i < 4; i++) PushHistory(state, existing, fourTicks[i], fourSlots[i]);
                        return true;
                    }
                    // the mover's phase changed (the client's assignment carries the old one): release and enter again
                    ExitLocked(state, existing);
                }
                int track = AllocateTrack(state, slot, updateFrame);
                for (int i = 0; i < 4; i++) PushHistory(state, track, fourTicks[i], fourSlots[i]);
                QueueOp(state, new ControlOp { Kind = MotionWire.OpAssign, Track = track, Version = state.Tracks[track].Version });
                state.Stats.Entries++;
                return true;
            }
        }

        /// <summary>A mover leaves the peer's interest: the next flush sends its release with a pin instruction (ARCHITECTURE.md#movers).</summary>
        public void ExitInterest(PeerId peer, NetSlot slot)
        {
            PeerState state = GetPeer(peer);
            if (state == null) return;
            lock (state.Sync)
            {
                if (!state.TrackOf.TryGetValue(slot.Value, out int track)) return;
                ExitLocked(state, track);
                state.Stats.Exits++;
            }
        }

        /// <summary>
        /// Queues the frame of tick <paramref name="tick"/> (≡ the mover's update frame mod 16) for the next flush.
        /// <paramref name="nearField"/> ranks it before far-field frames and exempts it from the far-field rate;
        /// <paramref name="exactVelocity"/> (followed or inspected movers) ranks it first, sends the f16 velocity and is
        /// never throttled. Keyframe or delta is decided when the frame is encoded. Frames for movers outside the
        /// interest, older than the mover's newest known frame or with invalid data are counted and dropped.
        /// </summary>
        public void AddFrame(PeerId peer, uint tick, NetSlot slot, in MotionFrame frame, bool nearField, bool exactVelocity)
        {
            PeerState state = GetPeer(peer);
            if (state == null) return;
            bool finite = MotionMath.IsFinite(frame);
            lock (state.Sync)
            {
                if (!state.TrackOf.TryGetValue(slot.Value, out int track))
                {
                    state.Stats.FramesNotInInterest++;
                    return;
                }
                ref Track t = ref state.Tracks[track];
                if ((tick & 15u) != t.UpdateFrame || !finite)
                {
                    state.Stats.FramesRejected++;
                    WarnOnce(state, PeerWarning.Frame, "motion: frame of " + slot + " at tick " + tick.ToString(CultureInfo.InvariantCulture) + " for " + state.Peer
                             + (finite ? " does not match its UpdateFrame " + t.UpdateFrame.ToString(CultureInfo.InvariantCulture) : " is not finite") + " (further cases are only counted)");
                    return;
                }
                if (t.HistoryCount > 0 && !MotionMath.IsAfter(tick, t.NewestTick))
                {
                    state.Stats.FramesStale++;
                    return;
                }
                PushHistory(state, track, tick, frame);
                if (state.FrameCount >= MaxPendingFrames)
                {
                    state.Stats.FramesDroppedBudget++;
                    WarnOnce(state, PeerWarning.Overflow, "motion: more than " + MaxPendingFrames.ToString(CultureInfo.InvariantCulture) + " frames queued for "
                             + state.Peer + " without a flush; further frames are dropped until the next flush");
                    return;
                }
                if (state.FrameCount == state.Frames.Length) Array.Resize(ref state.Frames, state.Frames.Length * 2);
                ref PendingFrame pending = ref state.Frames[state.FrameCount++];
                pending.Tick = tick;
                pending.Track = track;
                pending.Version = t.Version;
                pending.Priority = exactVelocity ? PriorityExact : nearField ? PriorityNear : PriorityFar;
                pending.Exact = exactVelocity;
                pending.Staleness = 0;
                pending.Frame = frame;
                pending.Frame.Synthetic = false;
                state.Stats.FramesAdded++;
            }
        }

        /// <summary>The client applied every <c>MotionControl</c> message up to <paramref name="lastAppliedMotionControlSeq"/>.</summary>
        public void OnAck(PeerId peer, uint lastAppliedMotionControlSeq)
        {
            PeerState state = GetPeer(peer);
            if (state == null) return;
            lock (state.Sync)
            {
                if (MotionMath.IsAfter(lastAppliedMotionControlSeq, state.ControlSeq))
                {
                    state.Stats.AcksIgnored++;
                    WarnOnce(state, PeerWarning.Ack, "motion: " + state.Peer + " acknowledged MotionControl " + lastAppliedMotionControlSeq.ToString(CultureInfo.InvariantCulture)
                             + " but only " + state.ControlSeq.ToString(CultureInfo.InvariantCulture) + " were sent; ignored");
                    return;
                }
                state.Table.OnClientAck(lastAppliedMotionControlSeq);
            }
        }

        /// <summary>True while the mover is in the peer's interest (also while its assignment waits to be sent).</summary>
        public bool IsInInterest(PeerId peer, NetSlot slot)
        {
            PeerState state = GetPeer(peer);
            if (state == null) return false;
            lock (state.Sync)
            {
                return state.TrackOf.ContainsKey(slot.Value);
            }
        }

        /// <summary>The per-frame motion grant of the scheduler (ARCHITECTURE.md#deferred-values-and-link-pacing); int.MaxValue means unlimited (the default).</summary>
        public void SetMotionBudget(PeerId peer, int bytesThisFrame, MotionQuality quality)
        {
            PeerState state = GetPeer(peer);
            if (state == null) return;
            lock (state.Sync)
            {
                state.BudgetBytes = bytesThisFrame < 0 ? 0 : bytesThisFrame;
                state.Quality = (byte)quality > (byte)MotionQuality.Reduced ? MotionQuality.Reduced : quality;
            }
        }

        /// <summary>Counters of the peer's stream (default when the peer is unknown).</summary>
        public MotionEncoderStats GetStats(PeerId peer)
        {
            PeerState state = GetPeer(peer);
            if (state == null) return default;
            lock (state.Sync)
            {
                MotionEncoderStats stats = state.Stats;
                stats.InInterest = state.TrackOf.Count;
                int awaiting = 0;
                for (int i = 0; i < state.TrackHighWater; i++)
                {
                    if (state.Tracks[i].State == TrackState.AwaitingAssign) awaiting++;
                }
                stats.AwaitingAssign = awaiting;
                stats.PendingReleases = state.Table.PendingReleases;
                stats.LastControlSeq = state.ControlSeq;
                stats.LastAckedControlSeq = state.Table.LastAcknowledged;
                return stats;
            }
        }

        // ------------------------------------------------------------------------------------------------ flush

        /// <summary>
        /// Sends the peer's pending <c>MotionControl</c> messages on the reliable lane and this frame's Q-B datagrams on
        /// the unreliable lane (one or more per tick, each at most 1200 bytes, every record for a mover whose update
        /// frame matches the datagram's tick), within the motion budget. Queued frames that were not sent are dropped.
        /// </summary>
        [ThreadAffinity(ThreadKind.Worker)]
        public void Flush(PeerId peer, IPeerOutput output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            PeerState state = GetPeer(peer);
            if (state == null) return;
            lock (state.Sync)
            {
                try
                {
                    FlushLocked(state, output);
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    state.Stats.FlushFailures++;
                    state.FrameCount = 0;
                    state.RecordCount = 0;
                    ResetPeerLocked(state);
                    _log.Error(LogCategory.Motion, "motion: flush for " + peer + " failed; its interest table is reset", e);
                }
            }
        }

        private void FlushLocked(PeerState s, IPeerOutput output)
        {
            bool unlimited = s.BudgetBytes == int.MaxValue;
            long available = unlimited ? long.MaxValue : Math.Max(0L, s.BudgetBytes - s.Debt);
            long used = 0;
            FlushControl(s, output, unlimited, available, ref used);
            FlushFrames(s, output, unlimited, available, ref used);
            if (!unlimited)
            {
                long over = used - available;
                s.Debt = over > 0 ? Math.Min(over, (long)s.BudgetBytes * MaxDebtFrames) : 0;
            }
        }

        private void FlushControl(PeerState s, IPeerOutput output, bool unlimited, long available, ref long used)
        {
            int opCount = s.OpCount;
            if (opCount == 0) return;
            long assignAllowance = unlimited ? long.MaxValue : (long)(Math.Max(0L, available - used) * AssignBudgetShare);
            long assignBytes = 0;
            int assignsWritten = 0;
            int kept = 0;
            s.ControlOpen = false;
            for (int i = 0; i < opCount; i++)
            {
                ControlOp op = s.Ops[i];
                switch (op.Kind)
                {
                    case MotionWire.OpRelease:
                    {
                        uint seq = OpenControl(s);
                        s.Table.Release(op.Slot, seq);
                        s.Message.WriteByte(MotionWire.OpRelease);
                        s.Message.WriteUInt16(op.InterestSlot);
                        s.Message.WriteByte((byte)((op.Generation & MotionWire.GenerationMask) | MotionWire.PinFlag));
                        s.ControlOps++;
                        s.Stats.ReleasesSent++;
                        break;
                    }
                    case MotionWire.OpReleaseAll:
                    {
                        uint seq = OpenControl(s);
                        s.Table.ReleaseAll(seq);
                        s.Message.WriteByte(MotionWire.OpReleaseAll);
                        s.Message.WriteByte(MotionWire.PinFlag);
                        s.ControlOps++;
                        break;
                    }
                    default:
                    {
                        if (op.Track < 0 || op.Track >= s.TrackHighWater) break;
                        ref Track t = ref s.Tracks[op.Track];
                        if (t.State != TrackState.AwaitingAssign || t.Version != op.Version) break;   // the mover left again
                        if (!unlimited && assignsWritten >= MinAssignsPerFlush && assignBytes >= assignAllowance)
                        {
                            s.Ops[kept++] = op;
                            s.Stats.AssignsDeferred++;
                            break;
                        }
                        uint seq = s.ControlOpen ? s.ControlSeq : unchecked(s.ControlSeq + 1);
                        if (!s.Table.TryAssign(t.Slot, seq, out ushort interestSlot, out byte generation))
                        {
                            s.Ops[kept++] = op;
                            s.Stats.AssignsDeferred++;
                            WarnOnce(s, PeerWarning.TableFull, "motion: the interest-slot table of " + s.Peer + " is full ("
                                     + s.Table.ToString() + "); entries wait for acknowledged releases");
                            break;
                        }
                        OpenControl(s);
                        int start = s.Message.Length;
                        s.Message.WriteByte(MotionWire.OpAssign);
                        int recordStart = s.Message.Length;
                        int baseIndex = op.Track * 4;
                        for (int k = 0; k < 4; k++)
                        {
                            s.QaFrames[k] = s.HistoryFrames[baseIndex + k];
                            s.QaTicks[k] = s.HistoryTicks[baseIndex + k];
                        }
                        MotionCodec.WriteQA(s.Message, interestSlot, generation, t.Slot, t.UpdateFrame, s.QaFrames, s.QaTicks);
                        // decode our own record: the reference must be the frame exactly as the client holds it
                        s.Reader.Reset(s.Message.Buffer, recordStart, s.Message.Length - recordStart);
                        MotionCodec.ReadQA(s.Reader, out _, out _, out _, out _, s.QaFrames, s.QaTicks);
                        int newest = 0;
                        for (int k = 1; k < 4; k++)
                        {
                            if (MotionMath.IsAfter(s.QaTicks[k], s.QaTicks[newest])) newest = k;
                        }
                        t.State = TrackState.Assigned;
                        t.InterestSlot = interestSlot;
                        t.Generation = generation;
                        t.HasReference = true;
                        t.Reference = s.QaFrames[newest];
                        t.ReferenceTick = s.QaTicks[newest];
                        t.HasSent = false;
                        s.ControlOps++;
                        assignBytes += s.Message.Length - start;
                        assignsWritten++;
                        s.Stats.AssignsSent++;
                        break;
                    }
                }
                if (s.ControlOpen && (s.Message.Length - MotionWire.EnvelopeHeaderBytes - MotionWire.ControlHeaderBytes >= MotionWire.ControlSplitBodyBytes
                                      || s.ControlOps >= MotionWire.MaxControlOps))
                {
                    used += CloseControl(s, output);
                }
            }
            if (s.ControlOpen) used += CloseControl(s, output);
            s.OpCount = kept;
        }

        private static uint OpenControl(PeerState s)
        {
            if (s.ControlOpen) return s.ControlSeq;
            s.ControlSeq = unchecked(s.ControlSeq + 1);
            s.Message.Reset();
            s.Message.WriteByte((byte)MessageKind.MotionControl);
            s.Message.WriteByte(0);   // EnvelopeFlags.None
            s.Message.WriteUInt32(s.ControlSeq);
            s.Message.WriteUInt16(0); // op count, patched when the message is closed
            s.ControlOps = 0;
            s.ControlOpen = true;
            return s.ControlSeq;
        }

        private int CloseControl(PeerState s, IPeerOutput output)
        {
            s.ControlOpen = false;
            byte[] buffer = s.Message.Buffer;
            int countPosition = MotionWire.EnvelopeHeaderBytes + 4;
            buffer[countPosition] = (byte)s.ControlOps;
            buffer[countPosition + 1] = (byte)(s.ControlOps >> 8);
            int length = s.Message.Length;
            SendResult result = output.Send(Lane.Reliable, Delivery.ReliableOrdered, buffer, 0, length);
            s.Stats.ControlMessages++;
            s.Stats.ControlBytes += length;
            if (result != SendResult.Ok)
            {
                WarnOnce(s, PeerWarning.ControlSend, "motion: MotionControl " + s.ControlSeq.ToString(CultureInfo.InvariantCulture) + " to " + s.Peer
                         + " was not accepted (" + result + ")");
            }
            return length;
        }

        private void FlushFrames(PeerState s, IPeerOutput output, bool unlimited, long available, ref long used)
        {
            int count = s.FrameCount;
            s.FrameCount = 0;
            s.RecordCount = 0;
            if (count == 0) return;

            // valid frames and the newest tick, for the staleness ranking
            uint newest = 0;
            bool haveNewest = false;
            if (s.Order.Length < count) s.Order = new int[Math.Max(count, s.Order.Length * 2)];
            int valid = 0;
            for (int i = 0; i < count; i++)
            {
                ref PendingFrame f = ref s.Frames[i];
                if (f.Track < 0 || f.Track >= s.TrackHighWater)
                {
                    s.Stats.FramesStale++;
                    continue;
                }
                ref Track t = ref s.Tracks[f.Track];
                if (t.State != TrackState.Assigned || t.Version != f.Version
                    || !MotionMath.IsAfter(f.Tick, t.ReferenceTick) || (t.HasSent && !MotionMath.IsAfter(f.Tick, t.LastSentTick)))
                {
                    s.Stats.FramesStale++;
                    continue;
                }
                if (!haveNewest || MotionMath.IsAfter(f.Tick, newest))
                {
                    newest = f.Tick;
                    haveNewest = true;
                }
                s.Order[valid++] = i;
            }
            for (int k = 0; k < valid; k++)
            {
                ref PendingFrame f = ref s.Frames[s.Order[k]];
                ref Track t = ref s.Tracks[f.Track];
                int staleness = MotionMath.TickDiff(newest, t.HasSent ? t.LastSentTick : t.ReferenceTick);
                f.Staleness = staleness < 0 ? 0 : staleness;
            }
            Array.Sort(s.Order, 0, valid, s.OrderComparer);

            s.Arena.Reset();
            s.TickSlots = 0;
            bool exhausted = false;
            MotionQuality quality = s.Quality;
            for (int k = 0; k < valid; k++)
            {
                ref PendingFrame f = ref s.Frames[s.Order[k]];
                ref Track t = ref s.Tracks[f.Track];
                // an earlier sample of the same mover may have been sent in this flush
                if (!MotionMath.IsAfter(f.Tick, t.ReferenceTick) || (t.HasSent && !MotionMath.IsAfter(f.Tick, t.LastSentTick)))
                {
                    s.Stats.FramesStale++;
                    continue;
                }
                if (f.Priority == PriorityFar && quality != MotionQuality.Full && !RateSelects(t.Slot, f.Tick, quality))
                {
                    s.Stats.FramesSkippedRate++;
                    continue;
                }
                if (exhausted && f.Priority != PriorityExact)
                {
                    s.Stats.FramesDroppedBudget++;
                    continue;
                }

                int age = MotionMath.TickDiff(f.Tick, t.ReferenceTick);
                bool keyframe = !t.HasReference || age >= MotionCodec.KeyframeEvery * MotionMath.TicksPerStep
                                || age % MotionMath.TicksPerStep != 0 || age / MotionMath.TicksPerStep > 7;
                s.Scratch.Reset();
                if (!keyframe)
                {
                    bool escaped = MotionCodec.WriteDeltaRecord(s.Scratch, t.InterestSlot, t.Generation, (byte)(age / MotionMath.TicksPerStep), f.Frame, t.Reference, f.Exact);
                    if (escaped)
                    {
                        keyframe = true;
                        s.Scratch.Reset();
                    }
                }
                if (keyframe) MotionCodec.WriteQBKeyframe(s.Scratch, t.InterestSlot, t.Generation, f.Frame, f.Exact);
                int size = s.Scratch.Length;
                int overhead = DatagramOverheadFor(s, f.Tick, size, commit: false);
                if (!unlimited && f.Priority != PriorityExact && used + size + overhead > available)
                {
                    exhausted = true;
                    s.Stats.FramesDroppedBudget++;
                    continue;
                }

                if (keyframe)
                {
                    s.Reader.Reset(s.Scratch.Buffer, 0, size);
                    MotionCodec.TryReadQB(s.Reader, f.Tick, null, out _, out MotionFrame decoded);
                    t.Reference = decoded;
                    t.ReferenceTick = f.Tick;
                    t.HasReference = true;
                    s.Stats.Keyframes++;
                }
                else
                {
                    s.Stats.Deltas++;
                }
                t.HasSent = true;
                t.LastSentTick = f.Tick;
                DatagramOverheadFor(s, f.Tick, size, commit: true);
                if (s.RecordCount == s.Records.Length) Array.Resize(ref s.Records, s.Records.Length * 2);
                s.Records[s.RecordCount++] = new RecordRef { Tick = f.Tick, Offset = s.Arena.Length, Length = size };
                s.Arena.WriteBytes(s.Scratch.Buffer, 0, size);
                used += size + overhead;
                s.Stats.FramesSent++;
            }
            SendDatagrams(s, output);
        }

        /// <summary>
        /// Bytes of datagram overhead that appending a record of <paramref name="size"/> bytes for <paramref name="tick"/>
        /// costs: the records of a tick are packed greedily in selection order, exactly as <see cref="SendDatagrams"/>
        /// does, so this is the exact envelope and header cost.
        /// </summary>
        private static int DatagramOverheadFor(PeerState s, uint tick, int size, bool commit)
        {
            const int maxRecordBytes = MotionWire.MaxDatagramBytes - MotionWire.DatagramOverheadBytes;
            for (int i = 0; i < s.TickSlots; i++)
            {
                if (s.TickTicks[i] != tick) continue;
                if (s.TickBytes[i] + size <= maxRecordBytes)
                {
                    if (commit) s.TickBytes[i] += size;
                    return 0;
                }
                if (commit) s.TickBytes[i] = size;
                return MotionWire.DatagramOverheadBytes;
            }
            if (commit)
            {
                if (s.TickSlots == s.TickTicks.Length)
                {
                    Array.Resize(ref s.TickTicks, s.TickTicks.Length * 2);
                    Array.Resize(ref s.TickBytes, s.TickBytes.Length * 2);
                }
                s.TickTicks[s.TickSlots] = tick;
                s.TickBytes[s.TickSlots] = size;
                s.TickSlots++;
            }
            return MotionWire.DatagramOverheadBytes;
        }

        private void SendDatagrams(PeerState s, IPeerOutput output)
        {
            int count = s.RecordCount;
            if (count == 0) return;
            Array.Sort(s.Records, 0, count, RecordOrder.Instance);
            int i = 0;
            while (i < count)
            {
                uint tick = s.Records[i].Tick;
                s.DatagramSeq = unchecked((ushort)(s.DatagramSeq + 1));
                s.Message.Reset();
                s.Message.WriteByte((byte)MessageKind.Motion);
                s.Message.WriteByte(0);   // EnvelopeFlags.None
                s.Message.WriteUInt32(tick);
                s.Message.WriteUInt16(s.DatagramSeq);
                int records = 0;
                while (i < count && s.Records[i].Tick == tick && s.Message.Length + s.Records[i].Length <= MotionWire.MaxDatagramBytes)
                {
                    s.Message.WriteBytes(s.Arena.Buffer, s.Records[i].Offset, s.Records[i].Length);
                    i++;
                    records++;
                }
                if (records == 0)
                {
                    i++;   // cannot happen (a record is far below a datagram); never loop forever
                    continue;
                }
                SendResult result = output.Send(Lane.Unreliable, Delivery.Unreliable, s.Message.Buffer, 0, s.Message.Length);
                s.Stats.Datagrams++;
                s.Stats.DatagramBytes += s.Message.Length;
                if (result != SendResult.Ok) s.Stats.DatagramsRefused++;
            }
            s.RecordCount = 0;
        }

        /// <summary>
        /// The far-field rate of the degradation ladder: half or quarter of the samples, with the phase spread over the
        /// movers by a multiplicative hash of the slot so the datagrams of consecutive ticks stay balanced.
        /// </summary>
        private static bool RateSelects(NetSlot slot, uint tick, MotionQuality quality)
        {
            uint mask = quality == MotionQuality.FarHalf ? 1u : 3u;
            uint phase = unchecked(slot.Value * 2654435761u) >> 28;
            return (((tick >> 4) + phase) & mask) == 0;
        }

        // ------------------------------------------------------------------------------------------------ state

        private PeerState GetPeer(PeerId peer) => Volatile.Read(ref _peers[peer.Value]);

        private static void ResetPeerLocked(PeerState s)
        {
            s.OpCount = 0;
            QueueOp(s, new ControlOp { Kind = MotionWire.OpReleaseAll });
            for (int i = 0; i < s.TrackHighWater; i++)
            {
                ref Track t = ref s.Tracks[i];
                if (t.State == TrackState.Free) continue;
                t.State = TrackState.AwaitingAssign;
                t.HasReference = false;
                t.HasSent = false;
                QueueOp(s, new ControlOp { Kind = MotionWire.OpAssign, Track = i, Version = t.Version });
            }
        }

        private static void ExitLocked(PeerState s, int track)
        {
            ref Track t = ref s.Tracks[track];
            if (t.State == TrackState.Assigned)
            {
                QueueOp(s, new ControlOp
                {
                    Kind = MotionWire.OpRelease,
                    Slot = t.Slot,
                    InterestSlot = t.InterestSlot,
                    Generation = t.Generation
                });
            }
            s.TrackOf.Remove(t.Slot.Value);
            t.State = TrackState.Free;
            t.Version++;
            t.HistoryCount = 0;
            t.HasReference = false;
            t.HasSent = false;
            s.FreeTracks.Push(track);
        }

        private static int AllocateTrack(PeerState s, NetSlot slot, byte updateFrame)
        {
            int index;
            if (s.FreeTracks.Count > 0)
            {
                index = s.FreeTracks.Pop();
            }
            else
            {
                index = s.TrackHighWater++;
                if (index >= s.Tracks.Length)
                {
                    int size = s.Tracks.Length * 2;
                    Array.Resize(ref s.Tracks, size);
                    Array.Resize(ref s.HistoryFrames, size * 4);
                    Array.Resize(ref s.HistoryTicks, size * 4);
                }
            }
            ref Track t = ref s.Tracks[index];
            t.State = TrackState.AwaitingAssign;
            t.Version++;
            t.Slot = slot;
            t.UpdateFrame = updateFrame;
            t.InterestSlot = 0;
            t.Generation = 0;
            t.HistoryCount = 0;
            t.NewestTick = 0;
            t.HasReference = false;
            t.HasSent = false;
            s.TrackOf[slot.Value] = index;
            return index;
        }

        /// <summary>Keeps the four newest distinct samples of a mover, newest first (the source of re-entry baselines).</summary>
        private static void PushHistory(PeerState s, int track, uint tick, in MotionFrame frame)
        {
            ref Track t = ref s.Tracks[track];
            int baseIndex = track * 4;
            int count = t.HistoryCount;
            int position = 0;
            while (position < count)
            {
                int diff = MotionMath.TickDiff(tick, s.HistoryTicks[baseIndex + position]);
                if (diff == 0)
                {
                    s.HistoryFrames[baseIndex + position] = frame;
                    s.HistoryFrames[baseIndex + position].Synthetic = false;
                    return;
                }
                if (diff > 0) break;
                position++;
            }
            if (position >= 4) return;
            for (int i = Math.Min(count, 3); i > position; i--)
            {
                s.HistoryTicks[baseIndex + i] = s.HistoryTicks[baseIndex + i - 1];
                s.HistoryFrames[baseIndex + i] = s.HistoryFrames[baseIndex + i - 1];
            }
            s.HistoryTicks[baseIndex + position] = tick;
            s.HistoryFrames[baseIndex + position] = frame;
            s.HistoryFrames[baseIndex + position].Synthetic = false;
            if (count < 4) t.HistoryCount = count + 1;
            t.NewestTick = s.HistoryTicks[baseIndex];
        }

        private static void QueueOp(PeerState s, in ControlOp op)
        {
            if (s.OpCount == s.Ops.Length) Array.Resize(ref s.Ops, s.Ops.Length * 2);
            s.Ops[s.OpCount++] = op;
        }

        private void WarnOnce(PeerState s, PeerWarning warning, string message)
        {
            if ((s.Warned & warning) != 0) return;
            s.Warned |= warning;
            _log.Warn(LogCategory.Motion, message);
        }

        [Flags]
        private enum PeerWarning
        {
            None = 0,
            Baseline = 1,
            Frame = 2,
            Overflow = 4,
            Ack = 8,
            TableFull = 16,
            ControlSend = 32
        }

        private enum TrackState : byte { Free, AwaitingAssign, Assigned }

        private struct Track
        {
            public TrackState State;
            public int Version;
            public NetSlot Slot;
            public byte UpdateFrame;
            public ushort InterestSlot;
            public byte Generation;
            public int HistoryCount;
            public uint NewestTick;
            public bool HasReference;
            public uint ReferenceTick;
            public MotionFrame Reference;
            public bool HasSent;
            public uint LastSentTick;
        }

        private struct ControlOp
        {
            public byte Kind;
            public int Track;
            public int Version;
            public NetSlot Slot;
            public ushort InterestSlot;
            public byte Generation;
        }

        private struct PendingFrame
        {
            public uint Tick;
            public int Track;
            public int Version;
            public byte Priority;
            public bool Exact;
            public int Staleness;
            public MotionFrame Frame;
        }

        private struct RecordRef
        {
            public uint Tick;
            public int Offset;
            public int Length;
        }

        /// <summary>Records by tick, then in selection order (their arena offset).</summary>
        private sealed class RecordOrder : IComparer<RecordRef>
        {
            public static readonly RecordOrder Instance = new RecordOrder();

            public int Compare(RecordRef a, RecordRef b)
            {
                int diff = MotionMath.TickDiff(a.Tick, b.Tick);
                if (diff != 0) return diff < 0 ? -1 : 1;
                return a.Offset.CompareTo(b.Offset);
            }
        }

        /// <summary>Pending frames by priority, then staleness (longest unsent first), then mover, then tick.</summary>
        private sealed class FrameOrder : IComparer<int>
        {
            private readonly PeerState _state;

            public FrameOrder(PeerState state)
            {
                _state = state;
            }

            public int Compare(int x, int y)
            {
                PendingFrame[] frames = _state.Frames;
                ref PendingFrame a = ref frames[x];
                ref PendingFrame b = ref frames[y];
                if (a.Priority != b.Priority) return a.Priority < b.Priority ? -1 : 1;
                if (a.Staleness != b.Staleness) return a.Staleness > b.Staleness ? -1 : 1;
                if (a.Track != b.Track) return a.Track < b.Track ? -1 : 1;
                int diff = MotionMath.TickDiff(a.Tick, b.Tick);
                return diff < 0 ? -1 : diff > 0 ? 1 : 0;
            }
        }

        private sealed class PeerState
        {
            public readonly object Sync = new object();
            public readonly PeerId Peer;
            public readonly InterestSlotTable Table = new InterestSlotTable(MotionWire.MaxInterestSlots); // what clients accept
            public readonly Dictionary<uint, int> TrackOf = new Dictionary<uint, int>();
            public readonly Stack<int> FreeTracks = new Stack<int>();
            public readonly ByteWriter Message = new ByteWriter(MotionWire.MaxDatagramBytes);
            public readonly ByteWriter Arena = new ByteWriter(4096);
            public readonly ByteWriter Scratch = new ByteWriter(64);
            public readonly ByteReader Reader = new ByteReader();
            public readonly MotionFrame[] QaFrames = new MotionFrame[4];
            public readonly uint[] QaTicks = new uint[4];
            public readonly FrameOrder OrderComparer;

            public Track[] Tracks = new Track[64];
            public MotionFrame[] HistoryFrames = new MotionFrame[64 * 4];
            public uint[] HistoryTicks = new uint[64 * 4];
            public int TrackHighWater;
            public ControlOp[] Ops = new ControlOp[64];
            public int OpCount;
            public PendingFrame[] Frames = new PendingFrame[256];
            public int FrameCount;
            public int[] Order = new int[256];
            public RecordRef[] Records = new RecordRef[256];
            public int RecordCount;
            public uint[] TickTicks = new uint[16];
            public int[] TickBytes = new int[16];
            public int TickSlots;

            public int BudgetBytes = int.MaxValue;
            public MotionQuality Quality = MotionQuality.Full;
            public long Debt;
            public uint ControlSeq;
            public bool ControlOpen;
            public int ControlOps;
            public ushort DatagramSeq;
            public PeerWarning Warned;
            public MotionEncoderStats Stats;

            public PeerState(PeerId peer)
            {
                Peer = peer;
                OrderComparer = new FrameOrder(this);
            }
        }
    }
}
