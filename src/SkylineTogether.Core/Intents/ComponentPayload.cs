using System;

namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// One replicated component of a proposed create or update: its canonical bytes (docs/history/ARCHITECTURE-original.md §4.3.4; entity references are
    /// <see cref="NetRef"/>s, predicted entities of the same proposal as provisional <see cref="LocalId"/>s) and, for
    /// enableable types, the enabled bit.
    /// <list type="bullet">
    /// <item>Value: <see cref="Bytes"/> holds the canonical bytes.</item>
    /// <item>Presence: <see cref="Bytes"/> is null or empty; the component is present (tags, and presence-authorable
    /// types, whose value the host sets to the vanilla default, docs/history/ARCHITECTURE-original.md Appendix F).</item>
    /// <item><see cref="Removed"/> (updates only): the update removes the component (presence-authorable types such as
    /// <c>TrafficLights</c>, <c>Owner</c> of kept sub-nets, docs/history/ARCHITECTURE-original.md Appendix F); no bytes and no enabled bit.</item>
    /// </list>
    /// A present component with null bytes is encoded like one with empty bytes; decoders return an empty array.
    /// <see cref="Enabled"/> is meaningful only with <see cref="HasEnabledBit"/> (the encoder drops it otherwise).
    /// </summary>
    public struct ComponentPayload
    {
        public TypeRank Type;

        /// <summary>Canonical bytes; null or empty for presence only.</summary>
        public byte[] Bytes;

        public bool HasEnabledBit;

        public bool Enabled;

        /// <summary>The update removes the component (additive to the plan's contract; never set in creates).</summary>
        public bool Removed;

        /// <summary>Length of <see cref="Bytes"/> (0 when null).</summary>
        public int Length => Bytes == null ? 0 : Bytes.Length;

        /// <summary>A component with canonical bytes.</summary>
        public static ComponentPayload Value(TypeRank type, byte[] canonical)
        {
            if (canonical == null) throw new ArgumentNullException(nameof(canonical));
            return new ComponentPayload { Type = type, Bytes = canonical };
        }

        /// <summary>A component with canonical bytes and the enabled bit of an enableable type.</summary>
        public static ComponentPayload WithEnabledBit(TypeRank type, byte[] canonical, bool enabled)
        {
            return new ComponentPayload { Type = type, Bytes = canonical ?? Array.Empty<byte>(), HasEnabledBit = true, Enabled = enabled };
        }

        /// <summary>The presence of a component without a value (tags, presence-authorable types).</summary>
        public static ComponentPayload Presence(TypeRank type)
        {
            return new ComponentPayload { Type = type, Bytes = Array.Empty<byte>() };
        }

        /// <summary>The removal of a component (updates of presence-authorable or player-authorable types).</summary>
        public static ComponentPayload Removal(TypeRank type)
        {
            return new ComponentPayload { Type = type, Removed = true };
        }

        public override string ToString()
        {
            if (Removed) return Type + " removed";
            string text = Type + " (" + Length + " bytes)";
            return HasEnabledBit ? text + (Enabled ? " enabled" : " disabled") : text;
        }
    }
}
