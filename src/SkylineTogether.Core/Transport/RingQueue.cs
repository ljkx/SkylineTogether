using System;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// A growable FIFO ring of structs with by-reference access to the head (the outbox, the network simulator and the
    /// UDP transport keep their queues in it). It grows by doubling and never shrinks, so a warmed-up queue does not
    /// allocate. Not thread-safe: the owner synchronises access.
    /// </summary>
    internal sealed class RingQueue<T>
    {
        private T[] _items;
        private int _head;
        private int _count;

        public RingQueue(int initialCapacity = 16)
        {
            int capacity = 4;
            while (capacity < initialCapacity) capacity <<= 1;
            _items = new T[capacity];
        }

        public int Count => _count;

        public bool IsEmpty => _count == 0;

        public void Enqueue(in T item)
        {
            if (_count == _items.Length) Grow();
            _items[(_head + _count) & (_items.Length - 1)] = item;
            _count++;
        }

        /// <summary>The oldest item, by reference (valid until the next structural change).</summary>
        public ref T PeekRef()
        {
            if (_count == 0) throw new InvalidOperationException("the queue is empty");
            return ref _items[_head];
        }

        /// <summary>The item at <paramref name="index"/> positions after the head, by reference.</summary>
        public ref T At(int index)
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return ref _items[(_head + index) & (_items.Length - 1)];
        }

        /// <summary>Removes and returns the oldest item.</summary>
        public T Dequeue()
        {
            if (_count == 0) throw new InvalidOperationException("the queue is empty");
            T item = _items[_head];
            _items[_head] = default;
            _head = (_head + 1) & (_items.Length - 1);
            _count--;
            return item;
        }

        /// <summary>Forgets every item (the caller releases what they hold first).</summary>
        public void Clear()
        {
            if (_count > 0) Array.Clear(_items, 0, _items.Length);
            _head = 0;
            _count = 0;
        }

        private void Grow()
        {
            var grown = new T[_items.Length * 2];
            for (int i = 0; i < _count; i++)
            {
                grown[i] = _items[(_head + i) & (_items.Length - 1)];
            }
            _items = grown;
            _head = 0;
        }
    }
}
