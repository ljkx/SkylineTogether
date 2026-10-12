using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using SkylineTogether.Core.Identity;

namespace SkylineTogether.Core.Replication
{
    /// <summary>
    /// The policy table of ARCH §4.2.7 resolved against the session's type catalog: replication class per (family, type),
    /// the policy flags per type, the co-capture table of forward references (ARCH §4.2.6) and the line path-state types
    /// (ARCH §4.4.2 step 3a). Immutable after <see cref="FromModel"/>, so any thread may read it.
    /// <para>
    /// <b>Effective entry.</b> For a (family, type) pair the family-scoped entry wins over the unscoped one; unset values of
    /// the effective entry fall back to the family default (<see cref="PolicyTableModel.FamilyDefaults"/>, else
    /// <see cref="FamilyRules.DefaultClass"/>). Family-scoped entries matter for <see cref="ClassOf"/>,
    /// <see cref="IsSource"/> and <see cref="StandInResolverOf"/>.
    /// </para>
    /// <para>
    /// <b>Type-level predicates</b> (no family parameter) are true when the unscoped entry or any family-scoped entry of the
    /// type sets the flag, because a type flagged for its only relevant family (for example <c>cacheReceived</c> on
    /// <c>PathInformation</c> of the <c>Segment</c> family, ARCH Appendix F) must be treated as flagged. The exclusions
    /// <see cref="IsExcluded"/> and <see cref="IsOnDemand"/> are the opposite: true only when no family-scoped entry
    /// overrides them, since a type excluded on one family can replicate on another (<c>HiddenRoute</c>).
    /// </para>
    /// <para>
    /// <b>Class rules</b> (<see cref="ClassOf"/>): an excluded effective entry gives <see cref="ReplicationClass.Excluded"/>,
    /// an on-demand one <see cref="ReplicationClass.OnDemand"/>; an explicit class wins; <c>source</c> makes a type S on a
    /// family whose default is S ("S rather than V on structure families"); otherwise the family default applies. Unscoped
    /// class overrides do not apply to derived families (they never get slots; their K types are family-scoped entries).
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PolicyTable
    {
        /// <summary>Full names of the segment path-state types (ARCH §4.11.3), used when the policy table names none.</summary>
        public static readonly string[] DefaultPathStateTypeNames = { "Game.Routes.PathTargets", "Game.Pathfind.PathInformation", "Game.Pathfind.PathElement" };

        /// <summary>The property's renter list; <c>RentersUpdated</c> is synthesised after it (ARCH §4.7.8).</summary>
        public const string RenterTypeName = "Game.Buildings.Renter";

        /// <summary>The route colour; <c>ColorUpdated</c> is synthesised after it (ARCH §4.7.7).</summary>
        public const string RouteColorTypeName = "Game.Routes.Color";

        /// <summary>Owners whose light flags drive client-derived street-light poles (ARCH §6.4, §4.7.7).</summary>
        public static readonly string[] StreetLightOwnerTypeNames = { "Game.Net.Road", "Game.Buildings.Building", "Game.Vehicles.Watercraft" };

        private const int FamilyCount = (int)Family.Generic + 1;
        private const TypeFlags OrFlags = TypeFlags.PlayerAuthorable | TypeFlags.PresenceAuthorable | TypeFlags.HashAsSet | TypeFlags.NoReapply
                                          | TypeFlags.Merge | TypeFlags.DataReplicatedPresence | TypeFlags.AuthorableMask | TypeFlags.ForwardReference
                                          | TypeFlags.CacheReceived | TypeFlags.Guarded | TypeFlags.PresenceSafe | TypeFlags.DeferredFields;

        private static readonly ReadOnlyCollection<TypeRank> NoTypes = new ReadOnlyCollection<TypeRank>(Array.Empty<TypeRank>());

        private readonly ITypeCatalog _catalog;
        private readonly int _typeCount;
        private readonly ReplicationClass[] _familyDefaults = new ReplicationClass[FamilyCount];
        private readonly TypeFlags[] _flags;              // unscoped flags | OR of scoped flags (OrFlags only)
        private readonly TouchFlags[] _applyTags;
        private readonly bool[] _excludedEverywhere;
        private readonly bool[] _onDemandEverywhere;
        private readonly Dictionary<int, TypePolicyEntry> _scoped = new Dictionary<int, TypePolicyEntry>();
        private readonly Dictionary<int, TypePolicyEntry> _unscoped = new Dictionary<int, TypePolicyEntry>();
        private readonly Dictionary<int, ReadOnlyCollection<TypeRank>> _reverseBuffers = new Dictionary<int, ReadOnlyCollection<TypeRank>>();
        private readonly ReadOnlyCollection<TypeRank> _pathState;
        private readonly bool[] _isPathState;
        private readonly ReadOnlyCollection<string> _warnings;

        private PolicyTable(PolicyTableModel model, ITypeCatalog catalog)
        {
            _catalog = catalog;
            _typeCount = catalog.Count;
            Version = model.Version;
            var warnings = new List<string>();
            for (int f = 0; f < FamilyCount; f++) _familyDefaults[f] = FamilyRules.DefaultClass((Family)f);
            if (model.FamilyDefaults != null)
            {
                foreach (KeyValuePair<Family, ReplicationClass> pair in model.FamilyDefaults)
                {
                    if ((int)pair.Key < FamilyCount) _familyDefaults[(int)pair.Key] = pair.Value;
                }
            }

            _flags = new TypeFlags[_typeCount];
            _applyTags = new TouchFlags[_typeCount];
            _excludedEverywhere = new bool[_typeCount];
            _onDemandEverywhere = new bool[_typeCount];
            _isPathState = new bool[_typeCount];
            for (int i = 0; i < _typeCount; i++)
            {
                TypeDescriptor d = catalog[new TypeRank((ushort)i)];
                _flags[i] = d.Flags;
                _applyTags[i] = d.ApplyTags;
                _excludedEverywhere[i] = (d.Flags & TypeFlags.Excluded) != 0;
                _onDemandEverywhere[i] = (d.Flags & TypeFlags.OnDemand) != 0 || d.ClassOverride == ReplicationClass.OnDemand;
            }

            var pathState = new List<TypeRank>();
            if (model.Types != null)
            {
                foreach (TypePolicyEntry entry in model.Types)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.TypeName)) continue;
                    if (!catalog.TryGetByName(entry.TypeName, out TypeDescriptor d))
                    {
                        // TypeCatalogBuilder already reports unknown names; family-scoped ones are repeated here for the dump
                        if (entry.Family.HasValue) warnings.Add("family-scoped policy for " + entry + " names no catalogued type");
                        continue;
                    }
                    int rank = d.Rank.Value;
                    if (entry.Family.HasValue)
                    {
                        int key = Key(entry.Family.Value, rank);
                        if (_scoped.ContainsKey(key))
                        {
                            warnings.Add("policy for " + entry + " is listed twice; the first entry is used");
                            continue;
                        }
                        _scoped.Add(key, entry);
                        _flags[rank] |= entry.Flags & OrFlags;
                        _applyTags[rank] |= entry.ApplyTags;
                        if ((entry.Flags & TypeFlags.Excluded) == 0) _excludedEverywhere[rank] = false;
                        if ((entry.Flags & TypeFlags.OnDemand) == 0 && entry.Class != ReplicationClass.OnDemand) _onDemandEverywhere[rank] = false;
                        if (entry.Family.Value == Family.Segment && (entry.Flags & TypeFlags.Source) != 0 && !_isPathState[rank])
                        {
                            _isPathState[rank] = true;
                            pathState.Add(d.Rank);
                        }
                    }
                    else if (!_unscoped.ContainsKey(rank))
                    {
                        _unscoped.Add(rank, entry);
                    }
                    AddReverseBuffers(entry, d, warnings);
                }
            }
            if (pathState.Count == 0)
            {
                foreach (string name in DefaultPathStateTypeNames)
                {
                    if (catalog.TryGetByName(name, out TypeDescriptor d) && !_isPathState[d.Rank.Value])
                    {
                        _isPathState[d.Rank.Value] = true;
                        pathState.Add(d.Rank);
                    }
                }
            }
            pathState.Sort();
            _pathState = pathState.AsReadOnly();

            RenterType = Resolve(RenterTypeName);
            RouteColorType = Resolve(RouteColorTypeName);
            var lights = new List<TypeRank>();
            foreach (string name in StreetLightOwnerTypeNames)
            {
                TypeRank? rank = Resolve(name);
                if (rank.HasValue) lights.Add(rank.Value);
            }
            StreetLightOwnerTypes = lights.AsReadOnly();
            _warnings = warnings.AsReadOnly();
        }

        /// <summary>
        /// Resolves the parsed policy table against the catalog. Unknown type names are skipped (the catalog builder already
        /// warns about them; family-scoped ones are listed in <see cref="Warnings"/>).
        /// </summary>
        public static PolicyTable FromModel(PolicyTableModel model, ITypeCatalog catalog)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            return new PolicyTable(model, catalog);
        }

        /// <summary>The catalog the table was resolved against.</summary>
        public ITypeCatalog Catalog => _catalog;

        /// <summary>The policy table version (part of the catalog fingerprint).</summary>
        public int Version { get; }

        /// <summary>Findings of the resolution (family-scoped entries naming unknown types, unknown reverse buffers).</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>The renter buffer type, when catalogued.</summary>
        public TypeRank? RenterType { get; }

        /// <summary>The route colour type, when catalogued.</summary>
        public TypeRank? RouteColorType { get; }

        /// <summary>Types whose changes make the client derive street-light poles (those catalogued).</summary>
        public IReadOnlyList<TypeRank> StreetLightOwnerTypes { get; }

        /// <summary>The class of the slot itself (its existence member): the family default.</summary>
        public ReplicationClass FamilyClassOf(Family family)
        {
            return (int)family < FamilyCount ? _familyDefaults[(int)family] : ReplicationClass.Excluded;
        }

        /// <summary>The replication class of a (family, type) pair (see the class remarks).</summary>
        public ReplicationClass ClassOf(Family family, TypeRank type)
        {
            int rank = type.Value;
            if (rank >= _typeCount || (int)family >= FamilyCount) return ReplicationClass.Excluded;
            ReplicationClass familyDefault = _familyDefaults[(int)family];
            if (_scoped.TryGetValue(Key(family, rank), out TypePolicyEntry scoped))
            {
                if ((scoped.Flags & TypeFlags.Excluded) != 0) return ReplicationClass.Excluded;
                if ((scoped.Flags & TypeFlags.OnDemand) != 0) return ReplicationClass.OnDemand;
                if (scoped.Class.HasValue) return scoped.Class.Value;
                if ((scoped.Flags & TypeFlags.Source) != 0 && familyDefault == ReplicationClass.S) return ReplicationClass.S;
                return familyDefault;
            }
            if (IsUnslotted(familyDefault)) return familyDefault;
            TypeDescriptor d = _catalog[type];
            if ((d.Flags & TypeFlags.Excluded) != 0) return ReplicationClass.Excluded;
            if ((d.Flags & TypeFlags.OnDemand) != 0 || d.ClassOverride == ReplicationClass.OnDemand) return ReplicationClass.OnDemand;
            if (d.ClassOverride != ReplicationClass.Excluded) return d.ClassOverride;
            if ((d.Flags & TypeFlags.Source) != 0 && familyDefault == ReplicationClass.S) return ReplicationClass.S;
            return familyDefault;
        }

        public bool IsPlayerAuthorable(TypeRank type) => Has(type, TypeFlags.PlayerAuthorable);

        public bool IsPresenceAuthorable(TypeRank type) => Has(type, TypeFlags.PresenceAuthorable);

        public bool HasAuthorableMask(TypeRank type) => Has(type, TypeFlags.AuthorableMask);

        public bool IsHashAsSet(TypeRank type) => Has(type, TypeFlags.HashAsSet);

        /// <summary>Excluded on every family (no family-scoped entry replicates it).</summary>
        public bool IsExcluded(TypeRank type) => type.Value < _typeCount && _excludedEverywhere[type.Value];

        /// <summary>On demand on every family (no family-scoped entry gives it another class).</summary>
        public bool IsOnDemand(TypeRank type) => type.Value < _typeCount && _onDemandEverywhere[type.Value];

        public bool IsNoReapply(TypeRank type) => Has(type, TypeFlags.NoReapply);

        /// <summary>S rather than V on structure families (family-scoped entries win).</summary>
        public bool IsSource(Family family, TypeRank type)
        {
            TypePolicyEntry entry = EffectiveEntry(family, type);
            if (entry != null) return (entry.Flags & TypeFlags.Source) != 0;
            return type.Value < _typeCount && (_catalog[type].Flags & TypeFlags.Source) != 0;
        }

        public bool IsCacheReceived(TypeRank type) => Has(type, TypeFlags.CacheReceived);

        public bool IsGuarded(TypeRank type) => Has(type, TypeFlags.Guarded);

        public bool HasDeferredFields(TypeRank type) => Has(type, TypeFlags.DeferredFields);

        /// <summary>True when the type is a forward reference whose changes co-capture reverse buffers (ARCH §4.2.6).</summary>
        public bool IsForwardReference(TypeRank type) => Has(type, TypeFlags.ForwardReference);

        /// <summary>The presence-safe stand-in resolver of a (family, type) pair (ARCH §4.11.3); null if none.</summary>
        public string StandInResolverOf(Family family, TypeRank type)
        {
            TypePolicyEntry entry = EffectiveEntry(family, type);
            return entry?.StandInResolver;
        }

        /// <summary>Tags the client adds when a value of the type changes (tag rule, ARCH §4.7.7).</summary>
        public TouchFlags ApplyTagsOf(TypeRank type) => type.Value < _typeCount ? _applyTags[type.Value] : TouchFlags.None;

        /// <summary>The reverse buffers co-captured with a forward-reference type (ARCH §4.2.6); empty for other types.</summary>
        public IReadOnlyList<TypeRank> ReverseBuffersOf(TypeRank forwardReferenceType)
        {
            return _reverseBuffers.TryGetValue(forwardReferenceType.Value, out ReadOnlyCollection<TypeRank> list) ? list : NoTypes;
        }

        /// <summary><c>PathTargets</c>, <c>PathInformation</c>, <c>PathElement</c> on the <c>Segment</c> family (ARCH §4.4.2
        /// step 3a): the types with a <c>Segment</c>-scoped <c>source</c> entry, else the default names when catalogued.</summary>
        public IReadOnlyList<TypeRank> PathStateTypes => _pathState;

        /// <summary>True for a path-state type (see <see cref="PathStateTypes"/>).</summary>
        public bool IsPathState(TypeRank type) => type.Value < _typeCount && _isPathState[type.Value];

        // ---------------------------------------------------------------- catalog facts used by the engines

        /// <summary>True when the type's enabled bit is replicated: enableable and not <c>ISerializeAsEnabled</c> (ARCH §4.2.3).</summary>
        public bool HasReplicatedEnabledBit(TypeRank type)
        {
            if (type.Value >= _typeCount) return false;
            TypeFlags flags = _catalog[type].Flags;
            return (flags & TypeFlags.Enableable) != 0 && (flags & TypeFlags.SerializeAsEnabled) == 0;
        }

        /// <summary>
        /// True when the type carries canonical bytes (data through its codec): serializable components and buffers, shared
        /// serializable components, the mover <c>UpdateFrame</c>, hand-written codecs and presence types replicated as data
        /// (the <c>EntityRefList</c> codec, ARCH §4.2.3-4.2.4). Pure presence types (codec <c>EmptyTag</c> or
        /// <c>EnableBit</c>) travel in the type set and, when enableable, as an enabled bit.
        /// </summary>
        public bool CarriesData(TypeRank type)
        {
            if (type.Value >= _typeCount) return false;
            CodecKind codec = _catalog[type].Codec;
            return codec != CodecKind.EmptyTag && codec != CodecKind.EnableBit;
        }

        /// <summary>True when a (slot, type) pair has a member hash: it carries data or a replicated enabled bit.</summary>
        public bool HasMember(TypeRank type) => CarriesData(type) || HasReplicatedEnabledBit(type);

        /// <summary>The value hash of canonical bytes of the type (<see cref="MemberHash.HashValue"/> with the type's policy).</summary>
        public uint HashValue(TypeRank type, byte[] canonical, int offset, int count)
        {
            bool asSet = IsHashAsSet(type);
            int refs = asSet && type.Value < _typeCount ? Math.Max(1, _catalog[type].EntityOffsetCount) : 1;
            return MemberHash.HashValue(type, asSet, refs, canonical, offset, count);
        }

        /// <summary>True for the classes whose pairs are members of bucket sums keyed by slot (ARCH §8.2).</summary>
        public static bool IsHashedClass(ReplicationClass cls)
        {
            switch (cls)
            {
                case ReplicationClass.S:
                case ReplicationClass.G:
                case ReplicationClass.V:
                case ReplicationClass.A:
                case ReplicationClass.ALow:
                case ReplicationClass.M:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True for the classes delivered per peer from the value shadow with dirty bits (ARCH §4.5.2-4.5.3).</summary>
        public static bool IsPerPeerClass(ReplicationClass cls)
        {
            return cls == ReplicationClass.V || cls == ReplicationClass.A || cls == ReplicationClass.ALow || cls == ReplicationClass.M;
        }

        /// <summary>True for the shared P0 classes, whose hash shadow is updated at emission (ARCH §4.4.5).</summary>
        public static bool IsSharedClass(ReplicationClass cls) => cls == ReplicationClass.S || cls == ReplicationClass.G;

        public override string ToString()
        {
            return "PolicyTable(v" + Version.ToString(CultureInfo.InvariantCulture) + ", " + _scoped.Count.ToString(CultureInfo.InvariantCulture)
                   + " family-scoped entries, " + _reverseBuffers.Count.ToString(CultureInfo.InvariantCulture) + " forward references)";
        }

        // ---------------------------------------------------------------- internals

        private static int Key(Family family, int rank) => ((int)family << 16) | rank;

        private static bool IsUnslotted(ReplicationClass cls)
        {
            return cls == ReplicationClass.Derived || cls == ReplicationClass.Excluded || cls == ReplicationClass.Channel;
        }

        private bool Has(TypeRank type, TypeFlags flag) => type.Value < _typeCount && (_flags[type.Value] & flag) != 0;

        private TypePolicyEntry EffectiveEntry(Family family, TypeRank type)
        {
            if (type.Value >= _typeCount) return null;
            if (_scoped.TryGetValue(Key(family, type.Value), out TypePolicyEntry scoped)) return scoped;
            return _unscoped.TryGetValue(type.Value, out TypePolicyEntry unscoped) ? unscoped : null;
        }

        private TypeRank? Resolve(string name)
        {
            return _catalog.TryGetByName(name, out TypeDescriptor d) ? d.Rank : (TypeRank?)null;
        }

        private void AddReverseBuffers(TypePolicyEntry entry, TypeDescriptor forward, List<string> warnings)
        {
            if (entry.ReverseBuffers == null || entry.ReverseBuffers.Count == 0) return;
            _reverseBuffers.TryGetValue(forward.Rank.Value, out ReadOnlyCollection<TypeRank> existing);
            var list = existing != null ? new List<TypeRank>(existing) : new List<TypeRank>();
            foreach (string name in entry.ReverseBuffers)
            {
                if (!_catalog.TryGetByName(name, out TypeDescriptor reverse))
                {
                    warnings.Add("the reverse buffer " + name + " of " + entry + " is not a catalogued type");
                    continue;
                }
                if (!list.Contains(reverse.Rank)) list.Add(reverse.Rank);
            }
            list.Sort();
            _reverseBuffers[forward.Rank.Value] = list.AsReadOnly();
        }
    }
}
