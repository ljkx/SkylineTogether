using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Colossal.Serialization.Entities;
using Game.City;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using SkylineTogether.Core;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Sync;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// Economy and city state that lives in systems or singletons (ARCH §2.2, §4.10.1, §4.10.5), both directions.
    /// <para>
    /// Host → clients, about once a second and only when a channel's bytes changed (plus a 30 s keyframe): the tax
    /// table (<c>TaxSystem.GetTaxRates</c>), service budgets (<c>ServiceBudgetData</c>), custom names
    /// (<c>NameSystem</c>, keyed by net id), the city name, and the systems' own <c>ISerializable</c> state of the three
    /// demand systems and of the household and workplace counters the info panels read (side-effect-free
    /// <c>Deserialize</c> with the codec's full-tag context, R11 §5.2-5.3). Money, loan, fees, city policies, XP,
    /// milestone, development points, population and tourism live on the City entity, which the
    /// <see cref="SyncController"/> already value-scans every second; district, building and line policies are entity
    /// components and ride along with those entities (the vanilla policy system tags them <c>Updated</c>).
    /// </para>
    /// <para>
    /// Clients → host: changes the local player makes in the vanilla UI (taxes, budgets, fees, loan, policies of any
    /// target, renames, city name) are detected by comparing the state with the last converged state at the start and
    /// the end of every frame (state written by the appliers is re-baselined, so it is never proposed back) and sent
    /// as deltas; policy changes are taken from the vanilla <c>Modify</c> event entities. The host applies them through
    /// the vanilla entry points (last writer wins) and the result flows back to everyone. After a local change the
    /// channel is held for a moment, so host state sent before the host saw the change does not flick the UI back.
    /// </para>
    /// All work runs on the main thread inside the sync systems; every channel is isolated by try/catch.
    /// </summary>
    public sealed partial class GlobalStateSync
    {
        private const double HostInterval = 1.0;
        private const double KeyframeInterval = 30.0;
        private const double HoldSeconds = 1.5;
        private const int MaxBudget = GlobalProposalRules.MaxBudget;

        private readonly ILog _log;
        private readonly ChannelSendGate _send = new ChannelSendGate(KeyframeInterval);
        private readonly ChannelHold _hold = new ChannelHold(HoldSeconds);
        private readonly HashSet<string> _loggedOnce = new HashSet<string>();
        private readonly List<BlobChannel> _blobs = new List<BlobChannel>();
        private readonly ChannelPacer _pacer = new ChannelPacer();

        private World _world;
        private EntityCodec _codec;
        private NetIdSystem _ids;
        private TaxSystem _tax;
        private CityServiceBudgetSystem _budgets;
        private Game.Tools.LoanSystem _loan;
        private Game.UI.NameSystem _names;
        private CityConfigurationSystem _config;
        private CitySystem _city;
        private EntityQuery _budgetQuery;
        private EntityQuery _modifyQuery;
        private EntityQuery _feeParameters; // w9/perf (trust): fee maxima and adjustability
        private EntityArchetype _modifyArchetype;
        private FieldInfo _namesField;
        private double _lastHost = double.NegativeInfinity;

        // client: last converged state
        private bool _primed;
        private int[] _taxBase = Array.Empty<int>();
        private readonly Dictionary<uint, int> _budgetBase = new Dictionary<uint, int>();
        private readonly Dictionary<uint, float> _feeBase = new Dictionary<uint, float>();
        private int _loanBase;
        private readonly Dictionary<uint, string> _nameBase = new Dictionary<uint, string>();
        private string _cityNameBase = "";

        // client: changes waiting to be proposed, and fee values to re-assert while the fee channel is held
        private readonly Dictionary<uint, int> _taxPending = new Dictionary<uint, int>();
        private readonly Dictionary<uint, int> _budgetPending = new Dictionary<uint, int>();
        private readonly Dictionary<uint, float> _feePending = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> _feePredicted = new Dictionary<uint, float>();
        private int? _loanPending;
        private readonly Dictionary<uint, string> _namePending = new Dictionary<uint, string>();
        private string _cityNamePending;
        private readonly List<PolicyChange> _policyPending = new List<PolicyChange>();

        // scratch
        private readonly Dictionary<uint, int> _intScratch = new Dictionary<uint, int>();
        private readonly Dictionary<uint, float> _floatScratch = new Dictionary<uint, float>();
        private readonly Dictionary<uint, string> _stringScratch = new Dictionary<uint, string>();
        private readonly List<KeyValuePair<uint, int>> _intList = new List<KeyValuePair<uint, int>>();
        private readonly List<KeyValuePair<uint, float>> _floatList = new List<KeyValuePair<uint, float>>();
        private readonly List<KeyValuePair<uint, string>> _stringList = new List<KeyValuePair<uint, string>>();
        private readonly List<uint> _removed = new List<uint>();
        private readonly List<PolicyChange> _policyList = new List<PolicyChange>();
        private readonly List<KeyValuePair<uint, byte[]>> _released = new List<KeyValuePair<uint, byte[]>>();
        private readonly List<Entity> _entityScratch = new List<Entity>();

        /// <summary>A system whose own <c>ISerializable</c> state (or a custom slice of state) is mirrored host → clients.</summary>
        private sealed class BlobChannel
        {
            public string Name;
            public uint Id;
            public ISerializable State;
            /// <summary>A custom host serializer (null: <see cref="State"/>'s own <c>Serialize</c>).</summary>
            public Action<NetWriter> Write;
            /// <summary>A custom client deserializer (null: <see cref="State"/>'s own <c>Deserialize</c>).</summary>
            public Action<NetReader> Read;
            /// <summary>Host: the system's outstanding writers (public getter), completed before reading.</summary>
            public Func<JobHandle> HostWriters;
            /// <summary>Client: the system's private job handles, completed before writing.</summary>
            public object Owner;
            public FieldInfo[] ClientHandles;
            /// <summary>Client work before the state is written (completing the readers of a public getter).</summary>
            public Action ClientBefore;
            /// <summary>Minimum seconds between two offers (0: every 1 Hz pass); dirty channels are always due.</summary>
            public double Interval;
            public bool Disabled;
            /// <summary>
            /// Client: the byte length of this game's own encoding of the state, for channels written by the
            /// system's own <c>Deserialize</c> (0: not known yet, -1: not checkable). Those methods write into fixed-size
            /// native arrays with counts read from the payload (the happiness factors: 16 per count into 416 entries) and
            /// no bounds check in a player build, so a payload of another size is never handed to them.
            /// </summary>
            public int ExpectedLength;
        }

        /// <summary>
        /// The private job handles completed on a client before a channel's state is written (probed at startup,
        /// <see cref="Diagnostics.Probes"/>).
        /// </summary>
        public static readonly (Type Type, string Field)[] ClientJobHandleFields =
        {
            (typeof(ResidentialDemandSystem), "m_ReadDependencies"),
            (typeof(ResidentialDemandSystem), "m_WriteDependencies"),
            (typeof(CommercialDemandSystem), "m_ReadDependencies"),
            (typeof(CommercialDemandSystem), "m_WriteDependencies"),
            (typeof(IndustrialDemandSystem), "m_ReadDependencies"),
            (typeof(IndustrialDemandSystem), "m_WriteDependencies"),
            (typeof(CountHouseholdDataSystem), "m_HouseholdDataReadDependencies"),
            (typeof(CountHouseholdDataSystem), "m_HouseholdDataWriteDependencies"),
        };

        public GlobalStateSync(ILog log)
        {
            _log = log;
            // perf pass: the client runs the detectors twice and the baseline once per frame; their delegates are made once
            _detectTax = () => DetectTax(_detectNow);
            _detectBudgets = () => DetectBudgets(_detectNow);
            _detectFees = () => DetectFees(_detectNow);
            _detectLoan = DetectLoan;
            _detectNames = () => DetectNames(_detectNow);
            _detectCityName = DetectCityName;
            _detectFollowed = DetectFollowed;
            _rebaseline = RebaselineAll;
        }

        private readonly Action _detectTax, _detectBudgets, _detectFees, _detectLoan, _detectNames, _detectCityName, _detectFollowed, _rebaseline;
        private double _detectNow;

        // ---- gameplay branch ----

        /// <summary>Semantic intents and the unlock set (their channels are routed there).</summary>
        public ActionSync Actions;

        /// <summary>Host: whether a peer's role allows a permission (ARCH §5.6).</summary>
        public Func<PeerId, Permission, bool> Allows;

        /// <summary>Client: whether the local role allows a permission (null: allowed). Used to undo a viewer's local
        /// edits of shared state that no tool gate covers (following citizens), before they are proposed.</summary>
        public Func<Permission, bool> LocalAllows;

        /// <summary>Host: a refused edit; the controller tells the peer and re-sends the listed entities.</summary>
        public Action<PeerId, RejectReason, IReadOnlyList<uint>> Reject;

        /// <summary>
        /// Work that creates <c>EndFrameBarrier</c> command buffers (<c>NameSystem.SetCustomName</c>,
        /// <c>CityServiceBudgetSystem.SetServiceBudget</c>): the barrier refuses them between its playback (frame start,
        /// where proposals are applied) and <c>AllowBarrier&lt;EndFrameBarrier&gt;</c> (SafeCommandBufferSystem.cs:15-22,
        /// R10 §0.4), so they run in <see cref="RunLate"/> at the end-of-frame capture.
        /// </summary>
        private readonly List<Action> _late = new List<Action>();

        private void RunLate()
        {
            if (_late.Count == 0) return;
            Action[] work = _late.ToArray();
            _late.Clear();
            foreach (Action a in work)
            {
                try
                {
                    a();
                }
                catch (Exception ex)
                {
                    Once("late", "global state: a deferred change failed", ex);
                }
            }
        }

        /// <summary>Host: permission check of one channel proposal; refuses it (and re-sends the state) when not allowed.</summary>
        private bool Permitted(PeerId peer, uint channel, Permission needed, byte[] payload)
        {
            if (Allows == null || Allows(peer, needed)) return true;
            _log.Info(LogCategory.Replication, "global state: " + peer + " may not change " + ChannelName(channel));
            // the channel's state goes out again, so the refused prediction is corrected (fees, loan and policies
            // live on entities the host scans anyway)
            if (channel == GlobalChannel.Tax || channel == GlobalChannel.Budget || channel == GlobalChannel.Name || channel == GlobalChannel.CityName) _send.MarkDirty(channel);
            Reject?.Invoke(peer, RejectReason.NoPermission, RefusedIds(channel, payload));
            return false;
        }

        private static double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;

        private bool Attached => _codec != null;

        /// <summary>Binds to the game world (once, from <c>SyncController.EnsureWorld</c>).</summary>
        public void Attach(World world, EntityCodec codec, NetIdSystem ids)
        {
            if (_codec != null) return;
            try
            {
                _world = world;
                _tax = world.GetOrCreateSystemManaged<TaxSystem>();
                _budgets = world.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
                _loan = world.GetOrCreateSystemManaged<Game.Tools.LoanSystem>();
                _names = world.GetOrCreateSystemManaged<Game.UI.NameSystem>();
                _config = world.GetOrCreateSystemManaged<CityConfigurationSystem>();
                _city = world.GetOrCreateSystemManaged<CitySystem>();
                EntityManager em = world.EntityManager;
                _budgetQuery = em.CreateEntityQuery(ComponentType.ReadOnly<ServiceBudgetData>());
                _modifyQuery = em.CreateEntityQuery(ComponentType.ReadOnly<Game.Common.Event>(), ComponentType.ReadOnly<Modify>());
                _feeParameters = em.CreateEntityQuery(ComponentType.ReadOnly<ServiceFeeParameterData>());
                // the archetype PoliciesUISystem uses for its events (PoliciesUISystem.cs:157)
                _modifyArchetype = em.CreateArchetype(ComponentType.ReadWrite<Game.Common.Event>(), ComponentType.ReadWrite<Modify>());
                _namesField = Diagnostics.Probes.NameSystemNames;
                if (_namesField == null) _log.Warn(LogCategory.Replication, "NameSystem.m_Names not found: custom names will not sync");

                AddBlob<ResidentialDemandSystem>("Demand.Residential", s => { s.GetLowDensityDemandFactors(out JobHandle d); return d; }, "m_ReadDependencies", "m_WriteDependencies");
                AddBlob<CommercialDemandSystem>("Demand.Commercial", s => { s.GetDemandFactors(out JobHandle d); return d; }, "m_ReadDependencies", "m_WriteDependencies");
                AddBlob<IndustrialDemandSystem>("Demand.Industrial", s => { s.GetIndustrialDemandFactors(out JobHandle d); return d; }, "m_ReadDependencies", "m_WriteDependencies");
                AddBlob<CountHouseholdDataSystem>("Count.Household", s => { s.GetResourceNeeds(out JobHandle d); return d; }, "m_HouseholdDataReadDependencies", "m_HouseholdDataWriteDependencies");
                AddBlob<CountWorkplacesSystem>("Count.Workplaces", null);
                AttachPanels(); // info panels, budget inputs, chirper names, signature buildings
                _codec = codec;
                _ids = ids;
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Replication, "global state sync unavailable", ex);
            }
        }

        private void AddBlob<T>(string name, Func<T, JobHandle> hostWriters, params string[] clientHandles) where T : ComponentSystemBase
        {
            AddBlob(name, 0, hostWriters, clientHandles);
        }

        /// <summary><see cref="AddBlob{T}(string, Func{T, JobHandle}, string[])"/> with a minimum interval.</summary>
        private void AddBlob<T>(string name, double interval, Func<T, JobHandle> hostWriters, params string[] clientHandles) where T : ComponentSystemBase
        {
            T system = _world.GetOrCreateSystemManaged<T>();
            if (!(system is ISerializable state))
            {
                _log.Warn(LogCategory.Replication, name + ": " + typeof(T).Name + " is not serializable; not synced");
                return;
            }
            var handles = new List<FieldInfo>();
            foreach (string field in clientHandles)
            {
                FieldInfo f = Diagnostics.Probes.JobHandleField(typeof(T), field);
                if (f != null) handles.Add(f);
                else _log.Debug(LogCategory.Replication, name + ": job handle " + field + " not found");
            }
            _blobs.Add(new BlobChannel
            {
                Name = name,
                Id = GlobalChannel.Id(name),
                State = state,
                Owner = system,
                HostWriters = hostWriters == null ? (Func<JobHandle>)null : () => hostWriters(system),
                ClientHandles = handles.ToArray(),
                Interval = interval,
            });
        }

        /// <summary>
        /// A channel with its own serializer pair (state that is not a whole system blob: a getter's arrays, a
        /// private cache, prefab components). The reader must consume exactly what the writer wrote.
        /// </summary>
        private void AddCustom(string name, double interval, Action<NetWriter> write, Action<NetReader> read, Action clientBefore = null, Func<JobHandle> hostWriters = null)
        {
            _blobs.Add(new BlobChannel
            {
                Name = name,
                Id = GlobalChannel.Id(name),
                Write = write,
                Read = read,
                ClientBefore = clientBefore,
                HostWriters = hostWriters, // the jobs that write what Write reads
                ClientHandles = Array.Empty<FieldInfo>(),
                Interval = interval,
            });
        }

        /// <summary>Session ended or started: forget every baseline, hold and send record.</summary>
        public void Reset()
        {
            _send.Clear();
            _pacer.Clear();
            _late.Clear(); // gameplay branch
            _hold.Clear();
            _primed = false;
            _lastHost = double.NegativeInfinity;
            _taxPending.Clear();
            _budgetPending.Clear();
            _feePending.Clear();
            _feePredicted.Clear();
            _loanPending = null;
            _namePending.Clear();
            _cityNamePending = null;
            _policyPending.Clear();
            ResetPanels();
            foreach (BlobChannel b in _blobs) b.Disabled = false;
        }

        /// <summary>Host: send every channel again with the next capture (a peer is joining).</summary>
        public void ResendAll() => _send.MarkAllDirty();

        // ================================================================== host

        /// <summary>Host, end of frame: appends the channels whose state changed (≈1 Hz) or that are dirty.</summary>
        public void HostCapture(SyncBatch.Writer w)
        {
            if (!Attached) return;
            RunLate(); // gameplay branch: proposals that need EndFrameBarrier (names, budgets)
            double now = Now;
            bool due = now - _lastHost >= HostInterval;
            if (!due && !_send.AnyDirty) return;
            if (due) _lastHost = now;
            Offer(w, GlobalChannel.Tax, due, now, BuildTax);
            Offer(w, GlobalChannel.Budget, due, now, BuildBudgets);
            Offer(w, GlobalChannel.Name, due, now, BuildNames);
            Offer(w, GlobalChannel.CityName, due, now, BuildCityName);
            foreach (BlobChannel b in _blobs)
            {
                if (b.Disabled) continue;
                BlobChannel channel = b;
                // panel statistics may ask for a longer interval than the 1 Hz pass
                bool dirty = _send.IsDirty(b.Id);
                if (!dirty && !(due && _pacer.Due(b.Id, b.Interval, now, false))) continue;
                if (dirty) _pacer.Due(b.Id, b.Interval, now, true);
                Offer(w, b.Id, true, now, () => BuildBlob(channel));
            }
            _send.EndPass();
        }

        private void Offer(SyncBatch.Writer w, uint channel, bool due, double now, Func<byte[]> build)
        {
            if (!due && !_send.IsDirty(channel)) return;
            byte[] payload = null;
            try
            {
                payload = build();
            }
            catch (Exception ex)
            {
                Once("build " + channel, "global state: reading channel " + ChannelName(channel) + " failed", ex);
            }
            if (payload == null)
            {
                _send.ClearDirty(channel);
                return;
            }
            ulong hash = XxHash64.Hash(payload, 0, payload.Length);
            if (_send.ShouldSend(channel, hash, now)) w.Global(channel, payload, 0, payload.Length);
        }

        private byte[] BuildTax()
        {
            int[] rates = ReadTax();
            _intList.Clear();
            for (int i = 0; i < rates.Length; i++) _intList.Add(new KeyValuePair<uint, int>((uint)i, rates[i]));
            return GlobalPayload.WriteInts(GlobalPayloadMode.Full, _intList);
        }

        private byte[] BuildBudgets()
        {
            _intScratch.Clear();
            if (!ReadBudgets(_intScratch)) return null;
            _intList.Clear();
            _intList.AddRange(_intScratch);
            _intList.Sort((a, b) => a.Key.CompareTo(b.Key));
            return GlobalPayload.WriteInts(GlobalPayloadMode.Full, _intList);
        }

        private byte[] BuildNames()
        {
            _stringScratch.Clear();
            if (!ReadNames(_stringScratch)) return null;
            _stringList.Clear();
            _stringList.AddRange(_stringScratch);
            _stringList.Sort((a, b) => a.Key.CompareTo(b.Key));
            return GlobalPayload.WriteStrings(GlobalPayloadMode.Full, _stringList);
        }

        private byte[] BuildCityName()
        {
            _stringList.Clear();
            _stringList.Add(new KeyValuePair<uint, string>(0, _config.cityName ?? ""));
            return GlobalPayload.WriteStrings(GlobalPayloadMode.Full, _stringList);
        }

        private byte[] BuildBlob(BlobChannel b)
        {
            b.HostWriters?.Invoke().Complete();
            byte[] bytes = _codec.WriteBlob(writer =>
            {
                if (b.Write != null) b.Write(writer);
                else b.State.Serialize(writer);
            });
            if (bytes == null) Once("unresolved " + b.Name, "global state: " + b.Name + " references an unsynced entity; skipped", null);
            return bytes;
        }

        /// <summary>Host: a client's proposal (deltas only). Values are validated and applied through vanilla paths.</summary>
        private void ApplyProposal(PeerId peer, uint channel, byte[] payload)
        {
            EntityManager em = _world.EntityManager;
            // gameplay branch: semantic intents (dev tree, map tiles, historical, vehicles, districts, colours)
            if (channel == GlobalChannel.Action)
            {
                Actions?.ApplyProposal(peer, payload);
                return;
            }
            if (!Permitted(peer, channel, RequiredPermission(channel), payload)) return;
            if (ApplyPanelProposal(peer, channel, payload)) return; // followed citizens
            _proposalPeer = peer;
            if (channel == GlobalChannel.Tax)
            {
                if (!ReadDelta(GlobalPayload.ReadInts(payload, 0, payload.Length, Cleared(_intList)), peer, channel)) return;
                // TaxationUISystem completes the readers the same way before it writes (TaxationUISystem.cs:275)
                _tax.Readers.Complete();
                NativeArray<int> rates = _tax.GetTaxRates();
                if (!WithinLimit(_intList.Count, channel)) return;
                foreach (KeyValuePair<uint, int> e in _intList)
                {
                    if (e.Key < (uint)rates.Length) rates[(int)e.Key] = GlobalProposalRules.ClampTax(e.Value);
                }
                // w9/perf (trust): the game's own clamp cascade (total, area, job level and resource limits of
                // TaxParameterData), as its tax panel setters apply it; the raw offsets alone could exceed every limit
                TaxParameterData limits = _tax.GetTaxParameterData(); // loads the limits the setters clamp with
                if (limits.m_TotalTaxLimits.x < limits.m_TotalTaxLimits.y) _tax.TaxRate = _tax.TaxRate;
                _send.MarkDirty(channel);
            }
            else if (channel == GlobalChannel.Budget)
            {
                if (!ReadDelta(GlobalPayload.ReadInts(payload, 0, payload.Length, Cleared(_intList)), peer, channel)) return;
                if (!WithinLimit(_intList.Count, channel)) return;
                var budgets = new List<KeyValuePair<uint, int>>(_intList);
                // gameplay branch: SetServiceBudget tags the budget entity through EndFrameBarrier: late phase
                _late.Add(() =>
                {
                    EntityManager lem = _world.EntityManager;
                    foreach (KeyValuePair<uint, int> e in budgets)
                    {
                        Entity service = _ids.GetEntity(e.Key);
                        if (service == Entity.Null || !lem.Exists(service) || !lem.HasComponent<ServiceData>(service)) continue;
                        // w9/perf (trust): only budgets the game's panel lets players adjust
                        if (!GlobalProposalRules.TryBudget(e.Value, lem.GetComponentData<ServiceData>(service).m_BudgetAdjustable, out int percentage)) continue;
                        _budgets.SetServiceBudget(service, percentage);
                    }
                    _send.MarkDirty(GlobalChannel.Budget);
                });
            }
            else if (channel == GlobalChannel.Fee)
            {
                if (!ReadDelta(GlobalPayload.ReadFloats(payload, 0, payload.Length, Cleared(_floatList)), peer, channel)) return;
                if (!WithinLimit(_floatList.Count, channel)) return;
                Entity city = _city.City;
                if (city == Entity.Null || !em.HasBuffer<ServiceFee>(city)) return;
                DynamicBuffer<ServiceFee> fees = em.GetBuffer<ServiceFee>(city);
                bool haveParameters = !_feeParameters.IsEmptyIgnoreFilter;
                ServiceFeeParameterData parameters = haveParameters ? _feeParameters.GetSingleton<ServiceFeeParameterData>() : default;
                foreach (KeyValuePair<uint, float> e in _floatList)
                {
                    var resource = (PlayerResource)(int)e.Key;
                    if (resource == PlayerResource.Parking || e.Key > 64) continue;
                    // w9/perf (trust): the game's fee maximum, and only fees its panel lets players adjust
                    FeeParameters limits = haveParameters ? parameters.GetFeeParameters(resource) : new FeeParameters { m_Adjustable = true, m_Max = GlobalProposalRules.MaxFee };
                    if (!GlobalProposalRules.TryFee(e.Value, limits.m_Adjustable, limits.m_Max, out float fee)) continue;
                    // ServiceBudgetUISystem.SetServiceFee does exactly this (ServiceBudgetUISystem.cs:257-264)
                    ServiceFeeSystem.SetFee(resource, fees, fee);
                }
            }
            else if (channel == GlobalChannel.Loan)
            {
                if (!ReadDelta(GlobalPayload.ReadInts(payload, 0, payload.Length, Cleared(_intList)), peer, channel)) return;
                // LoanSystem clamps against the host's money and creditworthiness and applies in Modification1; w9/perf: one
                // loan exists, so only the last entry counts (was one ChangeLoan per entry, up to 65536)
                if (_intList.Count > 0) _loan.ChangeLoan(GlobalProposalRules.ClampLoan(_intList[_intList.Count - 1].Value));
            }
            else if (channel == GlobalChannel.Policy)
            {
                _policyList.Clear();
                if (!ReadDelta(GlobalPayload.ReadPolicies(payload, 0, payload.Length, _policyList), peer, channel)) return;
                if (!WithinLimit(_policyList.Count, channel)) return;
                foreach (PolicyChange c in _policyList)
                {
                    // gameplay branch: a target deleted meanwhile is a conflict (rollback of the client's prediction)
                    if (!CreateModifyEvent(em, c)) Reject?.Invoke(peer, RejectReason.Conflict, new[] { c.Target });
                }
            }
            else if (channel == GlobalChannel.Name)
            {
                if (!ReadDelta(GlobalPayload.ReadStrings(payload, 0, payload.Length, Cleared(_stringList)), peer, channel)) return;
                if (!WithinLimit(_stringList.Count, channel)) return;
                var names = new List<KeyValuePair<uint, string>>(_stringList.Count);
                foreach (KeyValuePair<uint, string> e in _stringList) names.Add(new KeyValuePair<uint, string>(e.Key, GlobalProposalRules.CleanName(e.Value))); // w9/perf (trust)
                // gameplay branch: NameSystem.SetCustomName uses EndFrameBarrier (NameSystem.cs:214-241): late phase
                _late.Add(() =>
                {
                    EntityManager lem = _world.EntityManager;
                    foreach (KeyValuePair<uint, string> e in names)
                    {
                        Entity target = _ids.GetEntity(e.Key);
                        if (!Live(lem, target))
                        {
                            Reject?.Invoke(peer, RejectReason.Conflict, new[] { e.Key });
                            continue;
                        }
                        _names.SetCustomName(target, e.Value);
                    }
                    _send.MarkDirty(GlobalChannel.Name);
                });
            }
            else if (channel == GlobalChannel.CityName)
            {
                if (!ReadDelta(GlobalPayload.ReadStrings(payload, 0, payload.Length, Cleared(_stringList)), peer, channel)) return;
                foreach (KeyValuePair<uint, string> e in _stringList)
                {
                    if (GlobalProposalRules.TryCityName(e.Value, out string cityName)) _config.cityName = cityName; // w9/perf (trust): sanitised
                }
                _send.MarkDirty(channel);
            }
            else
            {
                Once("unknown proposal " + channel, "global state: " + peer + " proposed unknown channel 0x" + channel.ToString("X8", CultureInfo.InvariantCulture), null);
            }
        }

        private PeerId _proposalPeer;

        /// <summary>w9/perf (trust): a proposal with more entries than a panel edit can produce is dropped whole.</summary>
        private bool WithinLimit(int count, uint channel)
        {
            if (GlobalProposalRules.WithinEntryLimit(count)) return true;
            Once("entries " + channel, "global state: " + _proposalPeer + " proposed " + count + " " + ChannelName(channel) + " entries at once; ignored", null);
            return false;
        }

        /// <summary>gameplay branch: the permission a channel proposal needs (ARCH §5.6).</summary>
        private static Permission RequiredPermission(uint channel)
        {
            if (channel == GlobalChannel.Tax || channel == GlobalChannel.Budget || channel == GlobalChannel.Fee || channel == GlobalChannel.Loan) return Permission.Economy;
            if (channel == GlobalChannel.Policy) return Permission.Policies;
            if (channel == GlobalChannel.Name || channel == GlobalChannel.CityName) return Permission.Names;
            // the followed-citizen list is shared city state (50 slots, life-path entries), so viewers may not
            // change it; following is a cosmetic, city-wide choice like names
            if (channel == FollowChannel) return Permission.Names;
            return Permission.None;
        }

        private bool ReadDelta(GlobalPayloadMode mode, PeerId peer, uint channel)
        {
            if (mode == GlobalPayloadMode.Delta) return true;
            Once("mode " + channel, "global state: " + peer + " sent a full " + ChannelName(channel) + " state as a proposal; ignored", null);
            return false;
        }

        /// <summary>
        /// The event the vanilla policy panel creates; <c>Policies.ModifiedSystem</c> applies it this frame. False when
        /// the target no longer exists (a conflict); malformed changes are dropped and count as handled.
        /// </summary>
        private bool CreateModifyEvent(EntityManager em, PolicyChange c)
        {
            Entity target = _ids.GetEntity(c.Target);
            Entity policy = _ids.GetEntity(c.Policy);
            if (!Live(em, target)) return false;
            if (policy == Entity.Null || !em.Exists(policy)) return true;
            if (!em.HasComponent<PolicyData>(policy)) return true;
            if (!em.HasBuffer<Policy>(target) && !em.HasComponent<Game.Buildings.Extension>(target)) return true;
            // w9/perf (trust): a policy that is still locked cannot be enacted; a slider value stays within its range
            if (em.HasComponent<Locked>(policy) && em.IsComponentEnabled<Locked>(policy)) return true;
            bool hasRange = em.HasComponent<PolicySliderData>(policy);
            Colossal.Mathematics.Bounds1 range = hasRange ? em.GetComponentData<PolicySliderData>(policy).m_Range : default;
            if (!GlobalProposalRules.TryPolicyAdjustment(c.Adjustment, hasRange, range.min, range.max, out float adjustment)) return true;
            Entity ev = em.CreateEntity(_modifyArchetype);
            em.SetComponentData(ev, new Modify(target, policy, (c.Flags & (byte)PolicyFlags.Active) != 0, adjustment));
            return true;
        }

        // ================================================================== records (both roles)

        /// <summary>
        /// Applies the global records of a batch: on the host they are proposals of <paramref name="peer"/>, on a client
        /// the host's state.
        /// </summary>
        public void Apply(byte[] data, PeerId peer, bool asHost)
        {
            if (!Attached) return;
            // global records always travel in batches of their own (SyncController.CaptureGlobal), so entity batches
            // are recognised by their first record kind (offset 5: after the version byte and the record count) and
            // not decoded a further time
            if (data == null || SyncBatch.FirstRecordKind(data, 0, data.Length) != (byte)SyncRecordKind.Global) return;
            foreach (SyncRecord r in SyncBatch.Read(data, 0, data.Length))
            {
                if (r.Kind != SyncRecordKind.Global || r.Payload == null) continue;
                try
                {
                    if (asHost) ApplyProposal(peer, r.Channel, r.Payload);
                    else if (!_hold.TryDefer(r.Channel, r.Payload, Now)) ApplyFull(r.Channel, r.Payload);
                }
                catch (DecodeException ex)
                {
                    _log.Warn(LogCategory.Replication, "global state: malformed " + ChannelName(r.Channel) + " payload from " + peer + ": " + ex.Message);
                }
                catch (Exception ex)
                {
                    Once("apply " + r.Channel, "global state: applying " + ChannelName(r.Channel) + " failed", ex);
                }
            }
        }

        /// <summary>Client: converges a channel to the host's full state.</summary>
        private void ApplyFull(uint channel, byte[] payload)
        {
            EntityManager em = _world.EntityManager;
            // gameplay branch: the unlock set and the host's sub-element deletions
            if (Actions != null && Actions.ApplyFromHost(channel, payload)) return;
            if (channel == GlobalChannel.Tax)
            {
                if (GlobalPayload.ReadInts(payload, 0, payload.Length, Cleared(_intList)) != GlobalPayloadMode.Full) return;
                _tax.Readers.Complete();
                NativeArray<int> rates = _tax.GetTaxRates();
                foreach (KeyValuePair<uint, int> e in _intList)
                {
                    if (e.Key < (uint)rates.Length) rates[(int)e.Key] = e.Value;
                }
            }
            else if (channel == GlobalChannel.Budget)
            {
                if (GlobalPayload.ReadInts(payload, 0, payload.Length, Cleared(_intList)) != GlobalPayloadMode.Full) return;
                if (_budgetQuery.IsEmptyIgnoreFilter) return;
                Entity budget = _budgetQuery.GetSingletonEntity();
                // GetBuffer (read-write) completes the jobs that read the budgets (CityServiceBudgetSystem)
                DynamicBuffer<ServiceBudgetData> buffer = em.GetBuffer<ServiceBudgetData>(budget);
                // only live services (as the host checks), budgets clamped, one entry per service; a service the
                // host names that is not bound here yet keeps its current entry instead of vanishing from the list
                var wanted = new Dictionary<Entity, int>();
                var unresolved = 0;
                foreach (KeyValuePair<uint, int> e in _intList)
                {
                    Entity service = _ids.GetEntity(e.Key);
                    if (service == Entity.Null || !em.Exists(service) || !em.HasComponent<ServiceData>(service))
                    {
                        unresolved++;
                        continue;
                    }
                    wanted[service] = Math.Max(0, Math.Min(MaxBudget, e.Value));
                }
                buffer = em.GetBuffer<ServiceBudgetData>(budget);
                var kept = new List<ServiceBudgetData>();
                for (int i = 0; i < buffer.Length; i++)
                {
                    ServiceBudgetData old = buffer[i];
                    if (unresolved > 0 && !wanted.ContainsKey(old.m_Service) && old.m_Service != Entity.Null && em.Exists(old.m_Service)) kept.Add(old);
                }
                buffer = em.GetBuffer<ServiceBudgetData>(budget);
                buffer.Clear();
                foreach (ServiceBudgetData k in kept) buffer.Add(k);
                foreach (KeyValuePair<Entity, int> w in wanted) buffer.Add(new ServiceBudgetData { m_Service = w.Key, m_Budget = w.Value });
            }
            else if (channel == GlobalChannel.Name)
            {
                if (GlobalPayload.ReadStrings(payload, 0, payload.Length, Cleared(_stringList)) != GlobalPayloadMode.Full) return;
                // gameplay branch: SetCustomName uses EndFrameBarrier, refused at frame start: applied in the late phase
                var names = new List<KeyValuePair<uint, string>>(_stringList);
                _late.Add(() =>
                {
                    _stringList.Clear();
                    _stringList.AddRange(names);
                    ApplyNames(_world.EntityManager);
                });
            }
            else if (channel == GlobalChannel.CityName)
            {
                if (GlobalPayload.ReadStrings(payload, 0, payload.Length, Cleared(_stringList)) != GlobalPayloadMode.Full) return;
                foreach (KeyValuePair<uint, string> e in _stringList)
                {
                    if (!string.IsNullOrEmpty(e.Value) && e.Value != _config.cityName) _config.cityName = e.Value;
                }
            }
            else
            {
                foreach (BlobChannel b in _blobs)
                {
                    if (b.Id != channel) continue;
                    ApplyBlob(b, payload);
                    return;
                }
                Once("unknown state " + channel, "global state: unknown channel 0x" + channel.ToString("X8", CultureInfo.InvariantCulture) + " from the host; ignored", null);
            }
        }

        private void ApplyNames(EntityManager em)
        {
            Dictionary<Entity, string> local = NameTable();
            if (local == null) return;
            _stringScratch.Clear();
            foreach (KeyValuePair<uint, string> e in _stringList) _stringScratch[e.Key] = e.Value;
            foreach (KeyValuePair<uint, string> e in _stringScratch)
            {
                Entity target = _ids.GetEntity(e.Key);
                if (!Live(em, target)) continue;
                if (local.TryGetValue(target, out string current) && current == e.Value) continue;
                _names.SetCustomName(target, e.Value);
            }
            // names of synced entities the host does not have any more
            _entityScratch.Clear();
            foreach (KeyValuePair<Entity, string> e in local)
            {
                uint id = _ids.GetNetId(e.Key);
                if (id != 0 && !_stringScratch.ContainsKey(id) && Live(em, e.Key)) _entityScratch.Add(e.Key);
            }
            foreach (Entity e in _entityScratch) _names.SetCustomName(e, "");
        }

        private void ApplyBlob(BlobChannel b, byte[] payload)
        {
            if (b.Disabled) return;
            // a vanilla Deserialize only ever gets a payload of exactly this game's own size for the state
            if (b.Read == null && b.State != null)
            {
                if (b.ExpectedLength == 0)
                {
                    byte[] local = null;
                    try
                    {
                        local = BuildBlob(b);
                    }
                    catch (Exception)
                    {
                        // not checkable: refused below
                    }
                    b.ExpectedLength = local != null ? local.Length : -1;
                }
                // (a state this game cannot measure, because it references unsynced entities, keeps the bounded reader only)
                if (b.ExpectedLength > 0 && b.ExpectedLength != payload.Length)
                {
                    b.Disabled = true;
                    _log.Warn(LogCategory.Replication, "global state: " + b.Name + " from the host has " + payload.Length + " bytes, this game's state has "
                                                       + b.ExpectedLength + "; channel disabled for this session (game versions differ?)");
                    return;
                }
            }
            foreach (FieldInfo f in b.ClientHandles) ((JobHandle)f.GetValue(b.Owner)).Complete();
            b.HostWriters?.Invoke().Complete();
            b.ClientBefore?.Invoke();
            bool exact;
            try
            {
                exact = _codec.ReadBlob(payload, 0, payload.Length, reader =>
                {
                    if (b.Read != null) b.Read(reader);
                    else b.State.Deserialize(reader);
                });
            }
            catch (Exception ex)
            {
                // a malformed payload (overrun, bad length) disables the channel instead of escaping half-applied
                b.Disabled = true;
                _log.Warn(LogCategory.Replication, "global state: " + b.Name + " could not be applied (" + ex.GetType().Name + ": " + ex.Message + "); channel disabled for this session");
                return;
            }
            if (exact) return;
            b.Disabled = true;
            _log.Warn(LogCategory.Replication, "global state: " + b.Name + " did not decode exactly (format differs?); channel disabled for this session");
        }

        // ================================================================== client

        /// <summary>Client, frame start (before any host data is applied): detects local edits first, so no apply can
        /// overwrite an edit that was not proposed yet.</summary>
        public void BeforeApply()
        {
            if (!Attached) return;
            if (!_primed)
            {
                Rebaseline();
                _primed = true;
                return;
            }
            DetectLocal(Now);
        }

        /// <summary>Client, after the frame's host data was applied: releases expired holds, re-asserts predicted fees
        /// (the City value scan may carry pre-change fees) and takes the converged state as the new baseline.</summary>
        public void AfterApply()
        {
            if (!Attached || !_primed) return;
            double now = Now;
            _released.Clear();
            _hold.TakeReleased(now, _released);
            foreach (KeyValuePair<uint, byte[]> r in _released)
            {
                try
                {
                    ApplyFull(r.Key, r.Value);
                }
                catch (Exception ex)
                {
                    Once("apply " + r.Key, "global state: applying " + ChannelName(r.Key) + " failed", ex);
                }
            }
            if (_hold.IsHeld(GlobalChannel.Fee, now)) ReassertFees();
            else _feePredicted.Clear();
            Rebaseline();
            PanelsAfterApply(); // chirp names
        }

        /// <summary>Client, end of frame: detects local edits and policy events and appends them as proposals.</summary>
        public void ClientCapture(SyncBatch.Writer w)
        {
            if (!Attached) return;
            if (!_primed)
            {
                RunLate();
                Rebaseline();
                _primed = true;
                return;
            }
            DetectLocal(Now);
            CapturePolicies();
            // gameplay branch: host state deferred to this point (names) is applied after the local edits were taken,
            // and becomes the new baseline, so it is never proposed back
            if (_late.Count > 0)
            {
                RunLate();
                Rebaseline();
            }
            Flush(w);
        }

        private void DetectLocal(double now)
        {
            _detectNow = now;
            Guard("tax", _detectTax);
            Guard("budget", _detectBudgets);
            Guard("fee", _detectFees);
            Guard("loan", _detectLoan);
            Guard("names", _detectNames);
            Guard("city name", _detectCityName);
            Guard("followed citizens", _detectFollowed);
        }

        private void DetectTax(double now)
        {
            int[] rates = ReadTax();
            bool any = false;
            for (int i = 0; i < rates.Length; i++)
            {
                if (i < _taxBase.Length && _taxBase[i] == rates[i]) continue;
                _taxPending[(uint)i] = rates[i];
                any = true;
            }
            _taxBase = rates;
            if (any) _hold.Hold(GlobalChannel.Tax, now);
        }

        private void DetectBudgets(double now)
        {
            _intScratch.Clear();
            if (!ReadBudgets(_intScratch)) return;
            _intList.Clear();
            if (KeyedDiff.Diff(_budgetBase, _intScratch, _intList, null))
            {
                foreach (KeyValuePair<uint, int> e in _intList) _budgetPending[e.Key] = e.Value;
                _hold.Hold(GlobalChannel.Budget, now);
            }
            Copy(_intScratch, _budgetBase);
        }

        private void DetectFees(double now)
        {
            _floatScratch.Clear();
            if (!ReadFees(_floatScratch)) return;
            _floatList.Clear();
            if (KeyedDiff.Diff(_feeBase, _floatScratch, _floatList, null))
            {
                foreach (KeyValuePair<uint, float> e in _floatList)
                {
                    _feePending[e.Key] = e.Value;
                    _feePredicted[e.Key] = e.Value;
                }
                _hold.Hold(GlobalChannel.Fee, now);
            }
            Copy(_floatScratch, _feeBase);
        }

        private void DetectLoan()
        {
            if (!ReadLoan(out int amount)) return;
            if (amount != _loanBase) _loanPending = amount;
            _loanBase = amount;
        }

        private void DetectNames(double now)
        {
            _stringScratch.Clear();
            if (!ReadNames(_stringScratch)) return;
            _stringList.Clear();
            _removed.Clear();
            if (KeyedDiff.Diff(_nameBase, _stringScratch, _stringList, _removed, StringComparer.Ordinal))
            {
                foreach (KeyValuePair<uint, string> e in _stringList) _namePending[e.Key] = e.Value;
                foreach (uint id in _removed) _namePending[id] = "";
                _hold.Hold(GlobalChannel.Name, now);
            }
            Copy(_stringScratch, _nameBase);
        }

        private void DetectCityName()
        {
            string name = _config.cityName ?? "";
            if (!string.Equals(name, _cityNameBase, StringComparison.Ordinal))
            {
                _cityNamePending = name;
                _hold.Hold(GlobalChannel.CityName, Now);
            }
            _cityNameBase = name;
        }

        /// <summary>The policy events the local UI created this frame (they live until the end of the frame).</summary>
        private void CapturePolicies()
        {
            if (_modifyQuery.IsEmptyIgnoreFilter) return;
            NativeArray<Modify> events = _modifyQuery.ToComponentDataArray<Modify>(Allocator.Temp);
            try
            {
                foreach (Modify m in events)
                {
                    uint target = _ids.GetNetId(m.m_Entity);
                    uint policy = _ids.GetNetId(m.m_Policy);
                    if (target == 0 || policy == 0)
                    {
                        _log.Debug(LogCategory.Replication, "global state: a policy change on an unsynced target was not proposed");
                        continue;
                    }
                    _policyPending.Add(new PolicyChange { Target = target, Policy = policy, Flags = (byte)m.m_Flags, Adjustment = m.m_Adjustment });
                }
            }
            catch (Exception ex)
            {
                Once("policies", "global state: reading policy events failed", ex);
            }
            finally
            {
                events.Dispose();
            }
        }

        private void Flush(SyncBatch.Writer w)
        {
            try
            {
                if (_taxPending.Count > 0) Put(w, GlobalChannel.Tax, GlobalPayload.WriteInts(GlobalPayloadMode.Delta, Sorted(_taxPending)));
                if (_budgetPending.Count > 0) Put(w, GlobalChannel.Budget, GlobalPayload.WriteInts(GlobalPayloadMode.Delta, Sorted(_budgetPending)));
                if (_feePending.Count > 0) Put(w, GlobalChannel.Fee, GlobalPayload.WriteFloats(GlobalPayloadMode.Delta, new List<KeyValuePair<uint, float>>(_feePending)));
                if (_loanPending.HasValue) Put(w, GlobalChannel.Loan, GlobalPayload.WriteInts(GlobalPayloadMode.Delta, new[] { new KeyValuePair<uint, int>(0, _loanPending.Value) }));
                if (_policyPending.Count > 0) Put(w, GlobalChannel.Policy, GlobalPayload.WritePolicies(GlobalPayloadMode.Delta, _policyPending));
                if (_namePending.Count > 0) Put(w, GlobalChannel.Name, GlobalPayload.WriteStrings(GlobalPayloadMode.Delta, new List<KeyValuePair<uint, string>>(_namePending)));
                FlushFollowed(w);
                if (_cityNamePending != null && !string.IsNullOrWhiteSpace(_cityNamePending))
                {
                    Put(w, GlobalChannel.CityName, GlobalPayload.WriteStrings(GlobalPayloadMode.Delta, new[] { new KeyValuePair<uint, string>(0, _cityNamePending) }));
                }
            }
            catch (Exception ex)
            {
                Once("flush", "global state: writing proposals failed", ex);
            }
            _taxPending.Clear();
            _budgetPending.Clear();
            _feePending.Clear();
            _loanPending = null;
            _policyPending.Clear();
            _namePending.Clear();
            _cityNamePending = null;
        }

        private void Put(SyncBatch.Writer w, uint channel, byte[] payload)
        {
            w.Global(channel, payload, 0, payload.Length);
            _log.Debug(LogCategory.Replication, "global state: proposing " + ChannelName(channel) + " (" + payload.Length + " bytes)");
        }

        private void ReassertFees()
        {
            if (_feePredicted.Count == 0) return;
            EntityManager em = _world.EntityManager;
            Entity city = _city.City;
            if (city == Entity.Null || !em.HasBuffer<ServiceFee>(city)) return;
            DynamicBuffer<ServiceFee> fees = em.GetBuffer<ServiceFee>(city);
            foreach (KeyValuePair<uint, float> e in _feePredicted) ServiceFeeSystem.SetFee((PlayerResource)(int)e.Key, fees, e.Value);
        }

        private void Rebaseline() => Guard("baseline", _rebaseline);

        private void RebaselineAll()
        {
            _taxBase = ReadTax();
            _intScratch.Clear();
            if (ReadBudgets(_intScratch)) Copy(_intScratch, _budgetBase);
            _floatScratch.Clear();
            if (ReadFees(_floatScratch)) Copy(_floatScratch, _feeBase);
            if (ReadLoan(out int loan)) _loanBase = loan;
            _stringScratch.Clear();
            if (ReadNames(_stringScratch)) Copy(_stringScratch, _nameBase);
            _cityNameBase = _config.cityName ?? "";
            RebaselineFollowed();
        }

        // ================================================================== state readers

        private int[] ReadTax()
        {
            NativeArray<int> rates = _tax.GetTaxRates();
            return rates.IsCreated ? rates.ToArray() : Array.Empty<int>();
        }

        private bool ReadBudgets(Dictionary<uint, int> into)
        {
            if (_budgetQuery.IsEmptyIgnoreFilter) return false;
            EntityManager em = _world.EntityManager;
            DynamicBuffer<ServiceBudgetData> buffer = em.GetBuffer<ServiceBudgetData>(_budgetQuery.GetSingletonEntity(), true);
            for (int i = 0; i < buffer.Length; i++)
            {
                uint id = _ids.GetNetId(buffer[i].m_Service);
                if (id != 0) into[id] = buffer[i].m_Budget;
            }
            return true;
        }

        private bool ReadFees(Dictionary<uint, float> into)
        {
            EntityManager em = _world.EntityManager;
            Entity city = _city.City;
            if (city == Entity.Null || !em.Exists(city) || !em.HasBuffer<ServiceFee>(city)) return false;
            DynamicBuffer<ServiceFee> fees = em.GetBuffer<ServiceFee>(city, true);
            for (int i = 0; i < fees.Length; i++) into[(uint)(int)fees[i].m_Resource] = fees[i].m_Fee;
            return true;
        }

        private bool ReadLoan(out int amount)
        {
            amount = 0;
            EntityManager em = _world.EntityManager;
            Entity city = _city.City;
            if (city == Entity.Null || !em.Exists(city) || !em.HasComponent<Loan>(city)) return false;
            amount = em.GetComponentData<Loan>(city).m_Amount;
            return true;
        }

        /// <summary>Custom names of synced entities, keyed by net id (names of host-local entities stay local).</summary>
        private bool ReadNames(Dictionary<uint, string> into)
        {
            Dictionary<Entity, string> table = NameTable();
            if (table == null) return false;
            foreach (KeyValuePair<Entity, string> e in table)
            {
                uint id = _ids.GetNetId(e.Key);
                if (id != 0) into[id] = e.Value ?? "";
            }
            return true;
        }

        private Dictionary<Entity, string> NameTable() => _namesField?.GetValue(_names) as Dictionary<Entity, string>;

        // ================================================================== helpers

        /// <summary>
        /// Exists and is not being deleted: <c>NameSystem</c> and the policy system tag their target through command
        /// buffers that play back after this frame's clean-up, when a deleted entity is already gone.
        /// </summary>
        private static bool Live(EntityManager em, Entity e) => e != Entity.Null && em.Exists(e) && !em.HasComponent<Game.Common.Deleted>(e);

        private static List<KeyValuePair<uint, int>> Sorted(Dictionary<uint, int> d)
        {
            var list = new List<KeyValuePair<uint, int>>(d);
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            return list;
        }

        private static List<KeyValuePair<uint, TValue>> Cleared<TValue>(List<KeyValuePair<uint, TValue>> list)
        {
            list.Clear();
            return list;
        }

        private static void Copy<TValue>(Dictionary<uint, TValue> from, Dictionary<uint, TValue> to)
        {
            to.Clear();
            foreach (KeyValuePair<uint, TValue> e in from) to[e.Key] = e.Value;
        }

        private void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Once("detect " + what, "global state: checking " + what + " failed", ex);
            }
        }

        private void Once(string key, string message, Exception ex)
        {
            if (_loggedOnce.Add(key)) _log.Warn(LogCategory.Replication, message, ex);
        }

        // ================================================================== solo testing (record/replay verdict, ARCH §13.5)

        /// <summary>
        /// Host: the hash of every channel's last sent state (the recorder's checkpoints). Taxes, budgets, names and the
        /// city name are strict; the systems' own state blobs are compared as warnings (their client side may round
        /// their values on the way back).
        /// </summary>
        public void SentChannelHashes(List<Core.Replay.GlobalChannelHash> output)
        {
            if (!Attached) return;
            foreach (uint channel in new[] { GlobalChannel.Tax, GlobalChannel.Budget, GlobalChannel.Name, GlobalChannel.CityName })
            {
                if (_send.TryGetSent(channel, out ulong hash)) output.Add(new Core.Replay.GlobalChannelHash(channel, ChannelName(channel), hash, true));
            }
            foreach (BlobChannel b in _blobs)
            {
                if (!b.Disabled && _send.TryGetSent(b.Id, out ulong hash)) output.Add(new Core.Replay.GlobalChannelHash(b.Id, b.Name, hash, false));
            }
        }

        /// <summary>Client: the hashes of the same channels built from the local state with the host's own builders.</summary>
        public void LocalChannelHashes(IReadOnlyList<Core.Replay.GlobalChannelHash> wanted, List<Core.Replay.GlobalChannelHash> output)
        {
            if (!Attached) return;
            foreach (Core.Replay.GlobalChannelHash w in wanted)
            {
                byte[] payload = null;
                try
                {
                    if (w.Channel == GlobalChannel.Tax) payload = BuildTax();
                    else if (w.Channel == GlobalChannel.Budget) payload = BuildBudgets();
                    else if (w.Channel == GlobalChannel.Name) payload = BuildNames();
                    else if (w.Channel == GlobalChannel.CityName) payload = BuildCityName();
                    else
                    {
                        foreach (BlobChannel b in _blobs)
                        {
                            if (b.Id != w.Channel || b.Disabled) continue;
                            foreach (FieldInfo f in b.ClientHandles) ((JobHandle)f.GetValue(b.Owner)).Complete();
                            b.ClientBefore?.Invoke();
                            payload = BuildBlob(b);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Debug(LogCategory.Replication, "replay: reading " + w.Name + " failed: " + ex.Message);
                    payload = null;
                }
                if (payload != null) output.Add(new Core.Replay.GlobalChannelHash(w.Channel, w.Name, XxHash64.Hash(payload, 0, payload.Length), w.Strict));
            }
        }

        private string ChannelName(uint channel)
        {
            if (channel == GlobalChannel.Tax) return "taxes";
            if (channel == GlobalChannel.Budget) return "budgets";
            if (channel == GlobalChannel.Fee) return "fees";
            if (channel == GlobalChannel.Loan) return "loan";
            if (channel == GlobalChannel.Policy) return "policies";
            if (channel == GlobalChannel.Name) return "names";
            if (channel == GlobalChannel.CityName) return "city name";
            if (channel == GlobalChannel.Action) return "actions";
            if (channel == GlobalChannel.Unlocks) return "unlocks";
            foreach (BlobChannel b in _blobs)
            {
                if (b.Id == channel) return b.Name;
            }
            return "channel 0x" + channel.ToString("X8", CultureInfo.InvariantCulture);
        }
    }
}
