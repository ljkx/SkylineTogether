using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// A record named a type-set id that the registry does not know. On a client this is a <c>Faulted</c> condition
    /// that leads to a snapshot resync, never a session-closing decode error (ARCH §3.6, §12.2).
    /// </summary>
    public sealed class UnknownTypeSetException : Exception
    {
        public UnknownTypeSetException(TypeSetId id) : base("unknown type set " + id)
        {
            Id = id;
        }

        public TypeSetId Id { get; }
    }
}
