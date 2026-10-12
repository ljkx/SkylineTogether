using System.Threading;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// The immutable result of one accepted view report: a private copy of the report, its topics and the interest regions
    /// derived from it. Published by reference, so readers always see a consistent combination.
    /// </summary>
    internal sealed class ViewState
    {
        public readonly Protocol.ViewReport Report;
        public readonly double Time;
        public readonly AttentionTopic Topics;
        public readonly float BaseRadius;
        public readonly float RadiusScale;
        public readonly MotionInterestRegion Motion;    // with the ladder's radius scale
        public readonly MotionInterestRegion View;      // unscaled (what the camera shows)

        public ViewState(SchedulerSettings s, Protocol.ViewReport report, double time, float radiusScale)
        {
            Report = report;
            Time = time;
            Topics = TopicMap.FromView(report);
            BaseRadius = ViewGeometry.BaseRadius(s, report.Zoom);
            RadiusScale = radiusScale;
            View = ViewGeometry.Build(s, report, 1f);
            Motion = radiusScale >= 0.9999f ? View : ViewGeometry.Build(s, report, radiusScale);
        }
    }

    /// <summary>
    /// Everything <see cref="AttentionSets"/> knows about one peer. Writers serialise on the owner's lock; readers on any
    /// thread read the volatile references (immutable sets and view states) and the atomic motion bitset without locks.
    /// </summary>
    internal sealed class PeerAttention
    {
        public readonly PeerId Peer;
        public readonly AtomicSlotBits Motion = new AtomicSlotBits();

        private volatile ViewState _view;
        private volatile SlotSet _inView = SlotSet.Empty;
        private volatile SlotSet _closure = SlotSet.Empty;
        private volatile SlotSet _topicSlots = SlotSet.Empty;
        private volatile float _radiusScale = 1f;
        private int _viewVersion, _selectionVersion, _topicsVersion;

        // writer-side bookkeeping (under the owner's lock)
        public double InViewRefreshedAt = double.NaN;
        public ViewState ViewAtRefresh;
        public double LastReportTime = double.NaN;

        public PeerAttention(PeerId peer)
        {
            Peer = peer;
        }

        public ViewState View { get => _view; set => _view = value; }

        public SlotSet InView { get => _inView; set => _inView = value ?? SlotSet.Empty; }

        public SlotSet Closure { get => _closure; set => _closure = value ?? SlotSet.Empty; }

        public SlotSet TopicSlots { get => _topicSlots; set => _topicSlots = value ?? SlotSet.Empty; }

        public float RadiusScale { get => _radiusScale; set => _radiusScale = value; }

        public int ViewVersion => Volatile.Read(ref _viewVersion);

        public int SelectionVersion => Volatile.Read(ref _selectionVersion);

        public int TopicsVersion => Volatile.Read(ref _topicsVersion);

        public void BumpView() => Interlocked.Increment(ref _viewVersion);

        public void BumpSelection() => Interlocked.Increment(ref _selectionVersion);

        public void BumpTopics() => Interlocked.Increment(ref _topicsVersion);
    }
}
