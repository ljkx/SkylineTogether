using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Simulation;
using Game.UI.InGame;
using SkylineTogether.Core;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// w3/audit: system state the info panels read that clients cannot compute, because the systems that write it are
    /// <c>GameSimulation</c> systems (off on clients) or because it lives outside the entities that replicate
    /// (docs/design/SYNC-COVERAGE.md):
    /// <list type="bullet">
    /// <item>the systems' own saved state: happiness factors, production capacity, stored resources, company, consumption,
    /// residential property, study position and vehicle counters, electricity and water statistics and trade, garbage
    /// accumulation, the fire hazard's dry days (whole-system blobs, paced to their update rates);</item>
    /// <item>slices of state: the production panel's consumption, usage and chain tables (the full system blob would
    /// grow a queue on clients), the collected service fees the budget panel's fee income comes from (a cache of
    /// buffers on service prefabs, which do not replicate), the post panel's mail rates and the hail and rainbow
    /// amounts of the weather (runtime fields);</item>
    /// <item>the chirper's sender and link names (the host's <c>ChirpLinkSystem</c> cache, so chirps of citizens that do
    /// not replicate still name their sender) and the placed signature buildings (<c>PlacedSignatureBuildingData</c> on
    /// prefabs, written by the gated <c>XPBuiltSystem</c>; without it a placed signature building stays in the client's
    /// toolbar).</item>
    /// </list>
    /// Every channel is host → clients only, sent when its bytes change (and with the 30 s keyframe), and decoded with
    /// an exact-length check: a channel that ever fails to decode is switched off for the session.
    /// </summary>
    public sealed partial class GlobalStateSync
    {
        private const int MaxTableLength = 4096;
        private const int MaxChirpLinks = 512;
        private const int MaxLinksPerChirp = 16;

        private Game.Triggers.LifePathEventSystem _lifePath;
        private EntityQuery _followedQuery;
        private bool _hasFollowedQuery;
        private readonly HashSet<uint> _followedBase = new HashSet<uint>();
        private readonly HashSet<uint> _followedNow = new HashSet<uint>();
        private readonly Dictionary<uint, int> _followPending = new Dictionary<uint, int>();
        private ServiceFeeSystem _feeSystem;
        private CityProductionStatisticSystem _production;
        private MailAccumulationSystem _mail;
        private ClimateSystem _climate;
        private ChirpLinkSystem _chirpLinks;
        private EntityQuery _placedSignatures;
        private bool _hasSignatureQuery;
        private readonly List<uint> _signatureIds = new List<uint>();
        private readonly List<uint> _signatureAdd = new List<uint>();
        private readonly List<uint> _signatureRemove = new List<uint>();
        private Dictionary<uint, ReceivedChirp> _chirpNames = new Dictionary<uint, ReceivedChirp>();
        private readonly HashSet<uint> _chirpPending = new HashSet<uint>();
        private readonly List<uint> _chirpDone = new List<uint>();

        /// <summary>A chirp's names as the host cached them (net ids of the entities, 0 when they do not replicate).</summary>
        private struct ReceivedName
        {
            public uint Id;
            public Game.UI.NameSystem.Name Name;
        }

        private sealed class ReceivedChirp
        {
            public ReceivedName Sender;
            public ReceivedName[] Links;
            public bool HasLinks;
        }

        private void AttachPanels()
        {
            // whole-system blobs: info panel statistics (their writers are GameSimulation systems)
            AddBlob<CitizenHappinessSystem>("Happiness", 5.0, null, "m_LastDeps");
            AddBlob<CityProductionCapacityCalculationSystem>("Production.Capacity", 5.0, null);
            AddBlob<CountCityStoredResourceSystem>("Count.StoredResources", 2.0, null, "m_Handle");
            AddBlob<CountCompanyDataSystem>("Count.Companies", 2.0, s => { s.GetProduction(out JobHandle d); return d; }, "m_ReadDependencies", "m_WriteDependencies");
            AddBlob<CountConsumptionSystem>("Count.Consumption", 2.0, s => { s.GetConsumptions(out JobHandle d); return d; }, "m_ReadDeps", "m_WriteDeps", "m_CopyDeps");
            AddBlob<CountResidentialPropertySystem>("Count.ResidentialProperties", 2.0, null);
            AddBlob<CountStudyPositionsSystem>("Count.StudyPositions", 2.0, s => { s.GetStudyPositionsByEducation(out JobHandle d); return d; }, "m_ReadDependencies", "m_WriteDependencies");
            AddBlob<CountVehicleDataSystem>("Count.Vehicles", 2.0, null, "m_VehicleDataReadDependencies");
            AddBlob<ElectricityStatisticsSystem>("Electricity.Statistics", 1.0, null);
            AddBlob<ElectricityTradeSystem>("Electricity.Trade", 1.0, null);
            AddBlob<WaterStatisticsSystem>("Water.Statistics", 1.0, null);
            AddBlob<WaterTradeSystem>("Water.Trade", 1.0, null);
            AddBlob<GarbageAccumulationSystem>("Garbage.Accumulation", 2.0, null, "m_AccumulationDeps");
            AddBlob<FireHazardSystem>("FireHazard", 5.0, null);

            // slices of state
            _production = _world.GetOrCreateSystemManaged<CityProductionStatisticSystem>();
            _feeSystem = _world.GetOrCreateSystemManaged<ServiceFeeSystem>();
            _mail = _world.GetOrCreateSystemManaged<MailAccumulationSystem>();
            _climate = _world.GetOrCreateSystemManaged<ClimateSystem>();
            _chirpLinks = _world.GetOrCreateSystemManaged<ChirpLinkSystem>();
            _lifePath = _world.GetOrCreateSystemManaged<Game.Triggers.LifePathEventSystem>();
            try
            {
                _followedQuery = _world.EntityManager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Game.Citizens.Followed>() },
                    None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
                });
                _hasFollowedQuery = true;
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Replication, "following citizens will stay local", ex);
            }
            AddCustom("Production.Statistic", 2.0, WriteProduction, ReadProduction);
            AddCustom("Fees.Collected", 1.0, WriteCollectedFees, ReadCollectedFees, () =>
            {
                // the budget system's job reads the fee list (CityServiceBudgetSystem.UpdateDataJob.m_CollectedFees)
                _budgets.GetIncomeArray(out JobHandle readers);
                readers.Complete();
            });
            AddCustom("Runtime", 1.0, WriteRuntime, ReadRuntime);
            AddCustom("Chirp.Links", 2.0, WriteChirpLinks, ReadChirpLinks);
            try
            {
                _placedSignatures = _world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PlacedSignatureBuildingData>(), ComponentType.ReadOnly<PrefabData>());
                _hasSignatureQuery = true;
                AddCustom("Signatures", 1.0, WriteSignatures, ReadSignatures);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Replication, "placed signature buildings will not sync", ex);
            }
        }

        private static int CheckedLength(int n, string what)
        {
            if (n < 0 || n > MaxTableLength) throw new InvalidOperationException(what + ": bad length " + n);
            return n;
        }

        // ------------------------------------------------------------------ production panel

        private void WriteProduction(NetWriter w)
        {
            NativeArray<int2> consumption = _production.GetConsumptionProductions();
            w.Write(consumption.Length);
            w.Write(consumption);
            NativeArray<CityProductionStatisticSystem.CityResourceUsage> usage = _production.GetCityResourceUsages();
            w.Write(usage.Length);
            w.Write(usage);
            NativeParallelHashMap<CityProductionStatisticSystem.ProductionChain, CityProductionStatisticSystem.ProductionChainValue> chains = _production.GetProductionChains();
            NativeArray<CityProductionStatisticSystem.ProductionChain> keys = chains.GetKeyArray(Allocator.Temp);
            NativeArray<CityProductionStatisticSystem.ProductionChainValue> values = chains.GetValueArray(Allocator.Temp);
            try
            {
                // a stable order, so unchanged tables hash the same
                var order = new int[keys.Length];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                Array.Sort(order, (a, b) => CompareChains(keys[a], keys[b]));
                w.Write(keys.Length);
                foreach (int i in order)
                {
                    w.Write(keys[i]);
                    w.Write(values[i]);
                }
            }
            finally
            {
                keys.Dispose();
                values.Dispose();
            }
        }

        private static int CompareChains(CityProductionStatisticSystem.ProductionChain a, CityProductionStatisticSystem.ProductionChain b)
        {
            int c = ((ulong)a.m_Consume1).CompareTo((ulong)b.m_Consume1);
            if (c != 0) return c;
            c = ((ulong)a.m_Consume2).CompareTo((ulong)b.m_Consume2);
            return c != 0 ? c : ((ulong)a.m_Produce).CompareTo((ulong)b.m_Produce);
        }

        private void ReadProduction(NetReader r)
        {
            r.Read(out int n);
            var consumption = new NativeArray<int2>(CheckedLength(n, "production"), Allocator.Temp);
            r.Read(consumption);
            r.Read(out int m);
            var usage = new NativeArray<CityProductionStatisticSystem.CityResourceUsage>(CheckedLength(m, "resource usage"), Allocator.Temp);
            r.Read(usage);
            r.Read(out int k);
            CheckedLength(k, "production chains");
            var chainKeys = new CityProductionStatisticSystem.ProductionChain[k];
            var chainValues = new CityProductionStatisticSystem.ProductionChainValue[k];
            for (int i = 0; i < k; i++)
            {
                r.Read(out chainKeys[i]);
                r.Read(out chainValues[i]);
            }
            try
            {
                NativeArray<int2> target = _production.GetConsumptionProductions();
                for (int i = 0; i < Math.Min(target.Length, consumption.Length); i++) target[i] = consumption[i];
                NativeArray<CityProductionStatisticSystem.CityResourceUsage> usageTarget = _production.GetCityResourceUsages();
                for (int i = 0; i < Math.Min(usageTarget.Length, usage.Length); i++) usageTarget[i] = usage[i];
                NativeParallelHashMap<CityProductionStatisticSystem.ProductionChain, CityProductionStatisticSystem.ProductionChainValue> chains = _production.GetProductionChains();
                chains.Clear();
                for (int i = 0; i < k; i++) chains.TryAdd(chainKeys[i], chainValues[i]);
            }
            finally
            {
                consumption.Dispose();
                usage.Dispose();
            }
        }

        // ------------------------------------------------------------------ budget panel: collected fees

        private void WriteCollectedFees(NetWriter w)
        {
            NativeList<CollectedCityServiceFeeData> fees = _feeSystem.GetServiceFees();
            w.Write(fees.Length);
            for (int i = 0; i < fees.Length; i++) w.Write(fees[i]);
        }

        private void ReadCollectedFees(NetReader r)
        {
            r.Read(out int n);
            var received = new CollectedCityServiceFeeData[CheckedLength(n, "collected fees")];
            for (int i = 0; i < received.Length; i++) r.Read(out received[i]);
            NativeList<CollectedCityServiceFeeData> fees = _feeSystem.GetServiceFees();
            fees.Clear();
            foreach (CollectedCityServiceFeeData f in received) fees.Add(in f);
        }

        // ------------------------------------------------------------------ runtime panel values (not saved)

        private void WriteRuntime(NetWriter w)
        {
            w.Write(_mail.LastAccumulatedMail);
            w.Write(_mail.LastProcessedMail);
            w.Write(_climate.hail);
            w.Write(_climate.rainbow);
        }

        private void ReadRuntime(NetReader r)
        {
            r.Read(out int accumulated);
            r.Read(out int processed);
            r.Read(out float hail);
            r.Read(out float rainbow);
            Diagnostics.Probes.MailAccumulated?.SetValue(_mail, accumulated);
            Diagnostics.Probes.MailProcessed?.SetValue(_mail, processed);
            _climate.hail = hail;
            _climate.rainbow = rainbow;
        }

        // ------------------------------------------------------------------ chirper names

        private Dictionary<Entity, ChirpLinkSystem.CachedChirpData> ChirpCache() =>
            Diagnostics.Probes.ChirpLinkCache?.GetValue(_chirpLinks) as Dictionary<Entity, ChirpLinkSystem.CachedChirpData>;

        private void WriteChirpLinks(NetWriter w)
        {
            Dictionary<Entity, ChirpLinkSystem.CachedChirpData> cache = ChirpCache();
            var rows = new List<KeyValuePair<uint, ChirpLinkSystem.CachedChirpData>>();
            if (cache != null)
            {
                foreach (KeyValuePair<Entity, ChirpLinkSystem.CachedChirpData> e in cache)
                {
                    uint id = _ids.GetNetId(e.Key);
                    if (id != 0) rows.Add(new KeyValuePair<uint, ChirpLinkSystem.CachedChirpData>(id, e.Value));
                }
            }
            rows.Sort((a, b) => a.Key.CompareTo(b.Key));
            int start = Math.Max(0, rows.Count - MaxChirpLinks);
            w.Write(rows.Count - start);
            for (int i = start; i < rows.Count; i++)
            {
                w.Write(rows[i].Key);
                WriteName(w, rows[i].Value.m_Sender);
                ChirpLinkSystem.CachedEntityName[] links = rows[i].Value.m_Links;
                int count = links == null ? -1 : Math.Min(links.Length, MaxLinksPerChirp);
                w.Write(count);
                for (int j = 0; j < count; j++) WriteName(w, links[j]);
            }
        }

        private void WriteName(NetWriter w, ChirpLinkSystem.CachedEntityName name)
        {
            // raw net ids instead of entity references: a name must arrive even when its entity does not replicate
            w.Write(name.m_Entity == Entity.Null ? 0u : _ids.GetNetId(name.m_Entity));
            w.Write(name.m_Name);
        }

        private static ReceivedName ReadName(NetReader r)
        {
            var n = new ReceivedName();
            r.Read(out n.Id);
            r.Read(out n.Name);
            return n;
        }

        private void ReadChirpLinks(NetReader r)
        {
            r.Read(out int n);
            if (n < 0 || n > MaxChirpLinks) throw new InvalidOperationException("chirp links: bad count " + n);
            var names = new Dictionary<uint, ReceivedChirp>(n);
            for (int i = 0; i < n; i++)
            {
                r.Read(out uint id);
                var c = new ReceivedChirp { Sender = ReadName(r) };
                r.Read(out int links);
                if (links < -1 || links > MaxLinksPerChirp) throw new InvalidOperationException("chirp links: bad link count " + links);
                c.HasLinks = links >= 0;
                c.Links = new ReceivedName[Math.Max(0, links)];
                for (int j = 0; j < c.Links.Length; j++) c.Links[j] = ReadName(r);
                names[id] = c;
            }
            // chirps listed for the first time get the host's names (a known chirp's names do not change)
            foreach (uint id in names.Keys)
            {
                if (!_chirpNames.ContainsKey(id)) _chirpPending.Add(id);
            }
            _chirpPending.RemoveWhere(id => !names.ContainsKey(id));
            _chirpNames = names;
        }

        /// <summary>w3/audit: client end of the after-apply step (chirp names written after the chirp's first frame).</summary>
        private void PanelsAfterApply()
        {
            try
            {
                ReassertChirpNames();
            }
            catch (Exception ex)
            {
                Once("chirp names", "global state: writing chirp names failed", ex);
            }
        }

        /// <summary>w3/audit: forgets received panel state (session start or end).</summary>
        private void ResetPanels()
        {
            _chirpNames = new Dictionary<uint, ReceivedChirp>();
            _chirpPending.Clear();
            _followedBase.Clear();
            _followPending.Clear();
        }

        // ------------------------------------------------------------------ followed citizens (player action)

        /// <summary>The "Follow" channel: client deltas (citizen net id → 1 follow, 0 unfollow). The followed list is part of
        /// the city (50 citizens, life-path entries), so it is shared: the host follows through the vanilla
        /// <c>LifePathEventSystem</c> and agent replication brings <c>Followed</c> and the life path to everyone.</summary>
        private static readonly uint FollowChannel = GlobalChannel.Id("Follow");

        private void ReadFollowed(HashSet<uint> into)
        {
            into.Clear();
            if (!_hasFollowedQuery || _followedQuery.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> citizens = _followedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity c in citizens)
                {
                    uint id = _ids.GetNetId(c);
                    if (id != 0) into.Add(id);
                }
            }
            finally
            {
                citizens.Dispose();
            }
        }

        private void DetectFollowed()
        {
            ReadFollowed(_followedNow);
            if (LocalAllows != null && !LocalAllows(RequiredPermission(FollowChannel)))
            {
                RevertFollowed();
                return;
            }
            foreach (uint id in _followedNow)
            {
                if (!_followedBase.Contains(id)) _followPending[id] = 1;
            }
            foreach (uint id in _followedBase)
            {
                if (!_followedNow.Contains(id)) _followPending[id] = 0;
            }
            _followedBase.Clear();
            _followedBase.UnionWith(_followedNow);
        }

        /// <summary>
        /// polish: a viewer's client undoes its own follow/unfollow (the host refuses it anyway, see
        /// <see cref="RequiredPermission"/>), so the shared followed list never diverges locally. The vanilla
        /// <c>LifePathEventSystem</c> calls are used in reverse; an unfollow already marked the local life-path entries
        /// deleted, the host's agent replication brings them back with the citizen.
        /// </summary>
        private void RevertFollowed()
        {
            EntityManager em = _world.EntityManager;
            bool any = false;
            foreach (uint id in _followedNow)
            {
                if (_followedBase.Contains(id)) continue;
                Entity c = _ids.GetEntity(id);
                if (Live(em, c)) _lifePath.UnfollowCitizen(c);
                any = true;
            }
            foreach (uint id in _followedBase)
            {
                if (_followedNow.Contains(id)) continue;
                Entity c = _ids.GetEntity(id);
                if (Live(em, c) && em.HasComponent<Game.Citizens.Citizen>(c)) _lifePath.FollowCitizen(c);
                any = true;
            }
            if (!any) return;
            double now = Now;
            if (now - _lastFollowToast > 5.0)
            {
                _lastFollowToast = now;
                SkylineTogether.Session.SessionManager.Notify(Settings.LocaleSource.Resolve("st.toast.viewerFollow"));
            }
        }

        private double _lastFollowToast = double.NegativeInfinity;

        /// <summary>Host: entities to re-send to a peer whose channel proposal was refused (the followed citizens, so a
        /// client that followed anyway gets the host's state of them back).</summary>
        private IReadOnlyList<uint> RefusedIds(uint channel, byte[] payload)
        {
            if (channel != FollowChannel || payload == null) return Array.Empty<uint>();
            try
            {
                var entries = new List<KeyValuePair<uint, int>>();
                GlobalPayload.ReadInts(payload, 0, payload.Length, entries);
                var ids = new List<uint>(Math.Min(entries.Count, 64));
                foreach (KeyValuePair<uint, int> e in entries)
                {
                    if (ids.Count >= 64) break; // a refusal never turns into a large resend
                    ids.Add(e.Key);
                }
                return ids;
            }
            catch (Exception)
            {
                return Array.Empty<uint>(); // malformed: nothing to correct
            }
        }

        private void RebaselineFollowed()
        {
            ReadFollowed(_followedBase);
        }

        private void FlushFollowed(SyncBatch.Writer w)
        {
            if (_followPending.Count == 0) return;
            var list = new List<KeyValuePair<uint, int>>(_followPending);
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            Put(w, FollowChannel, GlobalPayload.WriteInts(GlobalPayloadMode.Delta, list));
            _followPending.Clear();
        }

        /// <summary>Host: a client's channel proposal handled here; false for every other channel.</summary>
        private bool ApplyPanelProposal(PeerId peer, uint channel, byte[] payload)
        {
            if (channel != FollowChannel) return false;
            var entries = new List<KeyValuePair<uint, int>>();
            if (!ReadDelta(GlobalPayload.ReadInts(payload, 0, payload.Length, entries), peer, channel)) return true;
            EntityManager em = _world.EntityManager;
            foreach (KeyValuePair<uint, int> e in entries)
            {
                Entity citizen = _ids.GetEntity(e.Key);
                if (!Live(em, citizen) || !em.HasComponent<Game.Citizens.Citizen>(citizen)) continue;
                bool followed = em.HasComponent<Game.Citizens.Followed>(citizen);
                if (e.Value != 0 && !followed) _lifePath.FollowCitizen(citizen);
                else if (e.Value == 0 && followed) _lifePath.UnfollowCitizen(citizen);
            }
            return true;
        }

        /// <summary>
        /// Client, after the frame's apply: writes the host's names of newly listed chirps into the local cache. The
        /// local <c>ChirpLinkSystem</c> caches a chirp's names in the frame the chirp appears (from entities this client
        /// may not have), so an entry is written once that frame is over; the write touches the chirp, which makes the
        /// chirper panel rebind it.
        /// </summary>
        private void ReassertChirpNames()
        {
            if (_chirpPending.Count == 0) return;
            Dictionary<Entity, ChirpLinkSystem.CachedChirpData> cache = ChirpCache();
            if (cache == null)
            {
                _chirpPending.Clear();
                return;
            }
            EntityManager em = _world.EntityManager;
            _chirpDone.Clear();
            foreach (uint id in _chirpPending)
            {
                if (!_chirpNames.TryGetValue(id, out ReceivedChirp received))
                {
                    _chirpDone.Add(id);
                    continue;
                }
                Entity chirp = _ids.GetEntity(id);
                if (chirp == Entity.Null || !em.Exists(chirp) || !em.HasComponent<Game.Triggers.Chirp>(chirp)) continue;
                if (em.HasComponent<Game.Common.Created>(chirp)) continue; // the local cache entry is written this frame
                cache[chirp] = Build(received);
                em.SetComponentData(chirp, em.GetComponentData<Game.Triggers.Chirp>(chirp)); // change version: rebind
                _chirpDone.Add(id);
            }
            foreach (uint id in _chirpDone) _chirpPending.Remove(id);
        }

        private ChirpLinkSystem.CachedChirpData Build(ReceivedChirp c)
        {
            var data = new ChirpLinkSystem.CachedChirpData { m_Sender = Resolve(c.Sender) };
            if (c.HasLinks)
            {
                data.m_Links = new ChirpLinkSystem.CachedEntityName[c.Links.Length];
                for (int i = 0; i < c.Links.Length; i++) data.m_Links[i] = Resolve(c.Links[i]);
            }
            return data;
        }

        private ChirpLinkSystem.CachedEntityName Resolve(ReceivedName n) => new ChirpLinkSystem.CachedEntityName
        {
            m_Entity = n.Id == 0 ? Entity.Null : _ids.GetEntity(n.Id),
            m_Name = n.Name,
        };

        // ------------------------------------------------------------------ placed signature buildings

        private void WriteSignatures(NetWriter w)
        {
            CollectSignatures(_signatureIds);
            w.Write(_signatureIds.Count);
            foreach (uint id in _signatureIds) w.Write(id);
        }

        private void ReadSignatures(NetReader r)
        {
            r.Read(out int n);
            var target = new List<uint>(CheckedLength(n, "signature buildings"));
            for (int i = 0; i < n; i++)
            {
                r.Read(out uint id);
                target.Add(id);
            }
            CollectSignatures(_signatureIds);
            IdSetDiff.Compute(_signatureIds, target, _signatureAdd, _signatureRemove);
            EntityManager em = _world.EntityManager;
            foreach (uint id in _signatureAdd)
            {
                Entity prefab = _ids.GetEntity(id);
                if (prefab != Entity.Null && em.Exists(prefab) && em.HasComponent<SignatureBuildingData>(prefab) && !em.HasComponent<PlacedSignatureBuildingData>(prefab))
                {
                    em.AddComponent<PlacedSignatureBuildingData>(prefab);
                }
            }
            foreach (uint id in _signatureRemove)
            {
                Entity prefab = _ids.GetEntity(id);
                if (prefab != Entity.Null && em.Exists(prefab) && em.HasComponent<PlacedSignatureBuildingData>(prefab))
                {
                    em.RemoveComponent<PlacedSignatureBuildingData>(prefab);
                }
            }
        }

        private void CollectSignatures(List<uint> into)
        {
            into.Clear();
            if (!_hasSignatureQuery || _placedSignatures.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> prefabs = _placedSignatures.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity p in prefabs)
                {
                    uint id = _ids.GetNetId(p);
                    if (id != 0) into.Add(id);
                }
            }
            finally
            {
                prefabs.Dispose();
            }
            into.Sort();
        }
    }
}
