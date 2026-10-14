using System;
using System.Collections.Generic;
using System.Reflection;
using Game.City;
using Game.Simulation;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Session;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// City statistics history (the statistics panel's charts, ARCH §2.2, §4.10.6; R14 §6.2, R11 §5.2). The host's
    /// <c>CityStatisticsSystem</c> samples every 8192 ticks into <c>CityStatistic</c> buffers on per-statistic entities;
    /// clients do not simulate, so their charts would stop at the join.
    /// <para>
    /// Safe path: <c>PostDeserialize</c> is never called (it can destroy or create statistic entities), and neither is
    /// <c>Deserialize</c> (it replaces the pending event queue). Series travel keyed by (statistic type, parameter), and a
    /// client writes them into the buffers of the entities its own lookup (<c>GetLookup()</c>, built at load) names; a key
    /// it does not have is skipped, so no entity is ever created or destroyed. The sample counter and the last sample
    /// frame (private fields, which date the samples in the chart) are set by reflection, then
    /// <c>eventStatisticsUpdated</c> refreshes the panel exactly like a local sample would.
    /// </para>
    /// The host polls once a second and broadcasts the series that changed (normally right after a sample). loadspeed: a
    /// peer that goes live gets only the series that changed since its admission (its snapshot, taken after the admission,
    /// carries the rest; the series' hashes at the admission are its baseline), instead of every series again; without a
    /// baseline it gets all of them as before. Main thread only.
    /// </summary>
    public sealed class StatisticsSync
    {
        private const double PollInterval = 1.0;
        private const double FullCompareInterval = 30.0;

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Dictionary<long, ulong> _sentHashes = new Dictionary<long, ulong>();
        private readonly Queue<byte[]> _inbox = new Queue<byte[]>();
        private readonly List<PeerId> _newPeers = new List<PeerId>();
        /// <summary>loadspeed: per joining peer, the series' content hashes at its admission (what its snapshot holds).</summary>
        private readonly Dictionary<byte, Dictionary<long, ulong>> _joinBaselines = new Dictionary<byte, Dictionary<long, ulong>>();
        private readonly ByteWriter _out = new ByteWriter(16 * 1024);
        private World _world;
        private CityStatisticsSystem _stats;
        private FieldInfo _sampleCount, _lastSampleFrame;
        private bool _warnedFields;
        private int _lastCount = int.MinValue;
        private uint _lastFrame;
        private double _nextPoll, _nextFull;
        private long _sent, _applied;

        public StatisticsSync(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            sessions.HostStarted += host =>
            {
                Reset();
                host.PeerLive += peer => _newPeers.Add(peer);
                host.PeerJoining += OnPeerJoining;
                host.PeerLeft += peer => _joinBaselines.Remove(peer.Value);
            };
            sessions.ClientStarted += client =>
            {
                Reset();
                client.AppMessage += e =>
                {
                    if (e.Kind != MessageKind.Statistics) return;
                    // w8/robust: with the feature off nothing drains the inbox (it grew for the rest of the session); the
                    // host's next keyframe or a rejoin (which re-arms the feature) brings the state back
                    if (!Diagnostics.Features.SimValues.IsAvailable) return;
                    var copy = new byte[e.BodyCount];
                    Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                    _inbox.Enqueue(copy);
                };
            };
            sessions.SessionEnded += Reset;
        }

        private bool IsHost => _sessions.Host != null;

        private bool IsLiveClient => _sessions.Client != null && _sessions.Client.State == ClientState.Live;

        private static double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;

        public string Status
        {
            get
            {
                if (IsHost && _sent > 0) return "statistics: " + _sent + " updates sent";
                if (IsLiveClient && _applied > 0) return "statistics: " + _applied + " updates applied";
                return "";
            }
        }

        private void Reset()
        {
            _sentHashes.Clear();
            _inbox.Clear();
            _newPeers.Clear();
            _joinBaselines.Clear();
            _lastCount = int.MinValue;
            _lastFrame = 0;
            _nextPoll = _nextFull = 0;
            _sent = _applied = 0;
        }

        private bool EnsureWorld()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;
            if (_world == world) return _stats != null;
            _world = world;
            _stats = world.GetOrCreateSystemManaged<CityStatisticsSystem>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            _sampleCount = typeof(CityStatisticsSystem).GetField("m_SampleCount", flags);
            _lastSampleFrame = typeof(CityStatisticsSystem).GetField("m_LastSampleFrameIndex", flags);
            if ((_sampleCount == null || _sampleCount.FieldType != typeof(int) || _lastSampleFrame == null || _lastSampleFrame.FieldType != typeof(uint)) && !_warnedFields)
            {
                _warnedFields = true;
                _log.Warn(LogCategory.Replication, "CityStatisticsSystem sample counter fields not found; client charts get the host's values but may be dated by their own counter");
                if (_sampleCount != null && _sampleCount.FieldType != typeof(int)) _sampleCount = null;
                if (_lastSampleFrame != null && _lastSampleFrame.FieldType != typeof(uint)) _lastSampleFrame = null;
            }
            return _stats != null;
        }

        private static long Key(int type, int parameter) => ((long)type << 32) | (uint)parameter;

        // ================================================================== host

        /// <summary>Host frame end: polls the sample counter and broadcasts changed series.</summary>
        public void CaptureOutgoing()
        {
            if (!IsHost) return;
            try
            {
                if (!EnsureWorld()) return;
                SessionHost host = _sessions.Host;
                if (_newPeers.Count > 0)
                {
                    List<StatisticSeries> all = Collect();
                    foreach (PeerId peer in _newPeers)
                    {
                        // loadspeed: only what changed since the peer's snapshot (usually nothing: the simulation was held)
                        List<StatisticSeries> send = all;
                        if (_joinBaselines.TryGetValue(peer.Value, out Dictionary<long, ulong> baseline))
                        {
                            send = new List<StatisticSeries>();
                            foreach (StatisticSeries s in all)
                            {
                                if (!baseline.TryGetValue(Key(s.Type, s.Parameter), out ulong hash) || hash != s.ContentHash()) send.Add(s);
                            }
                            _joinBaselines.Remove(peer.Value);
                        }
                        _log.Info(LogCategory.Replication, peer + ": " + send.Count + " of " + all.Count + " statistics series sent at live"
                                                           + (send == all ? "" : " (the rest is in its snapshot)"));
                        if (send.Count == 0) continue;
                        Encode(send == all ? new List<StatisticSeries>(all) : send);
                        host.SendTo(peer, MessageKind.Statistics, _out.Buffer, 0, _out.Length);
                    }
                    _newPeers.Clear();
                }
                double now = Now;
                if (now < _nextPoll) return;
                _nextPoll = now + PollInterval;
                int count = _stats.sampleCount;
                uint frame = _lastSampleFrame != null ? (uint)_lastSampleFrame.GetValue(_stats) : 0;
                bool sampled = count != _lastCount || frame != _lastFrame;
                if (!sampled && now < _nextFull) return;
                _nextFull = now + FullCompareInterval;
                List<StatisticSeries> series = Collect();
                var changed = new List<StatisticSeries>();
                foreach (StatisticSeries s in series)
                {
                    long key = Key(s.Type, s.Parameter);
                    ulong hash = s.ContentHash();
                    if (_sentHashes.TryGetValue(key, out ulong old) && old == hash) continue;
                    _sentHashes[key] = hash;
                    changed.Add(s);
                }
                bool first = _lastCount == int.MinValue;
                _lastCount = count;
                _lastFrame = frame;
                // the first poll only records the baseline (the join snapshots carry it)
                if (first || (changed.Count == 0 && !sampled)) return;
                Encode(changed);
                host.Broadcast(MessageKind.Statistics, _out.Buffer, 0, _out.Length);
                _sent++;
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Replication, "statistics capture failed", ex);
            }
        }

        /// <summary>loadspeed: the series' hashes now are what the joiner's snapshot (taken after its admission) holds.</summary>
        private void OnPeerJoining(PeerId peer)
        {
            try
            {
                if (!EnsureWorld()) return;
                var baseline = new Dictionary<long, ulong>();
                foreach (StatisticSeries s in Collect()) baseline[Key(s.Type, s.Parameter)] = s.ContentHash();
                _joinBaselines[peer.Value] = baseline;
            }
            catch (Exception ex)
            {
                _joinBaselines.Remove(peer.Value);
                _log.Warn(LogCategory.Replication, "statistics baseline for " + peer + " failed; it gets every series when live", ex);
            }
        }

        private void Encode(List<StatisticSeries> series)
        {
            _out.Reset();
            uint frame = _lastSampleFrame != null ? (uint)_lastSampleFrame.GetValue(_stats) : 0;
            if (series.Count > StatisticsMessage.MaxSeries) series.RemoveRange(StatisticsMessage.MaxSeries, series.Count - StatisticsMessage.MaxSeries);
            StatisticsMessage.Write(_out, _stats.sampleCount, frame, series);
        }

        /// <summary>Every series of the host's lookup (writers completed first).</summary>
        private List<StatisticSeries> Collect()
        {
            var list = new List<StatisticSeries>();
            _stats.CompleteWriters();
            EntityManager em = _world.EntityManager;
            NativeParallelHashMap<CityStatisticsSystem.StatisticsKey, Entity> lookup = _stats.GetLookup();
            if (!lookup.IsCreated || lookup.IsEmpty) return list;
            NativeKeyValueArrays<CityStatisticsSystem.StatisticsKey, Entity> pairs = lookup.GetKeyValueArrays(Allocator.Temp);
            try
            {
                for (int i = 0; i < pairs.Length; i++)
                {
                    Entity e = pairs.Values[i];
                    if (e == Entity.Null || !em.Exists(e) || !em.HasBuffer<CityStatistic>(e)) continue;
                    DynamicBuffer<CityStatistic> buffer = em.GetBuffer<CityStatistic>(e, true);
                    int n = Math.Min(buffer.Length, StatisticsMessage.MaxSamples);
                    // w8/robust: the newest samples (the buffers grow without end; the charts read from the end and pad
                    // the front when a buffer is shorter than the sample count, StatisticsUISystem.EnsureDataSize)
                    int start = buffer.Length - n;
                    var s = new StatisticSeries { Type = (int)pairs.Keys[i].type, Parameter = pairs.Keys[i].parameter, Values = new double[n], Totals = new double[n] };
                    for (int k = 0; k < n; k++)
                    {
                        s.Values[k] = buffer[start + k].m_Value;
                        s.Totals[k] = buffer[start + k].m_TotalValue;
                    }
                    list.Add(s);
                }
            }
            finally
            {
                pairs.Dispose();
            }
            // deterministic order (diagnostics; the receiver is order-insensitive)
            list.Sort((a, b) => a.Type != b.Type ? a.Type.CompareTo(b.Type) : a.Parameter.CompareTo(b.Parameter));
            return list;
        }

        // ================================================================== client

        /// <summary>Client frame start: writes received series into the local statistic entities.</summary>
        public void ApplyIncoming()
        {
            if (!IsLiveClient)
            {
                if (_sessions.Client == null) _inbox.Clear();
                return;
            }
            if (_inbox.Count == 0) return;
            try
            {
                if (!EnsureWorld()) return;
                while (_inbox.Count > 0)
                {
                    byte[] data = _inbox.Dequeue();
                    try
                    {
                        Apply(data);
                    }
                    catch (DecodeException ex)
                    {
                        _log.Warn(LogCategory.Replication, "dropped a malformed statistics message: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Replication, "applying statistics failed", ex);
            }
        }

        private void Apply(byte[] data)
        {
            List<StatisticSeries> series = StatisticsMessage.Read(data, 0, data.Length, out int sampleCount, out uint lastFrame);
            EntityManager em = _world.EntityManager;
            NativeParallelHashMap<CityStatisticsSystem.StatisticsKey, Entity> lookup = _stats.GetLookup();
            if (!lookup.IsCreated) return;
            _stats.CompleteWriters();
            int written = 0, missing = 0;
            foreach (StatisticSeries s in series)
            {
                var key = new CityStatisticsSystem.StatisticsKey((StatisticType)s.Type, s.Parameter);
                if (!lookup.TryGetValue(key, out Entity e) || e == Entity.Null || !em.Exists(e) || !em.HasBuffer<CityStatistic>(e))
                {
                    missing++;
                    continue;
                }
                DynamicBuffer<CityStatistic> buffer = em.GetBuffer<CityStatistic>(e);
                int n = Math.Min(s.Values.Length, s.Totals.Length);
                buffer.ResizeUninitialized(n);
                for (int k = 0; k < n; k++) buffer[k] = new CityStatistic { m_Value = Finite(s.Values[k]), m_TotalValue = Finite(s.Totals[k]) }; // w8/robust
                written++;
            }
            // w8/robust: the charts allocate this many samples (EnsureDataSize); a wild value is clamped
            sampleCount = Math.Max(0, Math.Min(MaxSampleCount, sampleCount));
            try
            {
                _sampleCount?.SetValue(_stats, sampleCount);
                _lastSampleFrame?.SetValue(_stats, lastFrame);
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Replication, "statistics counters not set: " + ex.Message);
            }
            if (missing > 0) _log.Debug(LogCategory.Replication, "statistics: " + missing + " series have no local statistic entity (skipped)");
            _applied++;
            if (written > 0) _stats.eventStatisticsUpdated?.Invoke();
        }

        /// <summary>w8/robust: the largest sample count accepted from the host (32 samples a game day).</summary>
        private const int MaxSampleCount = 1 << 20;

        private static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;
    }
}
