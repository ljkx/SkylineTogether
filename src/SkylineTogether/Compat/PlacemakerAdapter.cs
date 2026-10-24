using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Prefabs;
using SkylineTogether.Core;
using SkylineTogether.Core.Modding;
using Unity.Entities;
using Unity.Mathematics;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Placemaker (Paradox Mods 163638): designs parks, plazas, parking lots and similar places in a drawn area and builds
    /// them, and dresses zoned lots by itself ("neighbourhood polish", whose automatic part is called Outgrow).
    /// <list type="bullet">
    /// <item><b>Builds</b> run in <c>BuildService</c> over many frames with its own tool <c>BuildToolSystem</c> active:
    /// objects, nets, areas and zoning go through the game's Temp pipeline and apply through ApplyTool; around that it
    /// writes directly (group record and carrier entities without update tags, <c>PlacemakerMember</c> on the applied
    /// pieces, a lake's water source, owner lot, money for roads). <c>BuildToolSystem</c> is therefore a direct-write
    /// tool; the group record (<c>PlacemakerGroup</c> + <c>PlacemakerGroupBlob</c>) and the carrier are new entities a
    /// client proposes as spawns (<see cref="ModAdapter.ClientCreatedTypes"/>). Its pieces may collide by design (it
    /// removes the game's errors from its own temporaries), may lie outside owned tiles and use any prefab of its catalog,
    /// so the host relaxes every placement check while Placemaker is loaded. The planning tool
    /// <c>PlacemakerToolSystem</c> writes nothing itself (pipeline tool).</item>
    /// <item><b>Runtime prefabs</b>: a park build gets a hidden companion building whose prefab Placemaker creates for its
    /// statistics, named after them (<c>"Placemaker Park a30 l9 r6000 c3000 m16 u20000"</c>); each player's Placemaker
    /// remembers the names it made in its own files, so the host has prefabs a joiner never made, and overlay copies of
    /// non-base-game surfaces (<c>"Placemaker Overlay (...)"</c>) likewise. Both are fully described by their names
    /// (Core <see cref="PlacemakerNames"/>): this adapter asks the joiner's Placemaker to create the same prefab
    /// (<c>ParkCompanionPrefabSystem.Want</c>, <c>SurfaceCloneSystem.OverlayFor</c>) as soon as the host's prefab table
    /// names it (before the city is loaded, so the loader finds it), or when a later prefab record does; same type, name
    /// and no asset, so the same <c>PrefabID</c> (§7). A client's own new ones are created on the host on request.</item>
    /// <item><b>Host-only work</b>: Outgrow and its helpers dress lots asynchronously from local terrain and randomness,
    /// group removal deletes in passes, the companion systems create and remove companion buildings, the old-decal
    /// clean-up deletes after a load: all off on clients from the start of the join; the host's results replicate. A
    /// client's companion requests go to the host on Skyline Together's channel (<see cref="PlacemakerMessage"/>), and a
    /// client's removal request is the <c>PlacemakerRemoval</c> component it adds to the record (watched mod state).</item>
    /// <item><b>Post-load repair</b>: the group registry runs everywhere (indices for the UI, the parking fees every peer
    /// derives), but its one-time repair after a load would redo, on a joiner, what the host's load already did (re-show
    /// hidden pieces, recreate companions): the registry is held off during the join and its pending repair is cleared
    /// before it runs again.</item>
    /// <item><b>Settings</b> that change shared results are the host's: parking economy and default fee, surface overlays,
    /// park statistics and companions, the experimental parking companion and its upkeep, road charges, the owner lot.
    /// Per-build options (clearing trees, keeping zoned buildings, collisions) stay each builder's own, like a tool mode.</item>
    /// </list>
    /// The 13 saved components replicate generically (records and the city switches are mod entities).
    /// </summary>
    public sealed class PlacemakerAdapter : ModAdapter
    {
        private const string Economy = "Placemaker.Game.Economy.";
        private const string Groups = "Placemaker.Game.Groups.";
        private const string Neighbours = "Placemaker.Game.Neighbours.";
        private const string ParkPrefabs = Economy + "ParkCompanionPrefabSystem";
        private const string ParkCompanions = Economy + "ParkCompanionSystem";
        private const string ParkingCompanions = Economy + "ParkingCompanionSystem";
        private const string SurfaceClones = "Placemaker.Game.Build.SurfaceCloneSystem";
        private const string Registry = Groups + "GroupRegistrySystem";

        // host: what clients asked for, bounded (a builder's requests; the channel's budgets limit the rate)
        private const int MaxQueued = 256;
        private const double RecordWaitSeconds = 30.0;
        private const double ScanGraceSeconds = 5.0;
        private readonly List<(PlacemakerMessage Message, double Since)> _companions = new List<(PlacemakerMessage, double)>();
        private readonly List<(string Type, string Name, double Since)> _ensure = new List<(string, string, double)>();
        // both: prefabs asked for and park prefabs already given Placemaker's own prefab changes
        private readonly HashSet<string> _wanted = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _mutated = new HashSet<string>(StringComparer.Ordinal);
        // client: own on-demand prefabs already named to the host
        private readonly HashSet<string> _sent = new HashSet<string>(StringComparer.Ordinal);
        private int _scannedCount = -1;
        private double _firstTick = -1;
        private double _lastScan;
        private bool _warnedRefused;

        public override string ModName => "Placemaker";

        public override string AssemblyName => "Placemaker";

        public override string SettingsTypeName => "Placemaker.Game.Settings.PlacemakerSettings";

        public override IEnumerable<string> SyncedSettings => new[]
        {
            "ParkingEconomy", "DefaultParkingFee", "SurfaceClones", "ApplyParkStats", "RealParkStats",
            "ExperimentalParkingCompanion", "CompanionUpkeepFactor", "ChargeNetConstruction", "SelectAsOneThing",
        };

        public override IEnumerable<string> PipelineTools => new[] { "Placemaker.Game.Tool.PlacemakerToolSystem" };

        public override IEnumerable<string> JoinOffSystems => new[]
        {
            Neighbours + "NeighbourPolishSystem",
            Neighbours + "NeighbourShowSystem",
            Neighbours + "NeighbourObjectSystem",
            Neighbours + "NeighbourAreaSystem",
            Neighbours + "NeighbourLawnSystem",
            Neighbours + "NeighbourDeleteSystem",
            Neighbours + "NeighbourPaintSystem",
            Groups + "GroupRemovalSystem",
            ParkCompanions,
            Economy + "ParkCompanionCleanupSystem",
            ParkingCompanions,
            "Placemaker.Game.Build.OldDecalCleanupSystem",
        };

        public override IEnumerable<string> LoadOffSystems => new[] { Registry };

        public override IEnumerable<string> ClientCreatedTypes => new[] { Groups + "PlacemakerGroup", Groups + "PlacemakerMember" };

        public override IEnumerable<string> Members => new[]
        {
            ParkPrefabs + "::Want",
            ParkPrefabs + "::Ready",
            ParkCompanions + "::Request",
            ParkCompanions + "::MutatePrefab",
            ParkCompanions + "::_pending",
            ParkCompanions + "+Pending::GroupId",
            ParkCompanions + "+Pending::Name",
            ParkCompanions + "+Pending::Position",
            ParkCompanions + "+Pending::Yaw",
            ParkingCompanions + "::Request",
            ParkingCompanions + "::_pending",
            SurfaceClones + "::OverlayFor",
            Registry + "::_postLoadPending",
            Registry + "::TryGetRecordEntity",
            "Placemaker.Game.Tool.ToolRegistration::Settings",
        };

        /// <summary>Placemaker keeps its settings in a static property of its tool registration, not on its <c>IMod</c>.</summary>
        public override object SettingsInstance() => FindMember("Placemaker.Game.Tool.ToolRegistration::Settings") is PropertyInfo p ? p.GetValue(null) : null;

        /// <summary>Its builds collide by design, may reach past owned tiles and use any catalog prefab.</summary>
        public override Core.Sync.PlacementChecks RelaxedPlacementChecks => Core.Sync.PlacementChecks.All;

        public override string InternalChannel => PlacemakerMessage.Channel;

        public override string Summary => "groups replicate; Outgrow, removal and companions host-only; park and overlay prefabs created on demand; settings from the host";

        // ------------------------------------------------------------------------------------------------ runtime prefabs

        public override bool HandlesRuntimePrefab(string type, string name) => PlacemakerNames.Classify(type, name) != PlacemakerPrefabKind.None;

        public override RuntimePrefabState ProvideRuntimePrefab(string type, string name)
        {
            switch (PlacemakerNames.Classify(type, name))
            {
                case PlacemakerPrefabKind.Park:
                    return ParkPrefab(name);
                case PlacemakerPrefabKind.Overlay:
                    return OverlayPrefab(name);
                default:
                    return RuntimePrefabState.NotHandled;
            }
        }

        /// <summary>A park companion prefab: Placemaker builds it from a vanilla park with the name's statistics.</summary>
        private RuntimePrefabState ParkPrefab(string name)
        {
            object prefabs = System(ParkPrefabs);
            if (prefabs == null) return RuntimePrefabState.Refused;
            var ready = (Entity)Call(ParkPrefabs + "::Ready", prefabs, name);
            if (ready != Entity.Null)
            {
                // Placemaker clears the lot's terrain shaping of a companion prefab when it first places one; the same
                // here, so the prefab data match the host's (idempotent)
                object companions = System(ParkCompanions);
                if (companions != null && _mutated.Add(name)) Call(ParkCompanions + "::MutatePrefab", companions, ready);
                return RuntimePrefabState.Ready;
            }
            if (_wanted.Add(name)) Call(ParkPrefabs + "::Want", prefabs, name);
            return RuntimePrefabState.Pending;
        }

        /// <summary>An overlay surface: Placemaker copies the local surface of the original name.</summary>
        private RuntimePrefabState OverlayPrefab(string name)
        {
            if (!PlacemakerNames.TryParseOverlay(name, out string original)) return RuntimePrefabState.NotHandled;
            object clones = System(SurfaceClones);
            PrefabSystem prefabs = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<PrefabSystem>();
            if (clones == null || prefabs == null) return RuntimePrefabState.Refused;
            PrefabBase source = AssetSources.FindUnique(prefabs, PlacemakerNames.OverlayType, original);
            if (source == null || !prefabs.TryGetEntity(source, out Entity sourceEntity)) return RuntimePrefabState.Refused; // the original is missing here
            // returns the original until the copy is created and initialised (asks for it the first time)
            var overlay = (Entity)Call(SurfaceClones + "::OverlayFor", clones, sourceEntity);
            _wanted.Add(name);
            return overlay != Entity.Null && overlay != sourceEntity ? RuntimePrefabState.Ready : RuntimePrefabState.Pending;
        }

        // ------------------------------------------------------------------------------------------------ session

        public override void OnClientLive()
        {
            // the registry's repair after the load (queued while it was held off) is the host's, already in the city
            object registry = System(Registry);
            if (registry != null && FindMember(Registry + "::_postLoadPending") is FieldInfo pending) pending.SetValue(registry, false);
        }

        public override void Tick(bool host, double now)
        {
            if (_firstTick < 0) _firstTick = now;
            if (host)
            {
                HostCompanions(now);
                HostEnsure(now);
            }
            else
            {
                ForwardParkCompanions();
                ForwardParkingCompanions();
                if (now - _firstTick >= ScanGraceSeconds && now - _lastScan >= 1.0)
                {
                    _lastScan = now;
                    SendOwnPrefabs();
                }
            }
        }

        public override void OnSessionEnded()
        {
            _companions.Clear();
            _ensure.Clear();
            _wanted.Clear();
            _sent.Clear();
            _scannedCount = -1;
            _firstTick = -1;
            _lastScan = 0;
            _warnedRefused = false;
        }

        // ------------------------------------------------------------------------------------------------ client

        /// <summary>
        /// The client's own park builds asked its (switched off) companion system for a companion: the requests go to the
        /// host, whose Placemaker creates the companion (it replicates back like any building).
        /// </summary>
        private void ForwardParkCompanions()
        {
            object system = System(ParkCompanions);
            if (system == null || !(FindMember(ParkCompanions + "::_pending") is FieldInfo field) || !(field.GetValue(system) is IList pending) || pending.Count == 0) return;
            FieldInfo group = FindMember(ParkCompanions + "+Pending::GroupId") as FieldInfo;
            FieldInfo name = FindMember(ParkCompanions + "+Pending::Name") as FieldInfo;
            FieldInfo position = FindMember(ParkCompanions + "+Pending::Position") as FieldInfo;
            FieldInfo yaw = FindMember(ParkCompanions + "+Pending::Yaw") as FieldInfo;
            if (group == null || name == null || position == null || yaw == null) return;
            foreach (object p in pending)
            {
                var at = (float2)position.GetValue(p);
                Send(PlacemakerMessage.ParkCompanion((int)group.GetValue(p), name.GetValue(p) as string, at.x, at.y, (float)yaw.GetValue(p)));
            }
            pending.Clear();
        }

        /// <summary>The same for the experimental parking companion (a list of group ids).</summary>
        private void ForwardParkingCompanions()
        {
            object system = System(ParkingCompanions);
            if (system == null || !(FindMember(ParkingCompanions + "::_pending") is FieldInfo field) || !(field.GetValue(system) is List<int> pending) || pending.Count == 0) return;
            foreach (int group in pending) Send(PlacemakerMessage.ParkingCompanion(group));
            pending.Clear();
        }

        /// <summary>
        /// On-demand prefabs this client's Placemaker created that the host has not named (a new overlay surface, a park
        /// companion): the host creates the same, then names them to everyone, so this client's pieces using them can be
        /// proposed. Scanned when the prefab count changes, at most once a second.
        /// </summary>
        private void SendOwnPrefabs()
        {
            PrefabSystem prefabs = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<PrefabSystem>();
            Func<Entity, bool> bound = ModCompat.IsPrefabBound;
            if (prefabs == null || bound == null) return;
            int count = AssetSources.PrefabCount(prefabs);
            if (count == _scannedCount) return;
            _scannedCount = count;
            foreach (PrefabBase p in AssetSources.All(prefabs))
            {
                string type = p.GetType().Name;
                if (PlacemakerNames.Classify(type, p.name) == PlacemakerPrefabKind.None || _sent.Contains(p.name)) continue;
                if (!prefabs.TryGetEntity(p, out Entity e) || bound(e)) continue;
                if (_sent.Count >= RuntimePrefabBook.MaxPrefabs) return;
                _sent.Add(p.name);
                Send(PlacemakerMessage.EnsurePrefab(type, p.name));
            }
        }

        private void Send(PlacemakerMessage m)
        {
            if (!m.IsValid())
            {
                Mod.Log?.Debug(LogCategory.Compat, "Placemaker adapter: not sending an invalid " + m.Kind + " request");
                return;
            }
            ChannelSender?.Invoke(m.ToBytes(), 0);
        }

        // ------------------------------------------------------------------------------------------------ host

        public override void OnChannelMessage(int from, byte[] payload)
        {
            if (Mod.Sessions?.Host == null) return; // only the host acts on requests
            if (!PlacemakerMessage.TryRead(payload, out PlacemakerMessage m))
            {
                if (!_warnedRefused) Mod.Log?.Warn(LogCategory.Compat, "Placemaker adapter: dropped an invalid request from player " + from);
                _warnedRefused = true;
                return;
            }
            double now = Diagnostics.Perf.Now;
            if (m.Kind == PlacemakerMessageKind.EnsurePrefab)
            {
                if (_ensure.Count < MaxQueued && !_ensure.Exists(x => x.Name == m.PrefabName)) _ensure.Add((m.PrefabType, m.PrefabName, now));
            }
            else if (_companions.Count < MaxQueued)
            {
                _companions.Add((m, now));
            }
        }

        /// <summary>A client's companion request, once its group's record arrived here (it travels in the same frame).</summary>
        private void HostCompanions(double now)
        {
            if (_companions.Count == 0) return;
            object registry = System(Registry);
            for (int i = _companions.Count - 1; i >= 0; i--)
            {
                (PlacemakerMessage m, double since) = _companions[i];
                object[] args = { m.Group, Entity.Null };
                bool exists = registry != null && (bool)Call(Registry + "::TryGetRecordEntity", registry, args);
                if (!exists)
                {
                    if (now - since > RecordWaitSeconds)
                    {
                        _companions.RemoveAt(i);
                        Mod.Log?.Info(LogCategory.Compat, "Placemaker adapter: group " + m.Group + " never arrived; its companion request is dropped");
                    }
                    continue;
                }
                _companions.RemoveAt(i);
                if (m.Kind == PlacemakerMessageKind.ParkCompanion)
                {
                    object system = System(ParkCompanions);
                    if (system != null) Call(ParkCompanions + "::Request", system, m.Group, m.PrefabName, new float2(m.X, m.Z), m.Yaw);
                }
                else
                {
                    object system = System(ParkingCompanions);
                    if (system != null) Call(ParkingCompanions + "::Request", system, m.Group);
                }
            }
        }

        /// <summary>A client's own on-demand prefab: created here, then named to everyone (the next capture binds it).</summary>
        private void HostEnsure(double now)
        {
            if (_ensure.Count == 0) return;
            PrefabSystem prefabs = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<PrefabSystem>();
            if (prefabs == null) return;
            for (int i = _ensure.Count - 1; i >= 0; i--)
            {
                (string type, string name, double since) = _ensure[i];
                PrefabBase existing = AssetSources.FindUnique(prefabs, type, name);
                RuntimePrefabState state = existing != null ? RuntimePrefabState.Ready : ModCompat.RuntimePrefab(type, name, Mod.Log);
                if (state == RuntimePrefabState.Ready)
                {
                    existing = existing ?? AssetSources.FindUnique(prefabs, type, name);
                    if (existing != null && prefabs.TryGetEntity(existing, out Entity e)) ModCompat.AnnouncePrefab(e);
                    _ensure.RemoveAt(i);
                }
                else if (state != RuntimePrefabState.Pending || now - since > RuntimePrefabBook.GiveUpSeconds)
                {
                    _ensure.RemoveAt(i);
                }
            }
        }

        // ------------------------------------------------------------------------------------------------ reflection

        /// <summary>A Placemaker system of the game world by full type name, or null.</summary>
        private object System(string typeName)
        {
            Type type = FindType(typeName);
            World world = World.DefaultGameObjectInjectionWorld;
            return type == null || world == null ? null : world.GetExistingSystemManaged(type);
        }

        /// <summary>Calls a checked method (<see cref="ModAdapter.Members"/>); by-reference arguments come back in <paramref name="args"/>.</summary>
        private object Call(string member, object target, params object[] args)
        {
            if (!(FindMember(member) is MethodInfo method)) throw new MissingMethodException(member);
            return method.Invoke(target, args);
        }
    }
}
