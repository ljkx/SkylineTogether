using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Notifications;
using Game.Pathfind;
using Game.Routes;
using Game.UI.InGame;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// The UI-level line check (ARCH §4.11.6, the user's main complaint): per transport line, one
    /// <see cref="LineStateRow"/> of exactly what the vanilla line UI shows, computed with the UI's own functions on both
    /// sides: the stop count (<c>TransportUIUtils.GetStopCount</c>), the length (<c>GetRouteLength</c>), schedule and
    /// active state (<c>Route</c> options as in <c>BuildTransportLine</c>), <c>Route.m_Flags</c> (complete), the broken
    /// segments as the line diagram draws them (<c>PathInformation.m_Origin</c> and <c>m_Destination</c> both null,
    /// <c>LineVisualizerSection.cs:580-585</c>), and the hashes of the default-layer icons on the line and on its
    /// waypoints ("path not found", "not enough vehicles"). The host broadcasts its rows every
    /// <see cref="HostInterval"/> seconds; the client compares them with its own (<see cref="LineStateReport.Compare"/>)
    /// right after the batch that carried them was applied, and <see cref="LineCheckTracker"/> decides which lines to
    /// re-send (route, waypoints, segments) and which to tolerate. Every confirmed difference is logged.
    /// <para>
    /// Not compared: vehicles, cargo and capacity (moving agents are not replicated in this version) and
    /// <c>TransportLine.m_Flags</c>: <c>TransportLine</c> references the line's pending vehicle request, which is never
    /// replicated, so the component travels as presence only while a request is pending; its visible effect, the
    /// "not enough vehicles" warning, is compared through the icon hash.
    /// </para>
    /// </summary>
    public sealed class LineCheck
    {
        /// <summary>Seconds between the host's row broadcasts.</summary>
        public const double HostInterval = 10;

        private readonly ILog _log;
        private readonly LineCheckTracker _tracker = new LineCheckTracker();
        private readonly LineCheckTracker.Outcome _outcome = new LineCheckTracker.Outcome();
        private readonly List<uint> _resends = new List<uint>();
        private readonly List<uint> _taken = new List<uint>();
        private readonly ByteWriter _bytes = new ByteWriter(4096);
        private readonly List<PrefabRank> _ranks = new List<PrefabRank>();
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private EntityQuery _lines;
        private double _lastHost = -1000;
        private int _lastCount = -1;
        private int _lastDiffering;
        private int _lastTolerated;

        public LineCheck(ILog log)
        {
            _log = log;
        }

        public void Attach(EntityCodec codec, NetIdSystem ids)
        {
            _codec = codec;
            _ids = ids;
            _lines = codec.EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Route>(), ComponentType.ReadOnly<TransportLine>(), ComponentType.ReadOnly<RouteWaypoint>(), ComponentType.ReadOnly<RouteSegment>() },
                None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>(), ComponentType.ReadOnly<Game.Common.Deleted>() },
            });
        }

        public void Reset()
        {
            _tracker.Clear();
            _resends.Clear();
            _lastHost = -1000;
            _lastCount = -1;
            _lastDiffering = 0;
            _lastTolerated = 0;
        }

        /// <summary>Client: "12 lines match" / "1 of 12 lines differs"; empty before the first comparison (host: empty).</summary>
        public string Status
        {
            get
            {
                if (_lastCount < 0) return "";
                if (_lastDiffering == 0) return _lastCount.ToString(CultureInfo.InvariantCulture) + (_lastCount == 1 ? " line matches" : " lines match");
                string s = _lastDiffering.ToString(CultureInfo.InvariantCulture) + " of " + _lastCount.ToString(CultureInfo.InvariantCulture) + " lines differ";
                if (_lastTolerated > 0) s += " (" + _lastTolerated.ToString(CultureInfo.InvariantCulture) + " unresolved)";
                return s;
            }
        }

        /// <summary>Host: the rows into the broadcast batch every <see cref="HostInterval"/> seconds.</summary>
        public void HostCapture(double now, SyncBatch.Writer batch)
        {
            if (_codec == null || now - _lastHost < HostInterval) return;
            _lastHost = now;
            List<LineStateRow> rows = ComputeRows();
            if (rows.Count > LineStateReport.MaxRows) rows.RemoveRange(LineStateReport.MaxRows, rows.Count - LineStateReport.MaxRows);
            _bytes.Reset();
            LineStateReport.WriteRows(_bytes, rows);
            batch.LineRows(_bytes.Buffer, 0, _bytes.Length);
        }

        /// <summary>Client: the host's rows arrived (after everything before them was applied).</summary>
        public void OnHostRows(byte[] payload, double now, bool compare)
        {
            if (_codec == null || payload == null || !compare) return;
            List<LineStateRow> host = LineStateReport.ReadRows(new ByteReader(payload, 0, payload.Length));
            List<LineStateRow> mine = ComputeRows();
            IReadOnlyList<LineStateDifference> diffs = LineStateReport.Compare(host, mine);
            _tracker.Observe(diffs, now, _outcome);
            _lastCount = Math.Max(host.Count, mine.Count);
            _lastDiffering = _outcome.Differing;
            _lastTolerated = _outcome.Tolerated;
            foreach (string line in _outcome.Log) _log.Info(LogCategory.Integrity, line);
            foreach (uint line in _outcome.Resend)
            {
                if (!_resends.Contains(line)) _resends.Add(line);
            }
            if (_outcome.Fixed > 0) _log.Info(LogCategory.Integrity, _outcome.Fixed + " re-sent line(s) match the host now");
        }

        /// <summary>Client: lines to re-send (consumed).</summary>
        public List<uint> TakeResends()
        {
            _taken.Clear();
            _taken.AddRange(_resends);
            _resends.Clear();
            return _taken;
        }

        /// <summary>One row per bound transport line, as the vanilla line UI computes it.</summary>
        public List<LineStateRow> ComputeRows()
        {
            var rows = new List<LineStateRow>();
            EntityManager em = _codec.EntityManager;
            NativeArray<Entity> lines = _lines.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity e in lines)
                {
                    uint id = _ids.GetNetId(e);
                    if (id == 0 || !NetSlot.TryCreate(id, out NetSlot slot)) continue;
                    try
                    {
                        rows.Add(Row(em, e, slot));
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(LogCategory.Integrity, "line row of #" + id + " failed: " + ex.Message);
                    }
                }
            }
            finally
            {
                lines.Dispose();
            }
            return rows;
        }

        private LineStateRow Row(EntityManager em, Entity e, NetSlot slot)
        {
            Route route = em.GetComponentData<Route>(e);
            var row = new LineStateRow
            {
                Line = slot,
                Stops = TransportUIUtils.GetStopCount(em, e),
                LengthDecimetres = LineStateRow.LengthToDecimetres(TransportUIUtils.GetRouteLength(em, e)),
                Schedule = (byte)(RouteUtils.CheckOption(route, RouteOption.Day) ? RouteSchedule.Day : RouteUtils.CheckOption(route, RouteOption.Night) ? RouteSchedule.Night : RouteSchedule.DayAndNight),
                Active = !RouteUtils.CheckOption(route, RouteOption.Inactive),
                RouteFlags = (uint)route.m_Flags,
                IconsCompared = true,
            };
            DynamicBuffer<RouteSegment> segments = em.GetBuffer<RouteSegment>(e, true);
            for (int i = 0; i < segments.Length; i++)
            {
                Entity segment = segments[i].m_Segment;
                if (!em.HasComponent<PathInformation>(segment)) continue;
                PathInformation path = em.GetComponentData<PathInformation>(segment);
                if (path.m_Origin == Entity.Null && path.m_Destination == Entity.Null) row.MarkSegmentBroken(i);
            }
            _ranks.Clear();
            AddIconRanks(em, e);
            row.LineIconHash = LineStateRow.IconSetHash(_ranks);
            _ranks.Clear();
            DynamicBuffer<RouteWaypoint> waypoints = em.GetBuffer<RouteWaypoint>(e, true);
            for (int i = 0; i < waypoints.Length; i++) AddIconRanks(em, waypoints[i].m_Waypoint);
            row.WaypointIconHash = LineStateRow.IconSetHash(_ranks);
            return row;
        }

        /// <summary>The notification prefabs (as net ids) of the owner's default-layer icons, as the notifications section lists them.</summary>
        private void AddIconRanks(EntityManager em, Entity owner)
        {
            if (owner == Entity.Null || !em.HasBuffer<IconElement>(owner)) return;
            DynamicBuffer<IconElement> icons = em.GetBuffer<IconElement>(owner, true);
            for (int i = 0; i < icons.Length; i++)
            {
                Entity icon = icons[i].m_Icon;
                if (!em.Exists(icon) || em.HasComponent<Game.Common.Deleted>(icon) || em.HasComponent<Game.Tools.Temp>(icon) || !em.HasComponent<Icon>(icon)) continue;
                if (em.GetComponentData<Icon>(icon).m_ClusterLayer != IconClusterLayer.Default) continue;
                uint prefab = em.HasComponent<Game.Prefabs.PrefabRef>(icon) ? _ids.GetNetId(em.GetComponentData<Game.Prefabs.PrefabRef>(icon).m_Prefab) : 0;
                _ranks.Add(new PrefabRank(prefab > int.MaxValue ? -1 : (int)prefab));
            }
        }
    }
}
