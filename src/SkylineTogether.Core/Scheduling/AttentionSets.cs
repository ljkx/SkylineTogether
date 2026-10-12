using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// The host's attention model per peer (ARCH §4.8.3): what each player can currently observe, derived from the view
    /// reports and from the host's queries. It provides
    /// <list type="bullet">
    /// <item>the <b>motion interest</b> region (disc around the pivot with radius clamp(k × zoom, 300 m, 2500 m), entered at
    /// r and left at 1.25 r, plus the frustum footprint; the degradation ladder can scale the radius down,
    /// <see cref="SetMotionRadiusScale"/>) and the per-mover membership that game-motion maintains with
    /// <see cref="SetInMotionInterest"/>;</item>
    /// <item>the <b>in-view static set</b> (quad-tree query of game-host-capture at 1 Hz or after a large camera move,
    /// <see cref="NeedsInViewRefresh"/>);</item>
    /// <item>the <b>inspection closure</b> of the selected or followed entity;</item>
    /// <item>the <b>topics</b> of the active infoview and open panels (<see cref="TopicMap"/>) and the <b>topic set</b>: the
    /// entities whose topic data the active topics need beyond the in-view set (<see cref="TopicMap.ScopeOf"/>: residents,
    /// households or companies of in-view buildings, every line, the statistic entities).</item>
    /// </list>
    /// <para>
    /// Threading: every mutator may run on the main thread (or the scheduler worker for the radius scale); mutators
    /// serialise on one lock and publish immutable snapshots, so the <see cref="IAttentionView"/> queries, which the
    /// per-peer scheduler calls on its worker for every candidate slot, take no lock and allocate nothing.
    /// Input from the network (view reports) is validated: non-finite or absurd coordinates are rejected and logged
    /// (rate-limited), and the previous view stays in effect.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class AttentionSets : IAttentionView
    {
        private const float MaxCoordinate = 1e6f;          // the playable map is about 14.3 km wide
        private const double WarnIntervalSeconds = 10.0;

        private static readonly ReadOnlyCollection<PeerId> NoPeers = new ReadOnlyCollection<PeerId>(new PeerId[0]);

        private readonly TopicMap _topics;
        private readonly SchedulerSettings _s;
        private readonly object _lock = new object();
        private readonly PeerAttention[] _peers = new PeerAttention[256];
        private ReadOnlyCollection<PeerId> _peerList = NoPeers;
        private double _lastNow;
        private double _lastWarn = double.NegativeInfinity;
        private int _suppressedWarnings;

        /// <param name="topics">Type topics for <see cref="TypeMatchesTopics"/>; null = no type has a topic.</param>
        /// <param name="config">Radii, footprint, refresh and size tunables.</param>
        public AttentionSets(TopicMap topics, SchedulerConfig config)
        {
            _topics = topics;
            _s = SchedulerSettings.From(config);
        }

        /// <summary>Optional log (category <see cref="LogCategory.Scheduling"/>): rejected input, topic and selection changes.</summary>
        public ILog Log { get; set; }

        /// <summary>The topic map used by <see cref="TypeMatchesTopics"/> (may be null).</summary>
        public TopicMap TypeTopics => _topics;

        /// <summary>The peers this model knows (a snapshot; safe to enumerate on any thread).</summary>
        public IReadOnlyCollection<PeerId> Peers => Volatile.Read(ref _peerList);

        // ------------------------------------------------------------------ view reports

        /// <summary>
        /// Accepts a peer's view report (copied; the caller may reuse its instance, e.g. with <c>ViewReport.ReadInto</c>).
        /// A null report, or one with non-finite or out-of-range coordinates, is rejected and logged; the previous view
        /// stays in effect. <paramref name="now"/> is the host's monotonic time in seconds.
        /// </summary>
        public void OnViewReport(PeerId peer, Protocol.ViewReport report, double now)
        {
            if (!SchedulerSettings.IsFinite(now)) now = Volatile.Read(ref _lastNow);
            Volatile.Write(ref _lastNow, now);
            if (report == null)
            {
                Warn(now, peer + ": null view report ignored");
                return;
            }
            if (!IsSane(report))
            {
                Warn(now, peer + ": view report with invalid coordinates ignored (pivot " + F(report.PivotX) + ", " + F(report.PivotZ)
                          + ", zoom " + F(report.Zoom) + ")");
                return;
            }
            bool selectionChanged, topicsChanged;
            ViewState state;
            lock (_lock)
            {
                PeerAttention holder = GetOrCreateLocked(peer);
                holder.LastReportTime = now;
                ViewState previous = holder.View;
                if (previous != null && SameView(previous.Report, report)) return;     // repeated report: nothing changed

                Protocol.ViewReport copy = Copy(report, new Protocol.ViewReport());
                state = new ViewState(_s, copy, now, holder.RadiusScale);
                holder.View = state;
                holder.BumpView();
                selectionChanged = previous == null || previous.Report.Selected != copy.Selected || previous.Report.Followed != copy.Followed;
                topicsChanged = previous == null || previous.Topics != state.Topics;
                if (selectionChanged) holder.BumpSelection();
                if (topicsChanged) holder.BumpTopics();
            }
            if (selectionChanged) LogDebug(peer + " selection " + state.Report.Selected + ", followed " + state.Report.Followed);
            if (topicsChanged) LogDebug(peer + " attention topics " + state.Topics);
        }

        /// <summary>A copy of the peer's last accepted view report, or null. Allocates; see <see cref="TryCopyLastView"/>.</summary>
        public Protocol.ViewReport LastView(PeerId peer)
        {
            ViewState view = Holder(peer)?.View;
            return view == null ? null : Copy(view.Report, new Protocol.ViewReport());
        }

        /// <summary>Copies the peer's last accepted view report into <paramref name="into"/>; false when there is none.</summary>
        public bool TryCopyLastView(PeerId peer, Protocol.ViewReport into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            ViewState view = Holder(peer)?.View;
            if (view == null) return false;
            Copy(view.Report, into);
            return true;
        }

        /// <summary>Host time of the peer's last view report (also repeated ones); NaN when none arrived.</summary>
        public double LastReportSeconds(PeerId peer)
        {
            PeerAttention holder = Holder(peer);
            if (holder == null) return double.NaN;
            lock (_lock) return holder.LastReportTime;
        }

        /// <summary>Increments with every changed view of the peer (0 = none yet).</summary>
        public int ViewVersion(PeerId peer) => Holder(peer)?.ViewVersion ?? 0;

        /// <summary>Increments whenever the selected or followed slot changes: the host then sends one immediate inspection
        /// sample (ARCH §4.7.9) and recomputes the closure.</summary>
        public int SelectionVersion(PeerId peer) => Holder(peer)?.SelectionVersion ?? 0;

        /// <summary>Increments whenever the peer's topics change (the flow-status channel sends a keyframe on activation).</summary>
        public int TopicsVersion(PeerId peer) => Holder(peer)?.TopicsVersion ?? 0;

        // ------------------------------------------------------------------ regions

        /// <summary>The peer's motion interest region (with the ladder's radius scale); a default region (radius 0, contains
        /// nothing) before the first view report. The footprint array is shared: callers must not modify it.</summary>
        public MotionInterestRegion GetMotionInterest(PeerId peer)
        {
            ViewState view = Holder(peer)?.View;
            return view == null ? default : view.Motion;
        }

        /// <summary>The unscaled region of what the camera shows (disc and footprint); the host's in-view query can use it.</summary>
        public MotionInterestRegion GetViewRegion(PeerId peer)
        {
            ViewState view = Holder(peer)?.View;
            return view == null ? default : view.View;
        }

        /// <summary>
        /// Applies the degradation ladder's radius scale (<see cref="DegradationController"/>; clamped to
        /// [min radius / max radius, 1]). The motion region is rebuilt when the scale changes by more than 0.2 %.
        /// </summary>
        [ThreadAffinity(ThreadKind.Any)]
        public void SetMotionRadiusScale(PeerId peer, float scale)
        {
            if (float.IsNaN(scale)) return;
            if (scale > 1f) scale = 1f;
            if (scale < _s.MinRadiusScale) scale = _s.MinRadiusScale;
            PeerAttention existing = Holder(peer);
            if (existing != null && Math.Abs(existing.RadiusScale - scale) < 0.002f) return;
            lock (_lock)
            {
                PeerAttention holder = GetOrCreateLocked(peer);
                holder.RadiusScale = scale;
                ViewState view = holder.View;
                if (view != null) holder.View = new ViewState(_s, view.Report, view.Time, scale);
            }
        }

        /// <summary>The radius scale in effect for the peer (1 when not degraded).</summary>
        public float MotionRadiusScale(PeerId peer) => Holder(peer)?.RadiusScale ?? 1f;

        // ------------------------------------------------------------------ sets fed by the host's queries

        /// <summary>Replaces the peer's in-view static set (game-host-capture's quad-tree query, ARCH §4.8.3 item 2); the
        /// refresh time is the last time this model was given (see the three-argument overload).</summary>
        public void SetInViewSlots(PeerId peer, IReadOnlyList<NetSlot> slots)
        {
            SetInViewSlots(peer, slots, Volatile.Read(ref _lastNow));
        }

        /// <summary>Replaces the peer's in-view static set, refreshed at <paramref name="now"/>.</summary>
        public void SetInViewSlots(PeerId peer, IReadOnlyList<NetSlot> slots, double now)
        {
            if (SchedulerSettings.IsFinite(now)) Volatile.Write(ref _lastNow, now);
            SlotSet set = SlotSet.Create(slots, _s.MaxAttentionSetSize, out int dropped);
            lock (_lock)
            {
                PeerAttention holder = GetOrCreateLocked(peer);
                holder.InView = set;
                holder.InViewRefreshedAt = SchedulerSettings.IsFinite(now) ? now : Volatile.Read(ref _lastNow);
                holder.ViewAtRefresh = holder.View;
            }
            if (dropped > 0) Warn(now, peer + ": in-view set truncated, " + dropped.ToString(CultureInfo.InvariantCulture) + " slot(s) dropped");
        }

        /// <summary>Replaces the inspection closure of the peer's selected or followed entity (depth 1-2, ARCH §4.8.3 item 3).</summary>
        public void SetClosure(PeerId peer, IReadOnlyList<NetSlot> closure)
        {
            SlotSet set = SlotSet.Create(closure, _s.MaxAttentionSetSize, out int dropped);
            lock (_lock) GetOrCreateLocked(peer).Closure = set;
            if (dropped > 0) Warn(Volatile.Read(ref _lastNow), peer + ": closure truncated, " + dropped.ToString(CultureInfo.InvariantCulture) + " slot(s) dropped");
        }

        /// <summary>
        /// Replaces the peer's topic set: the entities whose data the peer's active topics need beyond the in-view set
        /// (<see cref="TopicMap.ScopeOf"/>). The scheduler sends their dirty values of matching types at P1, after the
        /// in-view values (ARCH §4.8.4 P1 order).
        /// </summary>
        public void SetTopicSlots(PeerId peer, IReadOnlyList<NetSlot> slots)
        {
            SlotSet set = SlotSet.Create(slots, _s.MaxAttentionSetSize, out int dropped);
            lock (_lock) GetOrCreateLocked(peer).TopicSlots = set;
            if (dropped > 0) Warn(Volatile.Read(ref _lastNow), peer + ": topic set truncated, " + dropped.ToString(CultureInfo.InvariantCulture) + " slot(s) dropped");
        }

        /// <summary>
        /// Marks a mover as inside or outside the peer's motion interest (game-motion, at interest entry and exit, using
        /// <see cref="MotionInterestRegion.Contains"/> with the mover's current state for the hysteresis).
        /// </summary>
        public void SetInMotionInterest(PeerId peer, NetSlot slot, bool inside)
        {
            if (slot.IsNone) return;
            PeerAttention holder = Holder(peer);
            if (holder == null)
            {
                if (!inside) return;
                lock (_lock) holder = GetOrCreateLocked(peer);
            }
            holder.Motion.Set(slot.Value, inside);
        }

        /// <summary>Removes every mover from the peer's motion interest (short-drop resume resets interest, ARCH §7.8).</summary>
        public void ClearMotionInterest(PeerId peer)
        {
            Holder(peer)?.Motion.Clear();
        }

        /// <summary>
        /// True when the peer's in-view set should be recomputed: never computed, older than
        /// <see cref="SchedulerConfig.InViewRefreshSeconds"/>, or the camera moved a lot since (pivot or camera beyond
        /// <see cref="SchedulerConfig.InViewRefreshMoveFraction"/> of the radius, or the zoom by more than that fraction).
        /// False before the first view report.
        /// </summary>
        public bool NeedsInViewRefresh(PeerId peer, double now)
        {
            if (SchedulerSettings.IsFinite(now)) Volatile.Write(ref _lastNow, now);
            PeerAttention holder = Holder(peer);
            if (holder == null) return false;
            lock (_lock)
            {
                ViewState view = holder.View;
                if (view == null) return false;
                double refreshed = holder.InViewRefreshedAt;
                ViewState atRefresh = holder.ViewAtRefresh;
                if (double.IsNaN(refreshed) || atRefresh == null) return true;
                if (!SchedulerSettings.IsFinite(now) || now < refreshed || now - refreshed >= _s.InViewRefreshSeconds) return true;
                if (ReferenceEquals(view.Report, atRefresh.Report)) return false;
                double limit = _s.InViewRefreshMoveFraction * Math.Max(atRefresh.BaseRadius, 1f);
                Protocol.ViewReport a = atRefresh.Report, b = view.Report;
                if (Distance(a.PivotX, a.PivotZ, b.PivotX, b.PivotZ) > limit) return true;
                if (Distance(a.CamX, a.CamZ, b.CamX, b.CamZ) > limit) return true;
                double za = a.Zoom > 0 ? a.Zoom : 1, zb = b.Zoom > 0 ? b.Zoom : 1;
                double ratio = zb / za;
                return ratio > 1 + _s.InViewRefreshMoveFraction || ratio < 1 / (1 + _s.InViewRefreshMoveFraction);
            }
        }

        /// <summary>Host time of the peer's last in-view refresh; NaN when never refreshed.</summary>
        public double LastInViewRefreshSeconds(PeerId peer)
        {
            PeerAttention holder = Holder(peer);
            if (holder == null) return double.NaN;
            lock (_lock) return holder.InViewRefreshedAt;
        }

        public int InViewCount(PeerId peer) => Holder(peer)?.InView.Count ?? 0;

        public int ClosureCount(PeerId peer) => Holder(peer)?.Closure.Count ?? 0;

        public int TopicSlotCount(PeerId peer) => Holder(peer)?.TopicSlots.Count ?? 0;

        public int MotionInterestCount(PeerId peer) => Holder(peer)?.Motion.Count ?? 0;

        /// <summary>True when the slot is in the peer's topic set.</summary>
        public bool IsInTopicScope(PeerId peer, NetSlot slot)
        {
            PeerAttention holder = Holder(peer);
            return holder != null && holder.TopicSlots.Contains(slot.Value);
        }

        /// <summary>Forgets a peer (left or kicked).</summary>
        public void RemovePeer(PeerId peer)
        {
            lock (_lock)
            {
                if (_peers[peer.Value] == null) return;
                Volatile.Write(ref _peers[peer.Value], null);
                RebuildPeerListLocked();
            }
        }

        // ------------------------------------------------------------------ IAttentionView (lock-free)

        public bool IsInView(PeerId peer, NetSlot slot)
        {
            PeerAttention holder = Holder(peer);
            return holder != null && holder.InView.Contains(slot.Value);
        }

        public bool IsInClosure(PeerId peer, NetSlot slot)
        {
            PeerAttention holder = Holder(peer);
            return holder != null && holder.Closure.Contains(slot.Value);
        }

        public bool IsInMotionInterest(PeerId peer, NetSlot slot)
        {
            PeerAttention holder = Holder(peer);
            return holder != null && holder.Motion.Contains(slot.Value);
        }

        public AttentionTopic Topics(PeerId peer)
        {
            ViewState view = Holder(peer)?.View;
            return view == null ? AttentionTopic.None : view.Topics;
        }

        public bool TypeMatchesTopics(TypeRank type, AttentionTopic topics) => _topics != null && _topics.Matches(type, topics);

        // ------------------------------------------------------------------ internals

        /// <summary>The peer's holder for the scheduler's fast path (lock-free; null when unknown).</summary>
        internal PeerAttention Holder(PeerId peer) => Volatile.Read(ref _peers[peer.Value]);

        private PeerAttention GetOrCreateLocked(PeerId peer)
        {
            PeerAttention holder = _peers[peer.Value];
            if (holder != null) return holder;
            holder = new PeerAttention(peer);
            Volatile.Write(ref _peers[peer.Value], holder);
            RebuildPeerListLocked();
            return holder;
        }

        private void RebuildPeerListLocked()
        {
            var list = new List<PeerId>();
            for (int i = 0; i < _peers.Length; i++)
            {
                if (_peers[i] != null) list.Add(new PeerId((byte)i));
            }
            Volatile.Write(ref _peerList, list.Count == 0 ? NoPeers : list.AsReadOnly());
        }

        private static bool IsSane(Protocol.ViewReport r)
        {
            return Coordinate(r.PivotX) && Coordinate(r.PivotZ) && Coordinate(r.CamX) && Coordinate(r.CamY) && Coordinate(r.CamZ)
                   && SchedulerSettings.IsFinite(r.Yaw) && SchedulerSettings.IsFinite(r.Zoom) && r.Zoom >= 0f && r.Zoom <= MaxCoordinate;
        }

        private static bool Coordinate(float v) => SchedulerSettings.IsFinite(v) && v >= -MaxCoordinate && v <= MaxCoordinate;

        private static bool SameView(Protocol.ViewReport a, Protocol.ViewReport b)
        {
            return a.PivotX == b.PivotX && a.PivotZ == b.PivotZ && a.Zoom == b.Zoom && a.Yaw == b.Yaw && a.CamX == b.CamX && a.CamY == b.CamY
                   && a.CamZ == b.CamZ && a.Infoview == b.Infoview && a.InfomodeMask == b.InfomodeMask && a.Selected == b.Selected
                   && a.Followed == b.Followed && a.PanelFlags == b.PanelFlags && a.PanelTarget == b.PanelTarget;
        }

        private static Protocol.ViewReport Copy(Protocol.ViewReport from, Protocol.ViewReport to)
        {
            to.PivotX = from.PivotX; to.PivotZ = from.PivotZ; to.Zoom = from.Zoom; to.Yaw = from.Yaw;
            to.CamX = from.CamX; to.CamY = from.CamY; to.CamZ = from.CamZ;
            to.Infoview = from.Infoview; to.InfomodeMask = from.InfomodeMask;
            to.Selected = from.Selected; to.Followed = from.Followed;
            to.PanelFlags = from.PanelFlags; to.PanelTarget = from.PanelTarget;
            return to;
        }

        private static double Distance(float ax, float az, float bx, float bz)
        {
            double dx = (double)ax - bx, dz = (double)az - bz;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        private void Warn(double now, string message)
        {
            ILog log = Log;
            if (log == null || !log.IsEnabled(LogLevel.Warn)) return;
            lock (_lock)
            {
                if (SchedulerSettings.IsFinite(now) && now - _lastWarn < WarnIntervalSeconds && now >= _lastWarn)
                {
                    _suppressedWarnings++;
                    return;
                }
                if (SchedulerSettings.IsFinite(now)) _lastWarn = now;
                if (_suppressedWarnings > 0)
                {
                    message += " (" + _suppressedWarnings.ToString(CultureInfo.InvariantCulture) + " similar warning(s) suppressed)";
                    _suppressedWarnings = 0;
                }
            }
            log.Write(LogLevel.Warn, LogCategory.Scheduling, message);
        }

        private void LogDebug(string message)
        {
            ILog log = Log;
            if (log != null && log.IsEnabled(LogLevel.Debug)) log.Write(LogLevel.Debug, LogCategory.Scheduling, message);
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
