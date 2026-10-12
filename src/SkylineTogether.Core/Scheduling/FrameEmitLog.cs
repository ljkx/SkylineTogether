namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// The type bits already emitted for each slot during the current frame of one peer. Dirty bits are cleared only once
    /// the peer's outbox accepted the message (ARCH §4.5.3), i.e. after the frame's scheduling, so without this log a slot
    /// reached by two passes (in view and in a stale bucket) would be encoded twice. Open addressing with frame stamps:
    /// <see cref="NextFrame"/> forgets everything in O(1); the table grows only when a frame emits more slots than ever
    /// before. Single owner.
    /// </summary>
    internal sealed class FrameEmitLog
    {
        private const int MaxCapacity = 1 << 22;

        private int[] _keys = new int[1024];
        private ulong[] _masks = new ulong[1024];
        private int[] _stamps = new int[1024];
        private int _stamp = 1;
        private int _count;

        /// <summary>Slots logged in the current frame.</summary>
        public int Count => _count;

        public void NextFrame()
        {
            _count = 0;
            if (_stamp == int.MaxValue)
            {
                System.Array.Clear(_stamps, 0, _stamps.Length);
                _stamp = 0;
            }
            _stamp++;
        }

        /// <summary>The type bits emitted for <paramref name="slot"/> in this frame (0 if none).</summary>
        public ulong Emitted(int slot)
        {
            int mask = _keys.Length - 1;
            int i = Hash(slot) & mask;
            while (_stamps[i] == _stamp)
            {
                if (_keys[i] == slot) return _masks[i];
                i = (i + 1) & mask;
            }
            return 0;
        }

        /// <summary>Records that <paramref name="bits"/> of <paramref name="slot"/> were emitted in this frame.</summary>
        public void Add(int slot, ulong bits)
        {
            if (_count * 2 >= _keys.Length && _keys.Length < MaxCapacity) Grow();
            int mask = _keys.Length - 1;
            int i = Hash(slot) & mask;
            while (_stamps[i] == _stamp)
            {
                if (_keys[i] == slot)
                {
                    _masks[i] |= bits;
                    return;
                }
                i = (i + 1) & mask;
            }
            if (_count >= _keys.Length - 1) return;   // full at the maximum capacity: a duplicate emission is harmless
            _stamps[i] = _stamp;
            _keys[i] = slot;
            _masks[i] = bits;
            _count++;
        }

        private void Grow()
        {
            int[] oldKeys = _keys, oldStamps = _stamps;
            ulong[] oldMasks = _masks;
            int capacity = oldKeys.Length * 2;
            _keys = new int[capacity];
            _masks = new ulong[capacity];
            _stamps = new int[capacity];
            int stamp = _stamp;
            _stamp = 1;
            _count = 0;
            for (int j = 0; j < oldKeys.Length; j++)
            {
                if (oldStamps[j] != stamp) continue;
                int mask = capacity - 1;
                int i = Hash(oldKeys[j]) & mask;
                while (_stamps[i] == _stamp) i = (i + 1) & mask;
                _stamps[i] = _stamp;
                _keys[i] = oldKeys[j];
                _masks[i] = oldMasks[j];
                _count++;
            }
        }

        private static int Hash(int slot)
        {
            unchecked
            {
                uint h = (uint)slot * 0x9E3779B1u;
                return (int)(h ^ (h >> 15));
            }
        }
    }
}
