using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// This game's world by net id for Core's <see cref="StructureIntegrity"/> (ARCHITECTURE.md#host-validation-and-commit,
    /// #client-apply): which ids are alive, which are nodes, an edge's nodes, an entity's owner and the edges that end at a
    /// node (its <c>ConnectedEdge</c> list without middle connections, tool previews and edges being deleted).
    /// </summary>
    internal sealed class StructureFacts : IStructureWorld
    {
        private readonly NetIdSystem _ids;
        private EntityManager _em;

        public StructureFacts(NetIdSystem ids)
        {
            _ids = ids;
        }

        /// <summary>
        /// The world to read, and whether entities tagged <c>Deleted</c> still count as alive: a client checking its own
        /// proposal reads its world after its tool applied it, where the entities it deletes carry <c>Deleted</c>
        /// (they were alive before the change, which is what the check judges).
        /// </summary>
        public StructureFacts Use(EntityManager em, bool deletedCountAsAlive)
        {
            _em = em;
            DeletedCountAsAlive = deletedCountAsAlive;
            return this;
        }

        public bool DeletedCountAsAlive { get; private set; }

        private Entity Live(uint netId)
        {
            Entity e = netId == 0 ? Entity.Null : _ids.GetEntity(netId);
            if (e == Entity.Null || !_em.Exists(e)) return Entity.Null;
            if (!DeletedCountAsAlive && _em.HasComponent<Game.Common.Deleted>(e)) return Entity.Null;
            return e;
        }

        public bool IsAlive(uint netId) => Live(netId) != Entity.Null;

        public bool IsNode(uint netId)
        {
            Entity e = Live(netId);
            return e != Entity.Null && _em.HasComponent<Game.Net.Node>(e) && _em.HasBuffer<Game.Net.ConnectedEdge>(e);
        }

        public bool TryGetEdge(uint netId, out uint start, out uint end)
        {
            start = end = 0;
            Entity e = Live(netId);
            if (e == Entity.Null || !_em.HasComponent<Game.Net.Edge>(e)) return false;
            Game.Net.Edge edge = _em.GetComponentData<Game.Net.Edge>(e);
            start = _ids.GetNetId(edge.m_Start);
            end = _ids.GetNetId(edge.m_End);
            return true;
        }

        public bool TryGetOwner(uint netId, out uint owner)
        {
            owner = 0;
            Entity e = Live(netId);
            if (e == Entity.Null || !_em.HasComponent<Game.Common.Owner>(e)) return false;
            Entity o = _em.GetComponentData<Game.Common.Owner>(e).m_Owner;
            owner = o == Entity.Null ? 0 : _ids.GetNetId(o);
            return o != Entity.Null;
        }

        public void GetEdgesAt(uint nodeId, List<uint> into)
        {
            Entity node = Live(nodeId);
            if (node == Entity.Null || !_em.HasBuffer<Game.Net.ConnectedEdge>(node)) return;
            DynamicBuffer<Game.Net.ConnectedEdge> edges = _em.GetBuffer<Game.Net.ConnectedEdge>(node, true);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i].m_Edge;
                if (edge == Entity.Null || !_em.Exists(edge) || !_em.HasComponent<Game.Net.Edge>(edge)) continue;
                // edges already being deleted go with the node; tool previews are the local tool's own
                if (_em.HasComponent<Game.Common.Deleted>(edge) || _em.HasComponent<Game.Tools.Temp>(edge)) continue;
                Game.Net.Edge ends = _em.GetComponentData<Game.Net.Edge>(edge);
                if (ends.m_Start != node && ends.m_End != node) continue; // connected in its middle: the node's removal drops it
                into.Add(_ids.GetNetId(edge));
            }
        }
    }
}
