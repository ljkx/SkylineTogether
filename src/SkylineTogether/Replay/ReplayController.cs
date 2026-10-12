using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Colossal.PSI.Common;
using Colossal.PSI.Environment;
using Game.City;
using Game.Rendering;
using Game.Simulation;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Replay;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;
using SkylineTogether.Settings;
using SkylineTogether.Sync;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylineTogether.Replay
{
    /// <summary>One recording folder as the panel lists it.</summary>
    public sealed class RecordingInfo
    {
        public string Id = "";
        public string Folder = "";
        public string City = "";
        public DateTime Created;
        public double Seconds = -1;
        public long Bytes;
        public string Verdict = "";
    }

    /// <summary>
    /// Solo testing (the record/replay verdict, ARCH §13.5) in the game.
    /// <para>
    /// <b>Record test session</b> (host): a <see cref="RecorderPeer"/> joins the running session (or a solo test host
    /// without any network part) like a real player and records everything it receives into
    /// <c>ModsData/SkylineTogether/Recordings/&lt;time&gt; &lt;city&gt;/</c> (stream, snapshot, summary). At host frame
    /// ends, when the recorder is up to date (no deferred values waiting for it), verification checkpoints go into its
    /// stream only: the integrity hash of every bucket (the rolling verification's own hasher), the members of every
    /// entity whose hash changed since the previous checkpoint, then a summary with the UI-level line rows, the icon
    /// digests per owner and the hashes of the global channels as last sent. The first checkpoint follows soon after the
    /// recorder went live, then one per <see cref="CheckpointInterval"/>, and a final one when the player stops.
    /// </para>
    /// <para>
    /// <b>Replay &amp; verify</b> (no session): the recording plays into the normal client path through a
    /// <see cref="PlaybackTransport"/> (the real snapshot loader with its join-asset splitter, follower gate and clock,
    /// movers, the replication receive path). The <see cref="ReplayChecker"/> compares every checkpoint right after its
    /// batch was applied; at the end the verdict is written next to the recording, summarised in the panel, and the
    /// session ends like a normal leave (gates and clock restored, the player keeps the replica as a single-player city).
    /// </para>
    /// Main thread: <see cref="Tick"/> from the session driver, <see cref="OnCaptureEnd"/> after the frame's capture.
    /// </summary>
    public sealed class ReplayController
    {
        /// <summary>Recorded seconds between the recorder going live and the first checkpoint.</summary>
        public const double FirstCheckpointDelay = 10;

        /// <summary>Recorded seconds between checkpoints.</summary>
        public const double CheckpointInterval = 60;

        private const double CheckpointBudgetSeconds = 0.002;
        private const int MinBucketsPerFrame = 4;
        private const int MaxBatchBytes = 256 * 1024;
        private const int MaxBacklogBatches = 64;
        private const double ReplaySettleSeconds = 3;
        private const double EndedBeforeLiveSeconds = 30;
        private const int MaxListed = 30;

        private readonly SessionManager _sessions;
        private readonly ILog _log;

        // ---------------------------------------------------------------- recording
        private RecorderPeer _recorder;
        private RecordingWriter _writer;
        private string _recordFolder;
        private DateTime _recordStartedUtc;
        private bool _soloHost;
        private int _addedSlot;
        private bool _stopRequested;
        private bool _finalCheckpointDone;
        private string _recordStopReason = "";
        private readonly CheckpointMemberTracker _tracker = new CheckpointMemberTracker();
        private readonly List<EntityHash> _bucketHashes = new List<EntityHash>(EntityDigest.BucketSize);
        private readonly List<EntityHash> _changed = new List<EntityHash>(EntityDigest.BucketSize);
        private readonly List<ComponentHash> _members = new List<ComponentHash>(64);
        private readonly SyncBatch.Writer _cpBatch = new SyncBatch.Writer();
        private uint _cpIndex;
        private bool _cpActive;
        private bool _cpFinal;
        private uint _cpBucket;
        private uint _cpEnd;
        private long _cpEntities;
        private long _cpMembers;
        private double _nextCheckpointAt = -1;
        private int _checkpointsWritten;

        // ---------------------------------------------------------------- replay
        private PlaybackTransport _playback;
        private ReplayVerdict _verdict;
        private ReplayChecker _checker;
        private string _replayFolder;
        private double _replayStartedAt;
        private double _replayLiveAt = -1;
        private double _replayFinishedAt = -1;
        private double _endedBeforeLiveAt = -1;
        private bool _replayDone = true;

        private List<RecordingInfo> _recordings;
        private SimulationSystem _simulation;
        private CameraUpdateSystem _camera;

        public ReplayController(SessionManager sessions, ILog log)
        {
            _sessions = sessions;
            _log = log;
            Instance = this;
            sessions.ReplayEnded += OnReplayEnded;
        }

        /// <summary>The instance created by <see cref="Mod"/> (null before the mod loaded).</summary>
        public static ReplayController Instance { get; private set; }

        /// <summary>Changes whenever something the panel shows changed (UI refresh key).</summary>
        public int Version { get; private set; }

        public bool Recording => _recorder != null;

        public bool RecordingStopping => _recorder != null && _stopRequested;

        public bool Replaying => !_replayDone;

        /// <summary>A recording can start: hosting (a recorder is added), or a city without a session (a solo test host).</summary>
        public bool CanRecord => _recorder == null && _replayDone && ((_sessions.Host != null && _sessions.HostTransport != null) || _sessions.CanHostSolo);

        public bool CanReplay => _recorder == null && _replayDone && _sessions.CanReplay;

        /// <summary>The recordings root: <c>ModsData/SkylineTogether/Recordings</c> in the game's user data folder.</summary>
        public static string Root
        {
            get
            {
                string user;
                try
                {
                    user = EnvPath.kUserDataPath;
                }
                catch (Exception)
                {
                    user = null;
                }
                if (string.IsNullOrEmpty(user)) user = UnityEngine.Application.persistentDataPath;
                return Path.Combine(user, "ModsData", "SkylineTogether", "Recordings");
            }
        }

        /// <summary>The panel's line about the running recording ("" when none).</summary>
        public string RecordStatus
        {
            get
            {
                if (_recorder == null) return "";
                SessionClient c = _recorder.Client;
                if (_stopRequested) return LocaleSource.Resolve("st.replay.status.stopping");
                if (c.State != ClientState.Live) return LocaleSource.Resolve("st.replay.status.joining");
                return LocaleSource.Resolve("st.replay.status.recording", "TIME", Duration(_recorder.Seconds), "MB", Mb(_writer.BytesWritten), "N", _checkpointsWritten.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>The panel's line about the running replay ("" when none).</summary>
        public string ReplayStatus
        {
            get
            {
                if (_replayDone || _playback == null) return "";
                SessionClient c = _sessions.Client;
                if (c == null || c.State != ClientState.Live) return LocaleSource.Resolve("st.replay.status.loading");
                return LocaleSource.Resolve("st.replay.status.playing", "TIME", Duration(_playback.DeliveredSeconds), "N", (_checker?.CheckpointsCompleted ?? 0).ToString(CultureInfo.InvariantCulture),
                    "DIFF", (_verdict?.Persistent.Count ?? 0).ToString(CultureInfo.InvariantCulture));
            }
        }

        public float ReplayProgress => _playback == null || _replayDone ? 0f : (float)_playback.Progress;

        /// <summary>The last verdict (status, one-line summary, folder) or null.</summary>
        public ReplayVerdict LastVerdict { get; private set; }

        public string LastVerdictFolder { get; private set; } = "";

        // ================================================================== frame

        /// <summary>Once per rendered frame, after the session's tick.</summary>
        public void Tick()
        {
            try
            {
                TickRecording();
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Session, "test recording", ex);
                AbortRecording("the recording failed: " + ex.Message);
            }
            try
            {
                TickReplay();
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Session, "replay", ex);
            }
        }

        /// <summary>After the frame's capture (both roles): the host's checkpoints, the replay's icon comparison.</summary>
        public void OnCaptureEnd()
        {
            try
            {
                if (_recorder != null) ProduceCheckpoint();
                if (!_replayDone) _checker?.FrameEnd();
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Integrity, "solo testing checkpoint", ex);
            }
        }

        /// <summary>The game is closing: the recording is kept as far as it got, a replay gets its verdict.</summary>
        public void Shutdown()
        {
            try
            {
                if (_recorder != null) AbortRecording("the game was closed");
                if (!_replayDone) FinishReplay("the game was closed");
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "closing the solo test failed", ex);
            }
        }

        // ================================================================== recording

        /// <summary>Starts recording: adds the recorder to the running host session, or hosts a solo test session first.</summary>
        public void StartRecording()
        {
            if (!CanRecord)
            {
                SessionManager.Notify(LocaleSource.Resolve("st.toast.record.unavailable"), ProgressState.Warning);
                return;
            }
            bool solo = false;
            if (_sessions.Host == null)
            {
                if (!_sessions.StartSoloHost()) return;
                solo = true;
            }
            SessionHost host = _sessions.Host;
            FileStream stream = null;
            try
            {
                CompositeTransportOrFail(out Core.Transport.CompositeTransport transport);
                string city = CityName();
                _recordStartedUtc = DateTime.UtcNow;
                _recordFolder = UniqueFolder(Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + " " + Sanitize(city)));
                Directory.CreateDirectory(_recordFolder);
                HandshakeDigests digests = _sessions.Info.GetDigests();
                var header = new RecordingHeader
                {
                    ProtocolVersion = digests.ProtocolVersion,
                    ModBuild = digests.ModBuild,
                    GameVersion = digests.GameVersion ?? "",
                    SessionName = host.Options.SessionName ?? "",
                    HostName = host.Options.HostName ?? "",
                    CityName = city,
                    CreatedUtcTicks = _recordStartedUtc.Ticks,
                    RecorderName = LocaleSource.Resolve("st.replay.recorderName"),
                };
                stream = new FileStream(Path.Combine(_recordFolder, RecordingFormat.StreamFileName), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);
                _writer = new RecordingWriter(stream, header);
                string snapshot = Path.Combine(_recordFolder, RecordingFormat.SnapshotFileName);
                // the recorder takes a player slot; it must never keep a real player out
                _addedSlot = 1;
                host.Options.MaxPlayers += 1;
                _recorder = new RecorderPeer(transport, _sessions.Info, _writer, () => new FileStream(snapshot, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20), _sessions.Clock, Frame, _log,
                    new RecorderPeerOptions { Name = header.RecorderName, Password = host.Options.Password ?? "" })
                {
                    View = FillView,
                };
                _soloHost = solo;
                _stopRequested = false;
                _finalCheckpointDone = false;
                _finalAt = -1;
                _recordStopReason = "";
                _tracker.Clear();
                _cpIndex = 0;
                _cpActive = false;
                _nextCheckpointAt = -1;
                _checkpointsWritten = 0;
                _recorder.Start();
                _log.Info(LogCategory.Session, "test recording started in " + _recordFolder);
                SessionManager.Notify(LocaleSource.Resolve("st.toast.record.started"), ProgressState.Progressing);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, "starting the test recording failed", ex);
                if (_writer == null) stream?.Dispose();
                CleanUpRecording();
                if (solo) _sessions.Leave();
                SessionManager.Notify(LocaleSource.Resolve("st.toast.record.failed", "ERROR", ex.Message), ProgressState.Failed);
            }
            Version++;
        }

        private void CompositeTransportOrFail(out Core.Transport.CompositeTransport transport)
        {
            transport = _sessions.HostTransport;
            if (transport == null) throw new InvalidOperationException("the host session has no transport for the recorder");
        }

        /// <summary>Stops the recording after a final checkpoint (or at once when the recorder is not live).</summary>
        public void StopRecording()
        {
            if (_recorder == null || _stopRequested) return;
            _stopRequested = true;
            _recordStopReason = "stopped by the host";
            if (!_recorder.IsLive) _finalCheckpointDone = true;
            Version++;
        }

        private void TickRecording()
        {
            if (_recorder == null) return;
            if (_sessions.Host == null)
            {
                AbortRecording("the host session ended");
                return;
            }
            _recorder.Tick();
            if (!_recorder.Stopping && _recorder.Client.State == ClientState.Ended)
            {
                AbortRecording("the recorder left the session: " + _recorder.Client.EndReason);
                return;
            }
            if (_writer.Failed)
            {
                AbortRecording("writing the recording failed: " + _writer.FailureReason);
                return;
            }
            if (_stopRequested && _finalCheckpointDone && !_recorder.Stopping && Delivered()) _recorder.Stop(_recordStopReason);
            if (_recorder.Stopping && _recorder.Finished) FinishRecording(true);
        }

        /// <summary>
        /// Everything the host queued for the recorder (the final checkpoint included) has arrived and is recorded, or the
        /// wait timed out.
        /// </summary>
        private bool Delivered()
        {
            SessionHost host = _sessions.Host;
            if (host == null || !_recorder.IsLive) return true;
            if (_finalAt < 0) _finalAt = _sessions.Clock.Seconds;
            if (_sessions.Clock.Seconds - _finalAt > 15) return true;
            return _recorder.Client.ApplyAcks.Received >= host.GetEpochSent(_recorder.Peer);
        }

        private double _finalAt = -1;

        /// <summary>The recording ends now (no final checkpoint); what was recorded is kept.</summary>
        private void AbortRecording(string reason)
        {
            if (_recorder == null) return;
            _log.Warn(LogCategory.Session, "test recording ended: " + reason);
            _recordStopReason = reason;
            try
            {
                if (!_recorder.Stopping) _recorder.Stop(reason);
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Session, "stopping the recorder: " + ex.Message);
            }
            FinishRecording(false);
        }

        private void FinishRecording(bool normal)
        {
            string folder = _recordFolder;
            double seconds = _recorder?.Seconds ?? 0;
            long bytes = _writer?.BytesWritten ?? 0;
            long messages = _writer?.Messages ?? 0;
            long snapshotBytes = _recorder?.Loader.SnapshotBytes ?? 0;
            bool solo = _soloHost;
            string reason = _recordStopReason;
            SessionHost host = _sessions.Host;
            try
            {
                WriteManifest(folder, seconds, bytes, messages, snapshotBytes, reason, normal);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "writing the recording summary failed", ex);
            }
            CleanUpRecording();
            if (host != null && _addedSlot > 0) host.Options.MaxPlayers = Math.Max(2, host.Options.MaxPlayers - _addedSlot);
            _addedSlot = 0;
            _recordings = null;
            _log.Info(LogCategory.Session, "test recording saved: " + folder + " (" + Duration(seconds) + ", " + messages + " messages, " + Mb(bytes) + " MB; " + reason + ")");
            SessionManager.Notify(LocaleSource.Resolve(normal ? "st.toast.record.saved" : "st.toast.record.ended", "TIME", Duration(seconds), "REASON", reason ?? ""), normal ? ProgressState.Complete : ProgressState.Warning);
            if (solo && _sessions.Host != null) _sessions.Leave();
            Version++;
        }

        private void CleanUpRecording()
        {
            try
            {
                _recorder?.Dispose();
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Session, "disposing the recorder: " + ex.Message);
            }
            try
            {
                _writer?.Dispose();
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Session, "closing the recording: " + ex.Message);
            }
            _recorder = null;
            _writer = null;
            _cpActive = false;
            _soloHost = false;
            _stopRequested = false;
            _tracker.Clear();
        }

        private void WriteManifest(string folder, double seconds, long bytes, long messages, long snapshotBytes, string reason, bool normal)
        {
            if (string.IsNullOrEmpty(folder) || _writer == null) return;
            RecordingHeader h = _writer.Header;
            var text = new StringWriter(CultureInfo.InvariantCulture);
            var w = new JsonWriter(text, true);
            w.BeginObject();
            w.Name("format"); w.Value((long)RecordingFormat.Version);
            w.Name("city"); w.Value(h.CityName ?? "");
            w.Name("session"); w.Value(h.SessionName ?? "");
            w.Name("host"); w.Value(h.HostName ?? "");
            w.Name("created"); w.Value(new DateTime(h.CreatedUtcTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture));
            w.Name("seconds"); w.Value(Math.Round(seconds, 1));
            w.Name("messages"); w.Value(messages);
            w.Name("bytes"); w.Value(bytes);
            w.Name("snapshotBytes"); w.Value(snapshotBytes);
            w.Name("checkpoints"); w.Value((long)_checkpointsWritten);
            w.Name("gameVersion"); w.Value(h.GameVersion ?? "");
            w.Name("protocol"); w.Value((long)h.ProtocolVersion);
            w.Name("modBuild"); w.Value(h.ModBuild.ToString("X16", CultureInfo.InvariantCulture));
            w.Name("ended"); w.Value(reason ?? "");
            w.Name("normal"); w.Value(normal);
            w.EndObject();
            File.WriteAllText(Path.Combine(folder, RecordingFormat.ManifestFileName), text.ToString(), new UTF8Encoding(false));
        }

        // ---------------------------------------------------------------- checkpoints (host)

        private void ProduceCheckpoint()
        {
            SessionHost host = _sessions.Host;
            SyncController sync = Mod.Sync;
            if (host == null || sync == null || !_recorder.IsLive || _recorder.Stopping) return;
            IntegritySync integrity = sync.Integrity;
            if (!integrity.IsAttached || !Features.Integrity.IsAvailable) return;
            PeerId peer = _recorder.Peer;
            double now = _recorder.Seconds;
            if (!_cpActive)
            {
                if (_nextCheckpointAt < 0) _nextCheckpointAt = now + FirstCheckpointDelay;
                if (!_stopRequested && now < _nextCheckpointAt) return;
                _cpIndex++;
                _cpActive = true;
                _cpFinal = _stopRequested;
                _cpBucket = 0;
                _cpEnd = EntityDigest.BucketOf(Math.Max(1u, integrity.IdLimit) - 1) + 1;
                _cpEntities = 0;
                _cpMembers = 0;
                _cpBatch.Reset();
                _cpBatch.ReplayCheckpoint(_cpIndex, ReplayCheckpointPhase.Begin, Frame(), Array.Empty<byte>(), 0, 0);
                Send(host, peer);
                _writer.Note(now, Frame(), "checkpoint " + _cpIndex + " begins (" + _cpEnd + " buckets)" + (_cpFinal ? ", final" : ""));
            }
            // a checkpoint is comparable only once everything it covers was sent to the recorder (like the rolling digests)
            if (sync.DeferredCount(peer) != 0) return;
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(CheckpointBudgetSeconds * Stopwatch.Frequency);
            int done = 0;
            _cpBatch.Reset();
            while (_cpBucket < _cpEnd && (done < MinBucketsPerFrame || Stopwatch.GetTimestamp() - start < budget))
            {
                uint bucket = _cpBucket++;
                done++;
                integrity.HashBucketForReplay(bucket, _bucketHashes);
                _tracker.Update(bucket, _bucketHashes, _changed);
                foreach (EntityHash h in _changed)
                {
                    if (!integrity.CollectForReplay(h.NetId, _members, out Family family)) continue;
                    _cpBatch.ReplayMembers(h.NetId, family, h.Hash, _members);
                    _cpMembers++;
                }
                _cpBatch.ReplayHashes(_cpIndex, bucket, _bucketHashes);
                _cpEntities += _bucketHashes.Count;
                if (_cpBatch.Length >= MaxBatchBytes) Send(host, peer);
            }
            if (_cpBucket >= _cpEnd) EndCheckpoint(host, peer, integrity, sync, now);
            else Send(host, peer);
        }

        private void EndCheckpoint(SessionHost host, PeerId peer, IntegritySync integrity, SyncController sync, double now)
        {
            var s = new CheckpointSummary
            {
                Index = _cpIndex,
                HostSeconds = now,
                HostFrame = Frame(),
                Buckets = _cpEnd,
                Entities = _cpEntities,
                MembersRecorded = _cpMembers,
                Note = _cpFinal ? "final" : "",
            };
            try
            {
                List<LineStateRow> rows = integrity.Lines.ComputeRows();
                if (rows.Count > LineStateReport.MaxRows) rows.RemoveRange(LineStateReport.MaxRows, rows.Count - LineStateReport.MaxRows);
                s.Lines = rows;
                s.HasLines = true;
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Integrity, "checkpoint line rows failed: " + ex.Message);
            }
            try
            {
                s.HasIcons = integrity.Icons.HostOwnerDigests(s.IconOwnerDigests, out s.IconOwners, out s.IconCount, out s.IconSum);
            }
            catch (Exception ex)
            {
                s.IconOwnerDigests.Clear();
                s.HasIcons = false;
                _log.Debug(LogCategory.Integrity, "checkpoint icon digests failed: " + ex.Message);
            }
            try
            {
                if (Features.GlobalState.IsAvailable) sync.Global.SentChannelHashes(s.Globals);
            }
            catch (Exception ex)
            {
                s.Globals.Clear();
                _log.Debug(LogCategory.Integrity, "checkpoint global hashes failed: " + ex.Message);
            }
            byte[] summary = s.ToBytes();
            _cpBatch.ReplayCheckpoint(_cpIndex, ReplayCheckpointPhase.End, s.HostFrame, summary, 0, summary.Length);
            Send(host, peer);
            _cpActive = false;
            _checkpointsWritten++;
            _nextCheckpointAt = now + CheckpointInterval;
            _writer.Note(now, s.HostFrame, "checkpoint " + _cpIndex + " ends: " + _cpEntities + " objects, " + _cpMembers + " member sets, " + s.Lines.Count + " lines, " + s.IconCount + " icons, " + s.Globals.Count + " global channels");
            if (_cpFinal) _finalCheckpointDone = true;
            Version++;
        }

        private void Send(SessionHost host, PeerId peer)
        {
            if (_cpBatch.IsEmpty) return;
            ArraySegment<byte> bytes = _cpBatch.Finish();
            host.SendTo(peer, MessageKind.Epoch, bytes.Array, bytes.Offset, bytes.Count);
            _cpBatch.Reset();
        }

        /// <summary>The recorder's view report: the host's own camera, selection, infoview and followed object.</summary>
        private void FillView(ViewReport view)
        {
            CameraUpdateSystem camera = _camera ?? (_camera = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<CameraUpdateSystem>());
            if (camera != null)
            {
                float3 position = camera.position;
                float3 pivot = position;
                IGameCameraController controller = camera.activeCameraController;
                if (controller != null)
                {
                    UnityEngine.Vector3 p = controller.pivot;
                    pivot = new float3(p.x, p.y, p.z);
                }
                float zoom = camera.zoom;
                if (!(zoom > 0f) || float.IsInfinity(zoom)) zoom = math.distance(position, pivot);
                view.PivotX = Finite(pivot.x);
                view.PivotZ = Finite(pivot.z);
                view.Zoom = Finite(zoom);
                view.CamX = Finite(position.x);
                view.CamY = Finite(position.y);
                view.CamZ = Finite(position.z);
            }
            AttentionValues.Instance?.FillViewReport(view, forRecorder: true);
            AgentSync.Instance?.FillViewReport(view, forRecorder: true);
        }

        private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        private uint Frame()
        {
            try
            {
                SimulationSystem sim = _simulation ?? (_simulation = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<SimulationSystem>());
                return sim != null ? sim.frameIndex : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ================================================================== replay

        /// <summary>Starts replaying the recording in <paramref name="id"/> (a folder name under <see cref="Root"/>) at <paramref name="speed"/> (0: full speed).</summary>
        public void StartReplay(string id, double speed)
        {
            if (!CanReplay)
            {
                SessionManager.Notify(LocaleSource.Resolve("st.toast.replay.unavailable"), ProgressState.Warning);
                return;
            }
            string folder = FolderOf(id);
            string path = folder != null ? Path.Combine(folder, RecordingFormat.StreamFileName) : null;
            if (path == null || !File.Exists(path))
            {
                SessionManager.Notify(LocaleSource.Resolve("st.toast.replay.missing"), ProgressState.Warning);
                return;
            }
            RecordingReader reader = null;
            try
            {
                reader = new RecordingReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20));
                HandshakeDigests mine = _sessions.Info.GetDigests();
                if (reader.Header.ProtocolVersion != mine.ProtocolVersion)
                {
                    reader.Dispose();
                    SessionManager.Notify(LocaleSource.Resolve("st.toast.replay.version"), ProgressState.Warning);
                    return;
                }
                var playback = new PlaybackTransport(reader, _sessions.Clock, new PlaybackOptions { Speed = speed, HostName = reader.Header.HostName }, _log)
                {
                    // the brakes: never more than a few dozen host batches waiting to be applied
                    Hold = () => (Mod.Sync?.PendingBatches ?? 0) > MaxBacklogBatches,
                };
                reader = null;
                _verdict = new ReplayVerdict(playback.Header, Path.GetFileName(folder)) { Speed = speed };
                if (playback.Header.ModBuild != mine.ModBuild) _verdict.AddNote("recorded with another Skyline Together build (" + playback.Header.ModBuild.ToString("X16", CultureInfo.InvariantCulture) + "); differences may come from that");
                if (!string.Equals(playback.Header.GameVersion, mine.GameVersion, StringComparison.Ordinal)) _verdict.AddNote("recorded with game version " + playback.Header.GameVersion + ", replayed with " + mine.GameVersion);
                _checker = new ReplayChecker(_verdict, new ReplayWorld(this));
                _playback = playback;
                _replayFolder = folder;
                _replayStartedAt = _sessions.Clock.Seconds;
                _replayLiveAt = -1;
                _replayFinishedAt = -1;
                _endedBeforeLiveAt = -1;
                _replayDone = false;
                if (Mod.Sync != null) Mod.Sync.Integrity.ReplayRecord = _checker.OnRecord;
                Guard.Reported += OnGuardReported;
                _log.Info(LogCategory.Session, "replay of " + folder + " starting at " + (speed > 0 ? speed.ToString("0.##", CultureInfo.InvariantCulture) + "x" : "full speed"));
                if (!_sessions.StartReplay(playback, Path.GetFileName(folder)))
                {
                    Unhook();
                    _replayDone = true;
                    _playback = null;
                }
            }
            catch (Exception ex)
            {
                reader?.Dispose();
                Unhook();
                _replayDone = true;
                _playback = null;
                _log.Warn(LogCategory.Session, "the recording could not be opened: " + path, ex);
                SessionManager.Notify(LocaleSource.Resolve("st.toast.replay.failed", "ERROR", ex.Message), ProgressState.Failed);
            }
            Version++;
        }

        /// <summary>Stops the replay now; the verdict covers what was played.</summary>
        public void StopReplay()
        {
            if (_replayDone) return;
            FinishReplay("stopped by the player");
        }

        private void TickReplay()
        {
            if (_replayDone || _playback == null) return;
            double now = _sessions.Clock.Seconds;
            SessionClient client = _sessions.Client;
            if (client == null) return; // OnReplayEnded finishes
            if (client.State == ClientState.Live && _replayLiveAt < 0)
            {
                _replayLiveAt = now;
                _verdict.LoadSeconds = now - _replayStartedAt;
                Version++;
            }
            if (!_playback.Finished)
            {
                _replayFinishedAt = -1;
                return;
            }
            if (client.State != ClientState.Live)
            {
                // the recording ended before the session was live (it was stopped during the join)
                if (_endedBeforeLiveAt < 0) _endedBeforeLiveAt = now;
                if (now - _endedBeforeLiveAt > EndedBeforeLiveSeconds) FinishReplay("the recording ended before the replay was live");
                return;
            }
            bool idle = (Mod.Sync?.PendingBatches ?? 0) == 0 && (_checker == null || !_checker.Busy);
            if (!idle)
            {
                _replayFinishedAt = -1;
                return;
            }
            if (_replayFinishedAt < 0) _replayFinishedAt = now;
            if (now - _replayFinishedAt >= ReplaySettleSeconds) FinishReplay(_playback.EndReason);
        }

        private void OnReplayEnded(ClientEndKind kind, string reason)
        {
            if (_replayDone) return;
            FinishReplay(kind == ClientEndKind.Left ? "the replay was left" : "the replay session ended: " + reason, sessionOver: true);
        }

        private void OnGuardReported(FeatureHealth feature, string where, Exception ex)
        {
            if (_verdict == null || feature == Features.Ui) return;
            _verdict.AddError("[" + (feature?.Id ?? "?") + "] " + where + ": " + (ex != null ? ex.GetType().Name + ": " + ex.Message : "failed"));
        }

        private void FinishReplay(string reason, bool sessionOver = false)
        {
            if (_replayDone) return;
            _replayDone = true;
            double now = _sessions.Clock.Seconds;
            ReplayVerdict v = _verdict;
            PlaybackTransport p = _playback;
            try
            {
                v.EndReason = reason ?? "";
                v.StreamCompleted = p.Finished && p.EndedNormally;
                if (p.Finished && !p.EndedNormally) v.AddNote(p.EndReason);
                v.RecordedSeconds = p.DeliveredSeconds;
                v.WallSeconds = now - _replayStartedAt;
                v.MessagesDelivered = p.MessagesDelivered;
                v.BytesDelivered = p.BytesDelivered;
                foreach (KeyValuePair<MessageKind, long> sent in p.SentByKind) v.ClientSent[sent.Key.ToString()] = sent.Value;
                v.FinalStatus = Mod.Sync?.Status ?? "";
                if (_checker != null && _checker.Busy) v.AddNote("the last icon comparison did not finish");
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "completing the verdict failed", ex);
            }
            Unhook();
            try
            {
                File.WriteAllText(Path.Combine(_replayFolder, RecordingFormat.VerdictTextFileName), v.ToText(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(_replayFolder, RecordingFormat.VerdictJsonFileName), v.ToJson(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "writing the verdict failed", ex);
            }
            LastVerdict = v;
            LastVerdictFolder = _replayFolder ?? "";
            _recordings = null;
            _log.Info(LogCategory.Session, "replay verdict (" + v.Status + "): " + v.Summary + " -> " + _replayFolder);
            SessionManager.Notify(LocaleSource.Resolve(v.Passed ? "st.toast.replay.passed" : "st.toast.replay.failedVerdict", "SUMMARY", v.Summary), v.Passed ? ProgressState.Complete : ProgressState.Warning, null, 12f);
            _playback = null;
            _checker = null;
            // the session ends like a normal leave: gates, clock and movers are restored; the replica stays as a single-player city
            if (!sessionOver && _sessions.Client != null) _sessions.Leave();
            Version++;
        }

        private void Unhook()
        {
            if (Mod.Sync != null) Mod.Sync.Integrity.ReplayRecord = null;
            Guard.Reported -= OnGuardReported;
        }

        // ================================================================== recordings

        /// <summary>The recordings, newest first (cached; refreshed after a recording or a replay, or on request).</summary>
        public IReadOnlyList<RecordingInfo> Recordings
        {
            get
            {
                if (_recordings == null) _recordings = Scan();
                return _recordings;
            }
        }

        public void RefreshRecordings()
        {
            _recordings = null;
            Version++;
        }

        private List<RecordingInfo> Scan()
        {
            var list = new List<RecordingInfo>();
            try
            {
                string root = Root;
                if (!Directory.Exists(root)) return list;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string stream = Path.Combine(dir, RecordingFormat.StreamFileName);
                    if (!File.Exists(stream)) continue;
                    var info = new RecordingInfo { Id = Path.GetFileName(dir), Folder = dir, Created = Directory.GetCreationTime(dir) };
                    try
                    {
                        info.Bytes = new FileInfo(stream).Length;
                        string manifest = Path.Combine(dir, RecordingFormat.ManifestFileName);
                        if (File.Exists(manifest))
                        {
                            JsonValue m = MiniJson.Parse(File.ReadAllText(manifest));
                            info.City = Text(m, "city");
                            info.Seconds = Number(m, "seconds", -1);
                        }
                        if (string.IsNullOrEmpty(info.City))
                        {
                            using (var r = new RecordingReader(new FileStream(stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                            {
                                info.City = r.Header.CityName;
                            }
                        }
                        string verdict = Path.Combine(dir, RecordingFormat.VerdictJsonFileName);
                        if (File.Exists(verdict)) info.Verdict = Text(MiniJson.Parse(File.ReadAllText(verdict)), "status");
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(LogCategory.Session, "recording " + dir + ": " + ex.Message);
                    }
                    list.Add(info);
                }
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "listing the recordings failed", ex);
            }
            list.Sort((a, b) => string.CompareOrdinal(b.Id, a.Id));
            if (list.Count > MaxListed) list.RemoveRange(MaxListed, list.Count - MaxListed);
            return list;
        }

        private static string Text(JsonValue v, string name)
        {
            try
            {
                JsonValue f = v[name];
                return f?.AsString("") ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static double Number(JsonValue v, string name, double fallback)
        {
            try
            {
                JsonValue f = v[name];
                return f != null ? f.AsNumber(fallback) : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private string FolderOf(string id)
        {
            if (string.IsNullOrEmpty(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return null;
            string folder = Path.Combine(Root, id);
            return Directory.Exists(folder) ? folder : null;
        }

        // ================================================================== helpers

        private static string CityName()
        {
            try
            {
                string name = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<CityConfigurationSystem>()?.cityName;
                return string.IsNullOrWhiteSpace(name) ? "City" : name.Trim();
            }
            catch (Exception)
            {
                return "City";
            }
        }

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == '.') sb.Append('_');
                else sb.Append(c);
                if (sb.Length >= 40) break;
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "City" : s;
        }

        private static string UniqueFolder(string folder)
        {
            string candidate = folder;
            for (int i = 2; Directory.Exists(candidate) && i < 100; i++) candidate = folder + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
            return candidate;
        }

        internal static string Duration(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) return "-";
            int s = (int)Math.Round(seconds);
            return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        internal static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

        /// <summary>The replaying client's world, through the rolling verification's own functions.</summary>
        private sealed class ReplayWorld : IReplayWorld
        {
            private readonly ReplayController _owner;

            public ReplayWorld(ReplayController owner)
            {
                _owner = owner;
            }

            private static IntegritySync Integrity => Mod.Sync?.Integrity;

            public void HashBucket(uint bucket, List<EntityHash> output)
            {
                if (Integrity != null) Integrity.HashBucketForReplay(bucket, output);
                else output.Clear();
            }

            public bool CollectMembers(uint netId, List<ComponentHash> output) => Integrity != null && Integrity.CollectForReplay(netId, output, out _);

            public string FamilyOf(uint netId) => Integrity?.FamilyNameOf(netId) ?? "";

            public string TypeName(ulong typeHash) => IntegritySync.TypeNameOf(typeHash);

            public List<LineStateRow> LineRows()
            {
                try
                {
                    return Integrity?.Lines.ComputeRows();
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public bool IconsSettled => Integrity == null || Integrity.Icons.ClientSettled;

            public bool IconDigests(List<IconOwnerDigest> owners, out uint ownerCount, out uint iconCount, out ulong sum)
            {
                ownerCount = 0;
                iconCount = 0;
                sum = 0;
                return Integrity != null && Integrity.Icons.LocalOwnerDigests(owners, out ownerCount, out iconCount, out sum);
            }

            public void GlobalHashes(IReadOnlyList<GlobalChannelHash> wanted, List<GlobalChannelHash> output)
            {
                if (Features.GlobalState.IsAvailable) Mod.Sync?.Global.LocalChannelHashes(wanted, output);
            }

            public double ReplaySeconds => _owner._replayLiveAt >= 0 ? _owner._sessions.Clock.Seconds - _owner._replayLiveAt : 0;
        }
    }
}
