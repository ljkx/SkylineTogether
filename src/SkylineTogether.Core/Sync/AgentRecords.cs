using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// The agent identities a mover refers to (body of <see cref="SyncRecordKind.MoverAgents"/>, written into the
    /// movers branch's batches right after the mover's spawn or state record). Movers are spawned on clients from their
    /// prefab archetype, so without this record a client's pedestrian has no citizen (no citizen panel, no follow camera
    /// through <c>Resident.m_Citizen</c>), a pet no household pet and a car no keeper or owner. Net ids; 0 means "none".
    /// <code>
    /// u8 flags (agent 1 | keeper 2 | owner 4) | [varint agent] [varint keeper] [varint owner]
    /// </code>
    /// </summary>
    public sealed class MoverAgentLinks
    {
        /// <summary><c>Resident.m_Citizen</c> of a human, <c>Pet.m_HouseholdPet</c> of a pet.</summary>
        public uint Agent;
        /// <summary><c>PersonalCar.m_Keeper</c> (the citizen using a personal car or bicycle).</summary>
        public uint Keeper;
        /// <summary><c>Owner.m_Owner</c> of a vehicle (household, company or service building).</summary>
        public uint Owner;

        public bool IsEmpty => Agent == 0 && Keeper == 0 && Owner == 0;

        public bool SameAs(MoverAgentLinks other)
        {
            if (other == null) return IsEmpty;
            return Agent == other.Agent && Keeper == other.Keeper && Owner == other.Owner;
        }

        public MoverAgentLinks Clone() => new MoverAgentLinks { Agent = Agent, Keeper = Keeper, Owner = Owner };

        public override string ToString() => "agent " + Agent + ", keeper " + Keeper + ", owner " + Owner;
    }

    /// <summary>Wire form of <see cref="MoverAgentLinks"/>.</summary>
    public static class MoverAgentLinksCodec
    {
        public static void Write(ByteWriter w, MoverAgentLinks links)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (links == null) throw new ArgumentNullException(nameof(links));
            byte flags = 0;
            if (links.Agent != 0) flags |= 1;
            if (links.Keeper != 0) flags |= 2;
            if (links.Owner != 0) flags |= 4;
            w.WriteByte(flags);
            if (links.Agent != 0) w.WriteVarUInt32(links.Agent);
            if (links.Keeper != 0) w.WriteVarUInt32(links.Keeper);
            if (links.Owner != 0) w.WriteVarUInt32(links.Owner);
        }

        /// <summary>Throws <see cref="DecodeException"/> for malformed input.</summary>
        public static MoverAgentLinks Read(ByteReader r)
        {
            byte flags = r.ReadByte();
            if ((flags & ~7) != 0) throw new DecodeException("mover agent links with flags " + flags);
            var links = new MoverAgentLinks();
            if ((flags & 1) != 0) links.Agent = NonZero(r, "agent");
            if ((flags & 2) != 0) links.Keeper = NonZero(r, "keeper");
            if ((flags & 4) != 0) links.Owner = NonZero(r, "owner");
            return links;
        }

        private static uint NonZero(ByteReader r, string what)
        {
            uint v = r.ReadVarUInt32();
            if (v == 0) throw new DecodeException("mover agent link " + what + " is 0 although flagged");
            return v;
        }
    }

    /// <summary>
    /// Body of <see cref="MessageKind.AgentRefresh"/> (client → host): agents whose last received values held a
    /// reference the client could not resolve yet (a partner spawned in a later batch). The host sends their current
    /// values again with focus priority. <c>u8 version | varint n | n × varint netId</c>.
    /// </summary>
    public static class AgentRefreshMessage
    {
        public const byte Version = 1;

        /// <summary>Ids in one request at most.</summary>
        public const int MaxIds = 1024;

        public static void Write(ByteWriter w, IReadOnlyList<uint> ids)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (ids.Count > MaxIds) throw new ArgumentException("too many ids", nameof(ids));
            w.WriteByte(Version);
            w.WriteVarUInt32((uint)ids.Count);
            for (int i = 0; i < ids.Count; i++) w.WriteVarUInt32(ids[i]);
        }

        /// <summary>Throws <see cref="DecodeException"/> for malformed input (zero ids are dropped).</summary>
        public static List<uint> Read(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            byte version = r.ReadByte();
            if (version != Version) throw new DecodeException("agent refresh version " + version);
            uint n = r.ReadVarUInt32();
            if (n > MaxIds || n > (uint)r.Remaining) throw new DecodeException("agent refresh with " + n + " ids");
            var list = new List<uint>((int)n);
            for (uint i = 0; i < n; i++)
            {
                uint id = r.ReadVarUInt32();
                if (id != 0) list.Add(id);
            }
            if (!r.IsAtEnd) throw new DecodeException("agent refresh has " + r.Remaining + " trailing bytes");
            return list;
        }
    }
}
