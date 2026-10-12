using System;
using System.Runtime.InteropServices;

namespace SkylineTogether.Core.Replication
{
    /// <summary>
    /// A growable block of unmanaged memory (<c>Marshal.AllocHGlobal</c>) for the host shadows and the client's
    /// received-value cache (ARCH §4.5.1-4.5.2, §3.7): large and long-lived data that the game's incremental GC should
    /// neither scan nor move. The block is zeroed on allocation and growth. Not thread-safe; owners follow the engine's
    /// capture-window discipline (mutation on the main thread inside the window, reads by workers outside it).
    /// A finalizer frees the memory if <see cref="Dispose"/> was never called.
    /// </summary>
    internal sealed unsafe class NativeBuffer : IDisposable
    {
        private IntPtr _pointer;
        private long _capacity;

        public NativeBuffer(long capacity)
        {
            if (capacity < 1) capacity = 1;
            _pointer = Marshal.AllocHGlobal(new IntPtr(capacity));
            _capacity = capacity;
            Zero((byte*)_pointer, capacity);
        }

        ~NativeBuffer()
        {
            Free();
        }

        /// <summary>The start of the block; changes when the block grows.</summary>
        public byte* Pointer
        {
            get
            {
                if (_pointer == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeBuffer));
                return (byte*)_pointer;
            }
        }

        public long Capacity => _capacity;

        public bool IsDisposed => _pointer == IntPtr.Zero;

        /// <summary>Grows the block to at least <paramref name="minimum"/> bytes (doubling); new bytes are zero. Returns true when it moved.</summary>
        public bool EnsureCapacity(long minimum)
        {
            if (minimum <= _capacity) return false;
            if (_pointer == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeBuffer));
            long capacity = Math.Max(minimum, _capacity * 2);
            IntPtr grown = Marshal.ReAllocHGlobal(_pointer, new IntPtr(capacity));
            Zero((byte*)grown + _capacity, capacity - _capacity);
            _pointer = grown;
            _capacity = capacity;
            return true;
        }

        /// <summary>Replaces the block by a smaller one of <paramref name="capacity"/> bytes keeping the first bytes (after compaction).</summary>
        public void Shrink(long capacity)
        {
            if (_pointer == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeBuffer));
            if (capacity < 1) capacity = 1;
            if (capacity >= _capacity) return;
            _pointer = Marshal.ReAllocHGlobal(_pointer, new IntPtr(capacity));
            _capacity = capacity;
        }

        public void Dispose()
        {
            Free();
            GC.SuppressFinalize(this);
        }

        /// <summary>Zeroes <paramref name="count"/> bytes at <paramref name="destination"/>.</summary>
        public static void Zero(byte* destination, long count)
        {
            long words = count >> 3;
            ulong* w = (ulong*)destination;
            for (long i = 0; i < words; i++) w[i] = 0;
            for (long i = words << 3; i < count; i++) destination[i] = 0;
        }

        /// <summary>Copies <paramref name="count"/> bytes from a managed array into unmanaged memory.</summary>
        public static void CopyIn(byte[] source, int offset, byte* destination, int count)
        {
            if (count <= 0) return;
            fixed (byte* s = &source[offset])
            {
                Buffer.MemoryCopy(s, destination, count, count);
            }
        }

        private void Free()
        {
            IntPtr pointer = _pointer;
            _pointer = IntPtr.Zero;
            _capacity = 0;
            if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
        }
    }
}
