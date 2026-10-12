using System;
using System.Collections.Generic;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Sync;

namespace SkylineTogether.Core.Replay
{
    /// <summary>
    /// The replaying client's view of its own world, as the checkpoint comparison needs it. The game layer implements it
    /// with the very functions the rolling verification uses (<c>IntegrityHasher</c>, <c>LineCheck</c>, <c>IconSync</c>,
    /// <c>GlobalStateSync</c>), so host and client values are computed the same way.
    /// </summary>
    public interface IReplayWorld
    {
        /// <summary>The integrity hashes of the bound entities of one bucket, in id order.</summary>
        void HashBucket(uint bucket, List<EntityHash> output);

        /// <summary>The members of an entity's integrity hash; false when the id is not bound here.</summary>
        bool CollectMembers(uint netId, List<ComponentHash> output);

        /// <summary>The family of a bound id here ("" when unknown).</summary>
        string FamilyOf(uint netId);

        /// <summary>A component type's name from its type hash.</summary>
        string TypeName(ulong typeHash);

        /// <summary>The UI-level line rows, or null when they cannot be computed.</summary>
        List<LineStateRow> LineRows();

        /// <summary>True when no icon reconciliation is pending (the live icons reflect every host set received).</summary>
        bool IconsSettled { get; }

        /// <summary>The live icons per owner and in total; false when unavailable.</summary>
        bool IconDigests(List<IconOwnerDigest> owners, out uint ownerCount, out uint iconCount, out ulong sum);

        /// <summary>The local hashes of the given global channels (channels it cannot read are left out).</summary>
        void GlobalHashes(IReadOnlyList<GlobalChannelHash> wanted, List<GlobalChannelHash> output);

        /// <summary>Real seconds since the replaying client went live.</summary>
        double ReplaySeconds { get; }
    }

    /// <summary>
    /// Compares the recorded host checkpoints with the replaying client's world (solo testing, ARCH §13.5). It receives
    /// every replay record right after the batch that carried it was applied, so each bucket's hashes are compared with
    /// exactly the state the host had when it hashed them. Icons are compared once the client's icon reconciliation has
    /// settled (a few frames later), unless newer icon records arrived meanwhile. Pure: the world is an interface.
    /// </summary>
    public sealed class ReplayChecker
    {
        /// <summary>Frames an icon comparison waits for the reconciliation to settle before it is compared anyway.</summary>
        public const int IconSettleFrames = 180;

        private readonly ReplayVerdict _verdict;
        private readonly IReplayWorld _world;
        private readonly MemberArchive _archive = new MemberArchive();
        private readonly List<EntityHash> _local = new List<EntityHash>(EntityDigest.BucketSize);
        private readonly List<ComponentHash> _members = new List<ComponentHash>(64);
        private readonly Dictionary<uint, ulong> _hostHashes = new Dictionary<uint, ulong>(EntityDigest.BucketSize);
        private CheckpointSummary _pendingIcons;
        private int _iconFrames;
        private bool _iconsSuperseded;

