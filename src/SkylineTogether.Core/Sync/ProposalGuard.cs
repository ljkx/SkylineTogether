using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Host: per-peer token bucket for proposals (ARCHITECTURE.md#host-validation-and-commit; <c>SyncController</c> uses 120 per
    /// second with a burst of 240).
    /// Main thread only.
    /// </summary>
    public sealed class ProposalRateLimiter
    {
        private readonly double _perSecond;
        private readonly double _burst;
        private readonly Dictionary<byte, (double Tokens, double Time)> _buckets = new Dictionary<byte, (double, double)>();

        public ProposalRateLimiter(double perSecond = 50, double burst = 100)
        {
            if (perSecond <= 0 || burst < 1) throw new ArgumentOutOfRangeException(nameof(perSecond));
            _perSecond = perSecond;
            _burst = burst;
        }

        /// <summary>Takes one token for <paramref name="peer"/> at <paramref name="now"/>; false when the bucket is empty.</summary>
        public bool TryTake(byte peer, double now)
        {
            if (!_buckets.TryGetValue(peer, out var b)) b = (_burst, now);
            double tokens = Math.Min(_burst, b.Tokens + Math.Max(0, now - b.Time) * _perSecond);
            bool ok = tokens >= 1;
            if (ok) tokens -= 1;
            _buckets[peer] = (tokens, now);
            return ok;
        }

        public void Forget(byte peer) => _buckets.Remove(peer);

        public void Clear() => _buckets.Clear();
    }

    /// <summary>
    /// Host limits for the requests a client batch may carry without any entity, cost or global record
    /// (request-only batches skip the proposal rate limit): more ids (<c>SyncRequest.MoreIds</c>) and full re-sends of
    /// the lines or of everything (<c>ResendRoutes</c>, <c>ResendAll</c>, each a scan of the whole id table).
    /// <list type="bullet">
    /// <item>Ids: a token bucket per peer, and no new grant while the peer still holds <see cref="MaxUnusedIds"/> granted
    /// ids it has not used (a client asks at <see cref="ClientIdPool.LowWater"/> remaining, so an honest one never
    /// does). A refused request is dropped; the client asks again with backoff (<see cref="ClientIdPool"/>).</item>
    /// <item>Re-sends: one bucket per peer for both kinds. A refused re-send is not lost: it is remembered (a full
    /// re-send covers a lines-only one) and served by <see cref="TakeDeferred"/> once the bucket refills, so the
    /// route tool's "give me the host's lines back" always arrives.</item>
    /// </list>
    /// Main thread only.
    /// </summary>
    public sealed class RequestThrottle
    {
        /// <summary>Granted-but-unused ids above which a peer gets no further grant.</summary>
        public const uint MaxUnusedIds = 4 * NetIdAllocator.DefaultGrant;

        private readonly ProposalRateLimiter _ids;
        private readonly ProposalRateLimiter _resends;
        private readonly Dictionary<byte, bool> _deferred = new Dictionary<byte, bool>(); // peer -> routes only

        public RequestThrottle(double idsPerSecond = 0.5, double idBurst = 4, double resendsPerSecond = 0.2, double resendBurst = 3)
        {
            _ids = new ProposalRateLimiter(idsPerSecond, idBurst);
            _resends = new ProposalRateLimiter(resendsPerSecond, resendBurst);
        }

        /// <summary>True when a grant may be made now to a peer that holds <paramref name="unusedHeld"/> unused ids.</summary>
        public bool TryGrantIds(byte peer, double now, long unusedHeld)
        {
            if (unusedHeld >= MaxUnusedIds) return false;
            return _ids.TryTake(peer, now);
        }

        /// <summary>True when the re-send may be served now; otherwise it is remembered for <see cref="TakeDeferred"/>.</summary>
        public bool TryResend(byte peer, bool routesOnly, double now)
        {
            if (_resends.TryTake(peer, now)) return true;
            _deferred[peer] = _deferred.TryGetValue(peer, out bool pendingRoutesOnly) ? pendingRoutesOnly && routesOnly : routesOnly;
            return false;
        }

        /// <summary>True when the peer has a remembered re-send and the bucket allows it now (it is then taken).</summary>
        public bool TakeDeferred(byte peer, double now, out bool routesOnly)
        {
            if (!_deferred.TryGetValue(peer, out routesOnly) || !_resends.TryTake(peer, now)) return false;
            _deferred.Remove(peer);
            return true;
        }

        public bool HasDeferred(byte peer) => _deferred.ContainsKey(peer);

        public void Forget(byte peer)
        {
            _ids.Forget(peer);
            _resends.Forget(peer);
            _deferred.Remove(peer);
        }

        public void Clear()
        {
            _ids.Clear();
            _resends.Clear();
            _deferred.Clear();
        }
    }

    /// <summary>
    /// Host: counts rejected proposals that indicate abuse or a broken client (rate, size, malformed, host-only
    /// families). Repeated violations within a window mean the player is kicked (ARCHITECTURE.md#host-validation-and-commit, #security-model). Rejections for
    /// conflicts, money, permissions and the placement rules are ordinary game events (or a mod the rules do not know)
    /// and never counted.
    /// </summary>
    public sealed class ViolationTracker
    {
        private readonly int _limit;
        private readonly double _window;
        private readonly Dictionary<byte, Queue<double>> _times = new Dictionary<byte, Queue<double>>();

        public ViolationTracker(int limit = 20, double windowSeconds = 10)
        {
            _limit = Math.Max(1, limit);
            _window = windowSeconds;
        }

        /// <summary>Records a violation; true when the peer has now reached the kick threshold.</summary>
        public bool Record(byte peer, double now)
        {
            if (!_times.TryGetValue(peer, out Queue<double> q)) _times[peer] = q = new Queue<double>();
            q.Enqueue(now);
            while (q.Count > 0 && now - q.Peek() > _window) q.Dequeue();
            return q.Count >= _limit;
        }

        public void Forget(byte peer) => _times.Remove(peer);

        public void Clear() => _times.Clear();

        /// <summary>True for reasons that count as violations.</summary>
        public static bool IsViolation(RejectReason reason) => reason == RejectReason.RateLimited || reason == RejectReason.Invalid || reason == RejectReason.HostOnly;
    }

    /// <summary>
    /// Host: the epoch of the last authored change of every net id (ARCHITECTURE.md#host-validation-and-commit). The host numbers its
    /// structural broadcasts (<see cref="SyncRecordKind.Epoch"/>); an entity authored while epoch N is being built is
    /// recorded at N with its author. A proposal from a client that had applied epochs up to B conflicts on an entity
    /// whose last authored change is newer than B and came from somebody else: the client edited a stale version.
    /// Only authored changes count (tool applies, committed proposals, UI edits), never simulation writes, so a growing
    /// building never makes a proposal fail. Main thread only.
    /// </summary>
    public sealed class AuthoredLedger
    {
        /// <summary>Entries older than this many epochs are pruned (every client's base is far newer in practice).</summary>
        public const uint Horizon = 1u << 14;

        private readonly Dictionary<uint, (uint Epoch, byte Author)> _authored = new Dictionary<uint, (uint, byte)>();
        private uint _current = 1;

        /// <summary>The number the next structural broadcast will carry.</summary>
        public uint Current => _current;

        /// <summary>The number of the last broadcast (0 before the first).</summary>
        public uint LastBroadcast => _current - 1;

        public int Count => _authored.Count;

        /// <summary>Records that <paramref name="author"/> changed <paramref name="id"/> in the epoch being built.</summary>
        public void MarkAuthored(uint id, byte author)
        {
            if (id != 0) _authored[id] = (_current, author);
        }

        /// <summary>The epoch being built was broadcast; returns its number and starts the next one.</summary>
        public uint Advance()
        {
            uint stamped = _current;
            _current = _current == uint.MaxValue ? 1u : _current + 1;
            if ((stamped & 1023) == 0) Prune();
            return stamped;
        }

        /// <summary>
        /// True when <paramref name="peer"/>'s edit of <paramref name="id"/>, based on epoch <paramref name="baseEpoch"/>,
        /// would overwrite a newer authored change of another player.
        /// </summary>
        public bool IsConflict(uint id, uint baseEpoch, byte peer)
        {
            if (!_authored.TryGetValue(id, out var e)) return false;
            return e.Epoch > baseEpoch && e.Author != peer;
        }

        /// <summary>
        /// Whether a proposal based on epoch <paramref name="baseEpoch"/> can be checked for conflicts:
        /// <see cref="RejectReason.Invalid"/> for a base the host never broadcast (a forged header),
        /// <see cref="RejectReason.Timeout"/> for a base so old that the ledger may have pruned the changes made since
        /// (more than <see cref="Horizon"/> epochs: the client has applied nothing for minutes, and its edit could
        /// silently overwrite newer ones), otherwise <see cref="RejectReason.None"/>.
        /// </summary>
        public RejectReason CheckBase(uint baseEpoch)
        {
            if (baseEpoch > LastBroadcast) return RejectReason.Invalid;
            if (_current > Horizon && baseEpoch < _current - Horizon) return RejectReason.Timeout;
            return RejectReason.None;
        }

        /// <summary>The author of the newest change of <paramref name="id"/> (false when none is recorded).</summary>
        public bool TryGetAuthor(uint id, out byte author, out uint epoch)
        {
            bool found = _authored.TryGetValue(id, out var e);
            author = e.Author;
            epoch = e.Epoch;
            return found;
        }

        public void Forget(uint id) => _authored.Remove(id);

        public void Clear()
        {
            _authored.Clear();
            _current = 1;
        }

        private void Prune()
        {
            if (_current <= Horizon) return;
            uint limit = _current - Horizon;
            var old = new List<uint>();
            foreach (KeyValuePair<uint, (uint Epoch, byte Author)> e in _authored)
            {
                if (e.Value.Epoch < limit) old.Add(e.Key);
            }
            foreach (uint id in old) _authored.Remove(id);
        }
    }

    /// <summary>What a proposal batch asks for, reduced to the facts the permission and economy rules need.</summary>
    public struct ProposalFacts
    {
        public int Spawns;
        public int Upserts;
        public int Despawns;
        public int ZoneBlocks;
        public bool HasCost;
        public long Cost;
        public ProposalFlags Flags;
        /// <summary>The number of <see cref="SyncRecordKind.ProposalHeader"/> records (a client writes exactly one).</summary>
        public int Headers;
        /// <summary>The header's base ledger epoch (meaningful when <see cref="Headers"/> is 1).</summary>
        public uint BaseEpoch;
        public uint Seq;

        public int EntityCount => Spawns + Upserts + Despawns + ZoneBlocks;

        /// <summary>
        /// True when the batch changes the world (entities, zone cells or money) and so needs a header: the conflict
        /// check and the rollback depend on its base epoch and sequence number.
        /// </summary>
        public bool NeedsHeader => EntityCount > 0 || HasCost;
    }

    /// <summary>Pure host validation rules of ARCHITECTURE.md#host-validation-and-commit that need no world access.</summary>
    public static class ProposalRules
    {
        /// <summary>Entities (creates + updates + deletes + zone blocks) per proposal at most (ARCHITECTURE.md#host-validation-and-commit).</summary>
        public const int MaxEntities = 200000;

        /// <summary>The permission flags a proposal needs.</summary>
        public static Permission Required(in ProposalFacts f)
        {
            Permission p = Permission.None;
            if (f.Spawns > 0 || f.Upserts > 0) p |= Permission.Build;
            if (f.Despawns > 0) p |= Permission.Demolish;
            if (f.ZoneBlocks > 0) p |= Permission.Zoning;
            if ((f.Flags & ProposalFlags.DirectTool) != 0) p |= Permission.ModTools;
            return p;
        }

        /// <summary>
        /// Economy rule 7: a charge must be affordable unless money is unlimited; a refund (negative cost) needs at least
        /// one removed or changed entity; amounts must fit the game's 32-bit money.
        /// </summary>
        public static RejectReason CheckCost(in ProposalFacts f, int money, bool unlimited)
        {
            if (!f.HasCost || f.Cost == 0) return RejectReason.None;
            if (f.Cost > int.MaxValue || f.Cost < -int.MaxValue) return RejectReason.Invalid;
            if (f.Cost < 0) return f.Despawns + f.Upserts > 0 ? RejectReason.None : RejectReason.Invalid;
            if (unlimited) return RejectReason.None;
            return f.Cost > money ? RejectReason.NotEnoughMoney : RejectReason.None;
        }

        /// <summary>
        /// What the host charges for an applied proposal. The client's own sum of its tool's costs is
        /// kept whenever it is plausible; it is raised to the host's bounds when it is not: a proposal never costs less
        /// than the construction cost of what it created (<paramref name="minCharge"/>, computed by the host from the
        /// created objects' prefabs) minus the most it may refund (<paramref name="maxRefund"/>, from the recent
        /// modification costs of the entities it removed or changed), and it never refunds more than that. A vanilla
        /// client's cost always lies within these bounds, so only forged amounts change.
        /// </summary>
        public static long EffectiveCost(long clientCost, long minCharge, long maxRefund)
        {
            long refundCap = Math.Max(0, maxRefund);
            long floor = Math.Max(0, minCharge) - refundCap;
            long cost = Math.Max(clientCost, floor);
            return Math.Max(-int.MaxValue, Math.Min(int.MaxValue, cost));
        }

        /// <summary>
        /// The most a removal may refund: the summed recent modification costs of the removed or changed
        /// entities times the largest refund percentage of the economy parameters (clamped to 0..4; 1 when unknown).
        /// </summary>
        public static long RefundBound(long recentModificationCosts, double maxRefundPercentage)
        {
            double pct = double.IsNaN(maxRefundPercentage) || double.IsInfinity(maxRefundPercentage) ? 1.0 : Math.Max(0, Math.Min(4, maxRefundPercentage));
            double bound = Math.Ceiling(Math.Max(0, recentModificationCosts) * pct);
            return bound >= int.MaxValue ? int.MaxValue : (long)bound;
        }

        /// <summary>
        /// Header rule: a batch that changes the world carries exactly one <see cref="SyncRecordKind.ProposalHeader"/>
        /// (every client capture writes one), and no batch carries several. Without it the conflict check would have no
        /// base epoch, so such a batch is refused as <see cref="RejectReason.Invalid"/>.
        /// </summary>
        public static RejectReason CheckHeader(in ProposalFacts f)
        {
            if (f.Headers > 1) return RejectReason.Invalid;
            if (f.NeedsHeader && f.Headers == 0) return RejectReason.Invalid;
            return RejectReason.None;
        }

        /// <summary>
        /// Sender rule: only a live player's proposals are applied. One queued before the player left (or was replaced
        /// by a reconnect) is dropped as <see cref="RejectReason.NotLive"/>; it is logged on the host but cannot be
        /// delivered (the peer is gone, and a joiner's snapshot carries the host's state anyway).
        /// </summary>
        public static RejectReason CheckSender(bool live) => live ? RejectReason.None : RejectReason.NotLive;

        /// <summary>Size rule 1.</summary>
        public static RejectReason CheckSize(in ProposalFacts f) => f.EntityCount > MaxEntities ? RejectReason.Invalid : RejectReason.None;

        /// <summary>
        /// The locale key of the reason in the toast a rejected player sees (<c>st.reject.*</c> in the game layer's
        /// locale tables, inserted into <c>st.toast.rejected</c>); unknown values fall back to <c>st.reject.other</c>.
        /// </summary>
        public static string LocaleKey(RejectReason reason)
        {
            switch (reason)
            {
                case RejectReason.Conflict: return "st.reject.conflict";
                case RejectReason.NoPermission: return "st.reject.noPermission";
                case RejectReason.NotEnoughMoney: return "st.reject.notEnoughMoney";
                case RejectReason.Locked: return "st.reject.locked";
                case RejectReason.OutsideTiles: return "st.reject.outsideTiles";
                case RejectReason.Invalid: return "st.reject.invalid";
                case RejectReason.RateLimited: return "st.reject.rateLimited";
                case RejectReason.NotLive: return "st.reject.notLive";
                case RejectReason.HostError: return "st.reject.hostError";
                case RejectReason.Timeout: return "st.reject.timeout";
                case RejectReason.HostOnly: return "st.reject.hostOnly";
                case RejectReason.PrefabClass: return "st.reject.prefabClass";
                case RejectReason.Geometry: return "st.reject.geometry";
                default: return "st.reject.other";
            }
        }

        /// <summary>The reason in English, for the host's and the client's logs (the toast uses <see cref="LocaleKey"/>).</summary>
        public static string Describe(RejectReason reason)
        {
            switch (reason)
            {
                case RejectReason.None: return "accepted";
                case RejectReason.Conflict: return "another player changed the same thing first";
                case RejectReason.NoPermission: return "your role does not allow this (ask the host)";
                case RejectReason.NotEnoughMoney: return "the city cannot afford it";
                case RejectReason.Locked: return "it is not unlocked yet";
                case RejectReason.OutsideTiles: return "it is outside the city's map tiles";
                case RejectReason.Invalid: return "the host could not use it";
                case RejectReason.RateLimited: return "too many changes at once";
                case RejectReason.NotLive: return "you are still joining";
                case RejectReason.HostError: return "the host failed to apply it";
                case RejectReason.Timeout: return "it was based on a state of the city that is too old";
                case RejectReason.HostOnly: return "only the host can do this";
                case RejectReason.PrefabClass: return "that replacement is not allowed";
                case RejectReason.Geometry: return "its shape is not one the game allows";
                default: return "rejected (" + (ushort)reason + ")";
            }
        }
    }
}

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Change detection for state that vanilla writes without any tag (R10 §2 M4: historical flag, vehicle models,
    /// service districts, mesh colours): a baseline value per net id, re-taken whenever the host wrote the entity, so
    /// only a difference that appeared between two observations without a host write counts as the local player's
    /// edit. The first observation of an id only sets its baseline. Main thread only.
    /// </summary>
    public sealed class EditWatch
    {
        private readonly System.Collections.Generic.Dictionary<uint, ulong> _baseline = new System.Collections.Generic.Dictionary<uint, ulong>();

        public int Count => _baseline.Count;

        /// <summary>
        /// Observes <paramref name="value"/> for <paramref name="id"/>; true when it differs from the baseline and the
        /// host did not write it (<paramref name="hostWrote"/>). The baseline always becomes <paramref name="value"/>.
        /// </summary>
        public bool Check(uint id, ulong value, bool hostWrote)
        {
            bool known = _baseline.TryGetValue(id, out ulong old);
            _baseline[id] = value;
            return known && !hostWrote && old != value;
        }

        public bool Has(uint id) => _baseline.ContainsKey(id);

        public void SetBaseline(uint id, ulong value) => _baseline[id] = value;

        public void Forget(uint id) => _baseline.Remove(id);

        public void Clear() => _baseline.Clear();
    }
}
