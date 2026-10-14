using System;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// The host's range and shape checks of a client's global-state proposal (tax rates, service budgets
    /// and fees, the loan, policy sliders, names). The game layer reads the limits from the game's own data (fee
    /// parameters, policy slider ranges, whether a service budget is adjustable) and applies what these rules return;
    /// the game's own clamps (tax limits, loan creditworthiness) run afterwards. Pure.
    /// </summary>
    public static class GlobalProposalRules
    {
        /// <summary>Tax rate entries are offsets of a few percent; anything beyond ±100 is malformed.</summary>
        public const int TaxBound = 100;

        /// <summary>A service budget percentage (the game's slider stays well inside 0..300).</summary>
        public const int MaxBudget = 300;

        /// <summary>The fee cap when the game has no maximum for a resource.</summary>
        public const float MaxFee = 100000f;

        /// <summary>A custom name (entity or city) after sanitising.</summary>
        public const int MaxNameChars = 256;

        /// <summary>Entries one proposal of a channel may carry (a panel edit changes a handful).</summary>
        public const int MaxEntries = 1024;

        /// <summary>A policy slider value when the policy has no slider range (finite, but bounded).</summary>
        public const float MaxUnrangedAdjustment = 1e6f;

        public static bool WithinEntryLimit(int count) => count >= 0 && count <= MaxEntries;

        public static int ClampTax(int value) => Math.Max(-TaxBound, Math.Min(TaxBound, value));

        /// <summary>A budget percentage, clamped; false when the service's budget is not adjustable.</summary>
        public static bool TryBudget(int value, bool adjustable, out int clamped)
        {
            clamped = Math.Max(0, Math.Min(MaxBudget, value));
            return adjustable;
        }

        /// <summary>
        /// A service fee clamped to 0..<paramref name="max"/> (the game's maximum for the resource; <see cref="MaxFee"/>
        /// when it has none); false for a non-finite value or a fee the game does not let players adjust.
        /// </summary>
        public static bool TryFee(float value, bool adjustable, float max, out float clamped)
        {
            clamped = 0f;
            if (!adjustable || float.IsNaN(value) || float.IsInfinity(value)) return false;
            float cap = float.IsNaN(max) || float.IsInfinity(max) || max <= 0f ? MaxFee : Math.Min(max, MaxFee);
            clamped = Math.Max(0f, Math.Min(cap, value));
            return true;
        }

        /// <summary>The loan the player asks for (the game then clamps it to the city's creditworthiness).</summary>
        public static int ClampLoan(int value) => Math.Max(0, value);

        /// <summary>
        /// A policy adjustment clamped to the policy's slider range (<paramref name="hasRange"/>), else to ±
        /// <see cref="MaxUnrangedAdjustment"/>; false for a non-finite value or an inverted range.
        /// </summary>
        public static bool TryPolicyAdjustment(float value, bool hasRange, float min, float max, out float clamped)
        {
            clamped = 0f;
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            if (hasRange)
            {
                if (float.IsNaN(min) || float.IsNaN(max) || min > max) return false;
                clamped = Math.Max(min, Math.Min(max, value));
                return true;
            }
            clamped = Math.Max(-MaxUnrangedAdjustment, Math.Min(MaxUnrangedAdjustment, value));
            return true;
        }

        /// <summary>An entity's custom name: control and bidi characters stripped, at most <see cref="MaxNameChars"/> (empty resets the name).</summary>
        public static string CleanName(string name) => ProtocolText.Truncate(ProtocolText.Sanitize(name), MaxNameChars).Trim();

        /// <summary>The city's name, cleaned like <see cref="CleanName"/>; false when nothing is left.</summary>
        public static bool TryCityName(string name, out string clean)
        {
            clean = CleanName(name);
            return clean.Length > 0;
        }
    }
}
