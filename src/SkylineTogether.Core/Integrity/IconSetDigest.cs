using System.Collections.Generic;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Sync;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Hashes of the icon channel (ARCHITECTURE.md#statistics-icons-and-flow-status, #integrity). <see cref="ExactSetHash"/> detects that an owner's icon set changed
    /// on the host (every field, so a moved or re-prioritised icon is re-sent). <see cref="Member"/> is the comparable form
    /// both peers compute from their live icons for the periodic digest: owner and target reduced to
    /// <see cref="SyncRef.NormalizedHash"/>, the notification prefab, the priority and the flags that describe the icon
    /// (<see cref="ComparedFlags"/>), but not the location (it is derived from each peer's own geometry). Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class IconSetDigest
    {
        /// <summary><c>IconFlags</c> compared between peers: Unique (1), IgnoreTarget (2), OnTop (8), SecondaryLocation (16).
        /// TargetLocation (4) and CustomLocation (32) only say where the icon is drawn.</summary>
        public const byte ComparedFlags = 0x1B;

        private const ulong MemberDomain = 0x510E527FADE682D1UL;
        private const ulong SetDomain = 0x9B05688C2B3E6C1FUL;

        /// <summary>The comparable digest member of one icon.</summary>
        public static ulong Member(ulong ownerNormalized, uint prefab, ulong targetNormalized, byte priority, byte flags)
        {
            ulong h = HashMix.Combine(MemberDomain, ownerNormalized);
            h = HashMix.Combine(h, prefab);
            h = HashMix.Combine(h, targetNormalized);
            h = HashMix.Combine(h, priority);
            return HashMix.Mix64(HashMix.Combine(h, (byte)(flags & ComparedFlags)));
        }

        /// <summary>Digest of every icon of every owner: wrapping sums, so the order of owners and icons does not matter.</summary>
        public struct Accumulator
        {
            public ulong Sum;
            public uint Owners;
            public uint Icons;

            public void AddIcon(ulong member)
            {
                unchecked
                {
                    Sum += member;
                }
                Icons++;
            }

            public void AddOwner() => Owners++;

            public bool Matches(uint owners, uint icons, ulong sum) => Owners == owners && Icons == icons && Sum == sum;
        }

        /// <summary>Order-independent hash of an owner's icon set over every field (host change detection); 0 for no icons.</summary>
        public static ulong ExactSetHash(IReadOnlyList<SyncIcon> icons)
        {
            if (icons == null || icons.Count == 0) return 0;
            ulong sum = 0;
            for (int i = 0; i < icons.Count; i++)
            {
                SyncIcon icon = icons[i];
                ulong h = HashMix.Combine(SetDomain, icon.Prefab);
                h = HashMix.Combine(h, icon.Target.Kind);
                h = HashMix.Combine(h, icon.Target.Id);
                h = HashMix.Combine(h, icon.Target.Prefab);
                h = HashMix.Combine(h, (ulong)(uint)icon.Target.Index);
                h = HashMix.Combine(h, ((ulong)(uint)icon.Target.X << 32) | (uint)icon.Target.Z);
                h = HashMix.Combine(h, (uint)icon.Target.Y);
                h = HashMix.Combine(h, ((ulong)icon.Priority << 8) | icon.Flags);
                h = HashMix.Combine(h, ((ulong)Bits(icon.X) << 32) | Bits(icon.Y));
                h = HashMix.Combine(h, Bits(icon.Z));
                unchecked
                {
                    sum += HashMix.Mix64(h);
                }
            }
            return HashMix.Combine(sum, (ulong)icons.Count);
        }

        private static unsafe uint Bits(float v) => *(uint*)&v;
    }
}