        public ReplayChecker(ReplayVerdict verdict, IReplayWorld world)
        {
            _verdict = verdict ?? throw new ArgumentNullException(nameof(verdict));
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        public ReplayVerdict Verdict => _verdict;

        /// <summary>Checkpoints begun so far.</summary>
        public int CheckpointsSeen { get; private set; }

        /// <summary>Checkpoints whose end arrived.</summary>
        public int CheckpointsCompleted { get; private set; }

        /// <summary>The newest checkpoint seen (begun).</summary>
        public uint CurrentCheckpoint { get; private set; }

        /// <summary>An icon comparison waits for the client's icons to settle.</summary>
        public bool Busy => _pendingIcons != null;

        /// <summary>Recorded members kept for explanations.</summary>
        public int ArchivedMembers => _archive.Count;

        /// <summary>A record of a host batch that was just applied (replay records; icon records mark pending icon comparisons stale).</summary>
        public void OnRecord(SyncRecord r)
        {
            if (r == null) return;
            switch (r.Kind)
            {
                case SyncRecordKind.ReplayMembers:
                    _archive.Add(r.NetId, r.Family, r.Hash, r.ComponentHashes ?? new List<ComponentHash>());
                    break;
                case SyncRecordKind.ReplayHashes:
                    CompareBucket(r.Seq, r.Bucket, r.Hashes ?? new List<EntityHash>());
                    break;
                case SyncRecordKind.ReplayCheckpoint:
                    if (r.CheckpointPhase == ReplayCheckpointPhase.Begin)
                    {
                        CheckpointsSeen++;
                        CurrentCheckpoint = r.Seq;
                        _verdict.Checkpoint(r.Seq);
                    }
                    else
                    {
                        OnCheckpointEnd(r);
                    }
                    break;
                case SyncRecordKind.IconSet:
                case SyncRecordKind.IconControl:
                    if (_pendingIcons != null) _iconsSuperseded = true;
                    break;
            }
        }

        /// <summary>Once per frame after the icon playback: compares a pending icon checkpoint when settled.</summary>
        public void FrameEnd()
        {
            if (_pendingIcons == null) return;
            _iconFrames++;
            if (_iconsSuperseded)
            {
                _verdict.AddNote("checkpoint " + _pendingIcons.Index + ": icons not compared (newer icon changes arrived before they settled)");
                _pendingIcons = null;
                return;
            }
            bool settled;
            try
            {
                settled = _world.IconsSettled;
            }
            catch (Exception)
            {
                settled = true;
            }
            if (!settled && _iconFrames < IconSettleFrames) return;
            CheckpointSummary s = _pendingIcons;
            _pendingIcons = null;
            CompareIcons(s, settled);
        }

        private void CompareBucket(uint checkpoint, uint bucket, List<EntityHash> host)
        {
            _world.HashBucket(bucket, _local);
            _verdict.OnBucketCompared(checkpoint, host.Count);
            EntityDigest.Accumulator local = EntityDigest.Of(_local);
            if (EntityDigest.Of(host).Matches(local.Count, local.Sum)) return;
            _hostHashes.Clear();
            foreach (EntityHash h in host) _hostHashes[h.NetId] = h.Hash;
            foreach ((uint id, EntityDifference kind) in EntityDigest.Compare(host, _local))
            {
                DivergenceKind k = kind == EntityDifference.MissingOnClient ? DivergenceKind.MissingHere : kind == EntityDifference.ExtraOnClient ? DivergenceKind.ExtraHere : DivergenceKind.Differs;
                string family = "";
                string components;
                if (k == DivergenceKind.ExtraHere)
                {
                    family = _world.FamilyOf(id);
                    components = "the host has no object with this id";
                }
                else if (k == DivergenceKind.MissingHere)
                {
                    if (_archive.TryGetFamily(id, out Family f)) family = f.ToString();
                    components = "not created in the replay (or deleted)";
                }
                else
                {
                    family = _world.FamilyOf(id);
                    components = Explain(id, _hostHashes[id], ref family);
                }
                _verdict.OnEntity(checkpoint, id, k, family, components);
            }
        }

        private string Explain(uint id, ulong hostHash, ref string family)
        {
            if (!_archive.TryGet(id, hostHash, out Family recordedFamily, out IReadOnlyList<ComponentHash> host))
            {
                return "components unknown (the host's members of this state were not recorded)";
            }
            if (string.IsNullOrEmpty(family)) family = recordedFamily.ToString();
            else if (family != recordedFamily.ToString()) family = family + " here, " + recordedFamily + " on the host";
            _members.Clear();
            if (!_world.CollectMembers(id, _members)) return "not bound here";
            string text = ReplayDiff.ExplainMembers(host, _members, _world.TypeName);
            return text.Length == 0 ? "the same components (the family or the set of hashed types differs)" : text;
        }

        private void OnCheckpointEnd(SyncRecord r)
        {
            CheckpointSummary s;
            try
            {
                s = CheckpointSummary.Read(r.Payload ?? Array.Empty<byte>(), 0, r.Payload?.Length ?? 0);
            }
            catch (DecodeException ex)
            {
                _verdict.AddError("checkpoint " + r.Seq + ": unreadable summary (" + ex.Message + ")");
                return;
            }
            CheckpointsCompleted++;
            _verdict.OnCheckpointEnd(r.Seq, r.Frame, s.HostSeconds, _world.ReplaySeconds);
            if (s.HasLines)
            {
                List<LineStateRow> mine = _world.LineRows();
                if (mine == null)
                {
                    _verdict.AddNote("checkpoint " + r.Seq + ": the line rows could not be computed here");
                }
                else
                {
                    var diffs = new List<string>();
                    foreach (LineStateDifference d in LineStateReport.Compare(s.Lines, mine)) diffs.Add(d.ToString());
                    _verdict.OnLines(r.Seq, Math.Max(s.Lines.Count, mine.Count), diffs);
                }
            }
            if (s.Globals.Count > 0)
            {
                var mine = new List<GlobalChannelHash>();
                _world.GlobalHashes(s.Globals, mine);
                _verdict.OnGlobals(r.Seq, s.Globals.Count, ReplayDiff.CompareGlobals(s.Globals, mine));
            }
            if (s.HasIcons)
            {
                if (_pendingIcons != null) _verdict.AddNote("checkpoint " + _pendingIcons.Index + ": icons not compared (the next checkpoint came first)");
                _pendingIcons = s;
                _iconFrames = 0;
                _iconsSuperseded = false;
            }
        }

        private void CompareIcons(CheckpointSummary s, bool settled)
        {
            var owners = new List<IconOwnerDigest>();
            if (!_world.IconDigests(owners, out uint ownerCount, out uint iconCount, out ulong sum))
            {
                _verdict.AddNote("checkpoint " + s.Index + ": the icons could not be read here");
                return;
            }
            bool match = ownerCount == s.IconOwners && iconCount == s.IconCount && sum == s.IconSum;
            List<string> diffs = match ? null : ReplayDiff.CompareIconOwners(s.IconOwnerDigests, owners);
            if (!match && diffs != null && diffs.Count == 0) diffs.Add("host " + s.IconCount + " icons on " + s.IconOwners + " objects, here " + iconCount + " on " + ownerCount);
            if (!match && !settled && diffs != null) diffs.Insert(0, "(the icon reconciliation had not settled after " + IconSettleFrames + " frames)");
            _verdict.OnIcons(s.Index, match, diffs);
        }
    }
}
