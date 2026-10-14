using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// A background thread that calls registered pump actions every few milliseconds, independent of the
    /// frame rate. The session ticks once per rendered frame, and while a host holds its simulation for a joiner, or a
    /// big city renders at 15-20 fps, a frame hitch used to leave the transport without chunks to send; this thread
    /// keeps <see cref="BlobSender"/> and <see cref="PeerOutbox"/> (both thread-safe, built for a network thread)
    /// topping up the bulk window between frames.
    /// <para>
    /// The thread starts with the first registration and sleeps (no polling) while nothing is registered. Disposing a
    /// registration waits for a round in progress, so its action never runs after <c>Dispose</c> returns; a pump action
    /// must therefore never wait for the thread that disposes it. Exceptions of an action are caught and logged with a
    /// rate limit. Use it only with connections whose <c>Send</c> and <c>GetStats</c> are thread-safe.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class BackgroundPump : IDisposable
    {
        private const long ErrorLogTicks = 10 * TimeSpan.TicksPerSecond;

        private readonly object _lock = new object();
        private readonly object _roundLock = new object();
        private readonly List<Registration> _items = new List<Registration>();
        private readonly ILog _log;
        private readonly string _name;
        private readonly int _intervalMs;
        private Registration[] _snapshot = new Registration[0];
        private Thread _thread;
        private bool _disposed;
        private long _rounds;
        private long _errors;
        private long _lastErrorLog = long.MinValue;

        private sealed class Registration : IDisposable
        {
            public BackgroundPump Owner;
            public Action Pump;
            public string Name;

            public void Dispose() => Owner.Remove(this);
        }

        public BackgroundPump(ILog log, string name = "Skyline Together pump", int intervalMilliseconds = 2)
        {
            if (intervalMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
            _log = log;
            _name = name ?? "pump";
            _intervalMs = intervalMilliseconds;
        }

        /// <summary>Completed rounds over every registered action (diagnostics, tests).</summary>
        public long Rounds => Interlocked.Read(ref _rounds);

        /// <summary>Actions that threw so far.</summary>
        public long Errors => Interlocked.Read(ref _errors);

        /// <summary>Registered actions.</summary>
        public int Count
        {
            get { lock (_lock) return _items.Count; }
        }

        /// <summary>Calls <paramref name="pump"/> from the background thread until the returned registration is disposed.</summary>
        public IDisposable Add(Action pump, string name = null)
        {
            if (pump == null) throw new ArgumentNullException(nameof(pump));
            var r = new Registration { Owner = this, Pump = pump, Name = name ?? "pump" };
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(_name);
                _items.Add(r);
                _snapshot = _items.ToArray();
                if (_thread == null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = _name };
                    _thread.Start();
                }
                Monitor.PulseAll(_lock);
            }
            return r;
        }

        /// <summary>Stops the thread (waits up to a second for the round in progress).</summary>
        public void Dispose()
        {
            Thread thread;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _items.Clear();
                _snapshot = new Registration[0];
                thread = _thread;
                Monitor.PulseAll(_lock);
            }
            if (thread != null && thread != Thread.CurrentThread) thread.Join(1000);
        }

        private void Remove(Registration r)
        {
            lock (_lock)
            {
                if (!_items.Remove(r)) return;
                _snapshot = _items.ToArray();
            }
            if (Thread.CurrentThread == _thread) return;
            // a round in progress may still be calling it: wait for that round (the next one no longer sees it)
            lock (_roundLock)
            {
            }
        }

        private void Run()
        {
            while (true)
            {
                Registration[] work;
                lock (_lock)
                {
                    while (_items.Count == 0 && !_disposed) Monitor.Wait(_lock);
                    if (_disposed) return;
                    work = _snapshot;
                }
                lock (_roundLock)
                {
                    foreach (Registration r in work)
                    {
                        lock (_lock)
                        {
                            if (!_items.Contains(r)) continue; // removed since the snapshot
                        }
                        try
                        {
                            r.Pump();
                        }
                        catch (Exception e) when (!(e is OutOfMemoryException) && !(e is ThreadAbortException))
                        {
                            Interlocked.Increment(ref _errors);
                            long now = DateTime.UtcNow.Ticks;
                            if (_lastErrorLog == long.MinValue || now - _lastErrorLog >= ErrorLogTicks)
                            {
                                _lastErrorLog = now;
                                _log?.Warn(LogCategory.Net, _name + ": " + r.Name + " failed (" + Errors.ToString(CultureInfo.InvariantCulture) + " error(s) so far)", e);
                            }
                        }
                    }
                }
                Interlocked.Increment(ref _rounds);
                if (_intervalMs > 0) Thread.Sleep(_intervalMs);
                else Thread.Yield();
            }
        }
    }
}
