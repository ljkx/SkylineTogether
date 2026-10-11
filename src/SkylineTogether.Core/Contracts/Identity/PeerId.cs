using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Session-local id of a peer: 0 is the host, clients get 1 ... 255 when admitted (<c>Welcome.AssignedPeer</c>).
    /// Origins of RESULT and BIND records are peer ids (ARCH §4.6.3).
    /// </summary>
    public readonly struct PeerId : IEquatable<PeerId>
    {
        /// <summary>The host (0); equal to <c>default(PeerId)</c>.</summary>
        public static readonly PeerId Host;

        /// <summary>The peer number (0 = host).</summary>
        public readonly byte Value;

        public PeerId(byte value)
        {
            Value = value;
        }

        /// <summary>True for the host (0).</summary>
        public bool IsHost => Value == 0;

        public bool Equals(PeerId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is PeerId other && Value == other.Value;

        public override int GetHashCode() => Value;

        public override string ToString() => IsHost ? "peer:host" : "peer:" + Value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(PeerId a, PeerId b) => a.Value == b.Value;

        public static bool operator !=(PeerId a, PeerId b) => a.Value != b.Value;
    }
}
