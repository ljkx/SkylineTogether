using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>What the applier does with a component value whose references did not all resolve here.</summary>
    public enum UnresolvedValueAction
    {
        /// <summary>Write the new value (references that did not resolve are null in it).</summary>
        Write,
        /// <summary>Keep the entity's current value.</summary>
        Keep,
    }

    /// <summary>
    /// The rule for a component value whose references do not all resolve on this client (ARCHITECTURE.md#reference-safety):
    /// an existing entity keeps its current value until a record that resolves arrives, as long as the kept value only
    /// names live entities. When the kept value names something gone or deleted, a value whose references may be null is
    /// written with the unresolved references null (the game checks those). A required link (<see cref="RequiredLinkTypes"/>:
    /// an object's owner, an edge's nodes) is never written as null over the entity's value: the game's Burst jobs index its
    /// target unchecked. The entity keeps its old value, and the dangling-entity pass deletes it and asks the host for it
    /// again, because its owner or node is missing or deleted. Pure.
    /// </summary>
    public static class UnresolvedValueRule
    {
        /// <summary>Component types whose entity references the game follows without a null check.</summary>
        public static readonly IReadOnlyList<string> RequiredLinkTypes = new[] { "Game.Common.Owner", "Game.Net.Edge" };

        /// <summary>True for a type of <see cref="RequiredLinkTypes"/> (full type name).</summary>
        public static bool IsRequiredLink(string typeFullName)
        {
            foreach (string t in RequiredLinkTypes)
            {
                if (string.Equals(t, typeFullName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Decides for one read: <paramref name="unresolved"/>, a reference of the new value did not resolve;
        /// <paramref name="keepOnUnresolved"/>, the entity exists (false for a spawn, which is refused elsewhere when a
        /// reference is missing); <paramref name="keptValueIsLive"/> is asked only when needed.
        /// </summary>
        public static UnresolvedValueAction Decide(bool unresolved, bool keepOnUnresolved, bool requiredLink, Func<bool> keptValueIsLive)
        {
            if (!unresolved || !keepOnUnresolved) return UnresolvedValueAction.Write;
            if (requiredLink) return UnresolvedValueAction.Keep; // live: kept until it resolves; dead: the dangling pass removes the entity
            return keptValueIsLive == null || keptValueIsLive() ? UnresolvedValueAction.Keep : UnresolvedValueAction.Write;
        }
    }
}
