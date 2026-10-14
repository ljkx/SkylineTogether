using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// A uniform hash grid of points on the ground plane (x, z) keyed by net id: the host's in-view query for the
    /// attention model (ARCH §4.8.3 item 2, "in-view static set"). Positions are updated incrementally by the host's
    /// rolling scan; <see cref="Query"/> returns the ids within a radius of a camera pivot. Not thread-safe.
    /// </summary>
    public sealed class SpatialHashGrid
    {
        private struct Entry
        {
            public long Cell;
            public float X, Z;
            /// <summary>The id's index in its cell's list (O(1) removal; a tower's households share one cell).</summary>
            public int Slot;
        }

        private readonly float _cellSize;
        private readonly Dictionary<long, List<uint>> _cells = new Dictionary<long, List<uint>>();
        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>();

        /// <param name="cellSize">Edge length of one grid cell in metres (positive and finite).</param>
        public SpatialHashGrid(float cellSize)
        {
            if (!(cellSize > 0f) || float.IsInfinity(cellSize)) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "the cell size must be positive and finite");
            _cellSize = cellSize;
        }

        public int Count => _entries.Count;

        public float CellSize => _cellSize;

        public bool Contains(uint id) => _entries.ContainsKey(id);

        /// <summary>Inserts or moves a point. Non-finite coordinates remove it (it cannot be queried).</summary>
        public void Set(uint id, float x, float z)
        {
            if (float.IsNaN(x) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(z))
            {
                Remove(id);
                return;
            }
            long cell = CellOf(x, z);
            int slot;
            if (_entries.TryGetValue(id, out Entry old))
            {
                if (old.Cell != cell)
                {
                    RemoveFromCell(old.Cell, old.Slot);
                    slot = AddToCell(cell, id);
                }
                else
                {
                    slot = old.Slot;
                }
            }
            else
            {
                slot = AddToCell(cell, id);
            }
            _entries[id] = new Entry { Cell = cell, X = x, Z = z, Slot = slot };
        }

        public bool Remove(uint id)
        {
            if (!_entries.TryGetValue(id, out Entry old)) return false;
            _entries.Remove(id);
            RemoveFromCell(old.Cell, old.Slot);
            return true;
        }

        public void Clear()
        {
            _cells.Clear();
            _entries.Clear();
        }

        /// <summary>The stored position of a point.</summary>
        public bool TryGet(uint id, out float x, out float z)
        {
            if (_entries.TryGetValue(id, out Entry e))
            {
                x = e.X;
                z = e.Z;
                return true;
            }
            x = z = 0f;
            return false;
        }

        /// <summary>Adds every id within <paramref name="radius"/> of (cx, cz) to <paramref name="output"/>; returns how many.</summary>
        public int Query(float cx, float cz, float radius, ICollection<uint> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!(radius >= 0f) || float.IsNaN(cx) || float.IsNaN(cz) || float.IsInfinity(cx) || float.IsInfinity(cz)) return 0;
            if (float.IsInfinity(radius)) radius = float.MaxValue / 4f;
            double r2 = (double)radius * radius;
            long minX = Coord(cx - radius), maxX = Coord(cx + radius);
            long minZ = Coord(cz - radius), maxZ = Coord(cz + radius);
            int found = 0;
            // a radius larger than the populated area: walking the populated cells is cheaper than the empty ones
            // (it walked every entry, which is every agent of the city)
            if (((double)(maxX - minX) + 1.0) * ((double)(maxZ - minZ) + 1.0) > _cells.Count)
            {
                foreach (KeyValuePair<long, List<uint>> cell in _cells)
                {
                    long gx = cell.Key >> 32, gz = (int)(cell.Key & 0xFFFFFFFFL);
                    if (gx < minX || gx > maxX || gz < minZ || gz > maxZ) continue;
                    foreach (uint id in cell.Value)
                    {
                        if (Within(_entries[id], cx, cz, r2))
                        {
                            output.Add(id);
                            found++;
                        }
                    }
                }
                return found;
            }
            for (long gx = minX; gx <= maxX; gx++)
            {
                for (long gz = minZ; gz <= maxZ; gz++)
                {
                    if (!_cells.TryGetValue(Key(gx, gz), out List<uint> ids)) continue;
                    foreach (uint id in ids)
                    {
                        if (Within(_entries[id], cx, cz, r2))
                        {
                            output.Add(id);
                            found++;
                        }
                    }
                }
            }
            return found;
        }

        private static bool Within(Entry e, float cx, float cz, double r2)
        {
            double dx = e.X - cx, dz = e.Z - cz;
            return dx * dx + dz * dz <= r2;
        }

        private long Coord(float v) => (long)Math.Floor(Math.Max(-1e15, Math.Min(1e15, v / (double)_cellSize)));

        private long CellOf(float x, float z) => Key(Coord(x), Coord(z));

        private static long Key(long gx, long gz) => (gx << 32) ^ (gz & 0xFFFFFFFFL);

        private int AddToCell(long cell, uint id)
        {
            if (!_cells.TryGetValue(cell, out List<uint> list)) _cells[cell] = list = new List<uint>(4);
            list.Add(id);
            return list.Count - 1;
        }

        private void RemoveFromCell(long cell, int slot)
        {
            if (!_cells.TryGetValue(cell, out List<uint> list) || slot < 0 || slot >= list.Count) return;
            int last = list.Count - 1;
            if (slot != last)
            {
                uint moved = list[last];
                list[slot] = moved;
                Entry m = _entries[moved];
                m.Slot = slot;
                _entries[moved] = m;
            }
            list.RemoveAt(last);
            if (list.Count == 0) _cells.Remove(cell);
        }
    }
}
