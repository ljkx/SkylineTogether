using System;
using System.Collections.Generic;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// The update-group rule of the game (ARCHITECTURE.md#client-apply): <c>Game.Simulation.UpdateGroupSystem</c> runs in
    /// Modification5 over every chunk with <c>UpdateFrame</c> and <c>Created</c> or <c>Deleted</c> and picks the table of
    /// update-group sizes by the chunk's marker component (moving objects, plants, buildings and extensions, nets, lanes,
    /// companies, households, citizens, household pets). An entity with <c>UpdateFrame</c> and none of them gets no table:
    /// the game logs "UpdateFrame added to unsupported type", gives a created entity an update frame out of range and indexes
    /// its Burst job with it. No entity this mod creates or changes may ever reach the system in that state.
    /// </summary>
    public static class UpdateGroupMarkers
    {
        public const string UpdateFrame = "Game.Simulation.UpdateFrame";

        /// <summary>The marker types, in the order the game tests them.</summary>
        public static readonly IReadOnlyList<string> TypeNames = new[]
        {
            "Game.Objects.Moving",
            "Game.Objects.Stopped",
            "Game.Creatures.CurrentVehicle",
            "Game.Objects.Plant",
            "Game.Buildings.Building",
            "Game.Buildings.Extension",
            "Game.Net.Node",
            "Game.Net.Edge",
            "Game.Net.Lane",
            "Game.Companies.CompanyData",
            "Game.Citizens.Household",
            "Game.Citizens.Citizen",
            "Game.Citizens.HouseholdPet",
        };

        private static readonly HashSet<string> Markers = new HashSet<string>(TypeNames, StringComparer.Ordinal);

        public static bool IsMarker(string typeName) => typeName != null && Markers.Contains(typeName);

        /// <summary>
        /// True when an entity with these component types is safe for the update-group system: it has no <c>UpdateFrame</c>,
        /// or it has one of the markers.
        /// </summary>
        public static bool IsComplete(IEnumerable<string> typeNames)
        {
            if (typeNames == null) return true;
            bool updateFrame = false, marker = false;
            foreach (string t in typeNames)
            {
                if (t == UpdateFrame) updateFrame = true;
                else if (IsMarker(t)) marker = true;
            }
            return !updateFrame || marker;
        }
    }

    /// <summary>One component of an agent spawn record as the applier sees it (<see cref="AgentSpawnRules.Check"/>).</summary>
    public struct SpawnComponent
    {
        public string TypeName;
        /// <summary>The record carries the value (not presence only).</summary>
        public bool Data;
        /// <summary>Every reference in the value resolves here (always true for presence).</summary>
        public bool Resolved;

        public SpawnComponent(string typeName, bool data, bool resolved)
        {
            TypeName = typeName;
            Data = data;
            Resolved = resolved;
        }
    }

    /// <summary>Why an agent spawn is not created (<see cref="AgentSpawnRules.Check"/>).</summary>
    public enum AgentSpawnVerdict : byte
    {
        Create,
        /// <summary>The record names none of the agent markers (citizen, household, pet, company).</summary>
        NoMarker,
        /// <summary>The record names a marker as presence only.</summary>
        MarkerWithoutValue,
        /// <summary>A defining link (a citizen's or pet's household) does not resolve here.</summary>
        DefiningLinkMissing,
    }

    /// <summary>
    /// Client: whether the applier creates an agent from a spawn (or a creating upsert) record (ARCHITECTURE.md#client-apply).
    /// The game never has a citizen without its household or a pet without its household, and the pet's household link is
    /// its update-group marker (<see cref="UpdateGroupMarkers"/>): such a record is not applied; the agent is asked from the
    /// host again once the household is here (the missing household id is requested as well).
    /// </summary>
    public static class AgentSpawnRules
    {
        /// <summary>The components that make an entity an agent; one of them is the entity's update-group marker.</summary>
        public static readonly IReadOnlyList<string> Markers = new[]
        {
            "Game.Citizens.Citizen", "Game.Citizens.Household", "Game.Citizens.HouseholdPet", "Game.Companies.CompanyData",
        };

        /// <summary>Links whose target must exist here for the agent to be created.</summary>
        public static readonly IReadOnlyList<string> DefiningLinks = new[] { "Game.Citizens.HouseholdMember", "Game.Citizens.HouseholdPet" };

        public static AgentSpawnVerdict Check(IReadOnlyList<SpawnComponent> components, out string type)
        {
            type = null;
            if (components == null) return AgentSpawnVerdict.NoMarker;
            bool marker = false;
            string presenceOnly = null;
            for (int i = 0; i < components.Count; i++)
            {
                SpawnComponent c = components[i];
                if (Contains(Markers, c.TypeName))
                {
                    if (c.Data) marker = true;
                    else presenceOnly = presenceOnly ?? c.TypeName;
                }
                if (c.Data && !c.Resolved && Contains(DefiningLinks, c.TypeName))
                {
                    type = c.TypeName;
                    return AgentSpawnVerdict.DefiningLinkMissing;
                }
            }
            if (marker) return AgentSpawnVerdict.Create;
            type = presenceOnly;
            return presenceOnly != null ? AgentSpawnVerdict.MarkerWithoutValue : AgentSpawnVerdict.NoMarker;
        }

        private static bool Contains(IReadOnlyList<string> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], name, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>What an agent record carries for a forward link (<see cref="AgentLinkNormalization.ForwardLink"/>).</summary>
    public enum ForwardInRecord : byte
    {
        /// <summary>The component with its value.</summary>
        Value,
        /// <summary>The component with its reference set to nobody (an optional reference whose target is gone).</summary>
        Null,
        /// <summary>The component is left out of the record (the reader removes it, as its own repair would).</summary>
        Omit,
    }

    /// <summary>
    /// The normalised view of the agent links that records and integrity hashes carry (ARCHITECTURE.md#reference-safety).
    /// The client repairs every link to a gone target (<see cref="AgentLinkRule.Decide"/>); the host's own state may still hold
    /// such links (a save writes a worker that is gone as nobody; the game prunes some lists lazily). If the host sent and
    /// hashed them as they are, the repaired client would differ from the host forever: every repair asked the host again and
    /// got the same record (a repair loop). So both sides normalise the same way: a list element naming nobody or an entity
    /// that is gone is left out of records and hashes, and a forward link to such a target is sent the way the client's repair
    /// leaves it.
    /// </summary>
    public static class AgentLinkNormalization
    {
        /// <summary>A reverse-list element is written and hashed only while it names a live entity.</summary>
        public static bool KeepsElement(LinkTarget state) => state == LinkTarget.Live;

        /// <summary>
        /// What a record carries for a forward link whose target is in <paramref name="state"/>; <paramref name="nameable"/>
        /// is false when the target has no net id (the reader could not resolve it: it travels as nobody).
        /// </summary>
        public static ForwardInRecord ForwardLink(AgentLink link, LinkTarget state, bool nameable)
        {
            AgentLinkRule rule = AgentLinkGraph.Rule(link);
            if (rule.IsBuffer) throw new ArgumentException(link + " is a reverse list", nameof(link));
            if (state == LinkTarget.Live && nameable) return ForwardInRecord.Value;
            // what the reader would see: nobody (unnameable or gone), decided by the reader's own repair rule
            LinkCleanup action = rule.Decide(state == LinkTarget.Live ? LinkTarget.Null : state);
            switch (action)
            {
                case LinkCleanup.Keep:
                case LinkCleanup.ClearReference:
                    return ForwardInRecord.Null;
                case LinkCleanup.RemoveComponent:
                    return ForwardInRecord.Omit;
                default:
                    // a citizen's or pet's household: the reader deletes the holder; the value travels as it is (nobody)
                    return ForwardInRecord.Null;
            }
        }

        /// <summary>
        /// The order-insensitive hash of a reverse list over its live members (<see cref="KeepsElement"/>); false when a live
        /// member has no net id (the list then travels and is hashed as presence only).
        /// </summary>
        public static bool TryHashMembers<TEntity>(IReadOnlyList<TEntity> members, Func<TEntity, LinkTarget> state, Func<TEntity, uint> netId, out ulong hash)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (netId == null) throw new ArgumentNullException(nameof(netId));
            hash = 0;
            var acc = new SetHash.Accumulator();
            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    if (!KeepsElement(state(members[i]))) continue;
                    uint id = netId(members[i]);
                    if (id == 0) return false;
                    acc.Add(id);
                }
            }
            hash = acc.Value;
            return true;
        }
    }
}
