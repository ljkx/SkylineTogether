using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// A host transport made of a primary transport (Steam, or none for a session without remote players) and in-process
    /// transports attached while the session runs (solo testing: the recorder peer's <see cref="LoopbackTransport"/>,
    /// ARCH §13.5). The host sees one transport: <see cref="Poll"/> polls every part, <see cref="Dispose"/> disposes every
    /// part. Attached parts listen on their own (the caller calls their <see cref="ITransport.Listen"/>).
    /// </summary>
    public sealed class CompositeTransport : ITransport
    {
        private readonly ITransport _primary;
        private readonly List<ITransport> _attached = new List<ITransport>();
        private readonly List<ITransport> _pollList = new List<ITransport>();
        private bool _disposed;

        /// <param name="primary">The network transport; null for a host that only serves in-process peers.</param>
        public CompositeTransport(ITransport primary)
        {
            _primary = primary;
        }

        /// <summary>The network transport, or null.</summary>
        public ITransport Primary => _primary;

        public TransportKind Kind => _primary != null ? _primary.Kind : TransportKind.Loopback;

        /// <summary>Parts attached so far.</summary>
        public int AttachedCount
        {
            get
            {
                lock (_attached) return _attached.Count;
            }
        }

        /// <summary>Adds an in-process transport; it is polled with the host from now on and disposed with it.</summary>
        public void Attach(ITransport transport)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            lock (_attached)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CompositeTransport));
                if (!_attached.Contains(transport)) _attached.Add(transport);
            }
        }

        /// <summary>Removes an attached transport (not disposed: it belongs to the caller again). False when unknown.</summary>
        public bool Detach(ITransport transport)
        {
            lock (_attached) return _attached.Remove(transport);
        }

        [ThreadAffinity(ThreadKind.Main)]
        public void Listen(ListenOptions options)
        {
            _primary?.Listen(options);
        }

        [ThreadAffinity(ThreadKind.Main)]
        public IConnection Connect(Endpoint endpoint)
        {
            if (_primary == null) throw new InvalidOperationException("this host transport has no network part");
            return _primary.Connect(endpoint);
        }

        [ThreadAffinity(ThreadKind.Network)]
        public void Poll(ITransportEventSink sink)
        {
            if (_disposed) return;
            _primary?.Poll(sink);
            _pollList.Clear();
            lock (_attached) _pollList.AddRange(_attached);
            foreach (ITransport t in _pollList) t.Poll(sink);
            _pollList.Clear();
        }

        public void Dispose()
        {
            ITransport[] parts;
            lock (_attached)
            {
                if (_disposed) return;
                _disposed = true;
                parts = _attached.ToArray();
                _attached.Clear();
            }
            try
            {
                _primary?.Dispose();
            }
            finally
            {
                foreach (ITransport t in parts)
                {
                    try
                    {
                        t.Dispose();
                    }
                    catch (Exception)
                    {
                        // an in-process part must never keep the host from shutting down
                    }
                }
            }
        }
    }
}
