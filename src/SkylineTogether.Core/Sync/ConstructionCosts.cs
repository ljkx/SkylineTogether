using System;

namespace SkylineTogether.Core.Sync
{
    /// <summary>The growth stage of a tree as the game prices it (its construction cost multiplier).</summary>
    public enum TreeGrowth : byte
    {
        /// <summary>Young trees and every other state: the prefab's base cost.</summary>
        Base = 0,
        Teen = 1,
        Adult = 2,
        Elderly = 3,
    }

    /// <summary>
    /// The host's lower bounds of what the game charges for building (ARCHITECTURE.md#host-validation-and-commit): a forged client can
    /// claim any cost, so the host never charges less than these for what a proposal created (<see cref="ProposalRules.EffectiveCost"/>).
    /// Every bound stays at or below the vanilla price, so an honest client's own sum is never raised:
    /// <list type="bullet">
    /// <item><b>Net edges</b> (roads, tracks, pipes, power lines): the game bills a new edge per unit of 8 m (rounded, at
    /// least one) at its composition's construction cost per unit, plus an elevation surcharge per unit and 10 m of
    /// height. The bound takes one unit less (rounding, and the halves of a split edge, which the game does not charge)
    /// and no elevation surcharge, and subtracts what the edges the proposal removed would cost with one unit more, so
    /// splitting or re-laying existing edges stays free.</item>
    /// <item><b>Upgrades</b> of an existing edge (a new composition on the same curve): the difference of the two
    /// compositions' prices over the edge, never negative, one unit of the new price less.</item>
    /// <item><b>Trees</b>: the prefab's cost times the multiplier of the tree's growth stage, as the object tool prices
    /// it.</item>
    /// </list>
    /// Pure.
    /// </summary>
    public static class ConstructionCosts
    {
        /// <summary>Metres per billing unit of a net edge.</summary>
        public const float MetresPerUnit = 8f;

        /// <summary>Metres of height per elevation step.</summary>
        public const float MetresPerElevationStep = 10f;

        /// <summary>The game's billing units of an edge of <paramref name="length"/> metres: round(length / 8), at least one.</summary>
        public static int EdgeUnits(float length)
        {
            if (float.IsNaN(length) || length <= 0) return 1;
            double units = Math.Round(Math.Min(length, 1e7f) / MetresPerUnit, MidpointRounding.ToEven);
            return Math.Max(1, (int)units);
        }

        /// <summary>
        /// Elevation steps of an edge from the lower elevation of each end node (metres): round(max(start, end) / 10), never
        /// negative.
        /// </summary>
        public static int ElevationSteps(float startLowest, float endLowest)
        {
            float top = Math.Max(Sanitize(startLowest), Sanitize(endLowest));
            if (top <= 0) return 0;
            return Math.Max(0, (int)Math.Round(Math.Min(top, 1e6f) / MetresPerElevationStep, MidpointRounding.ToEven));
        }

        /// <summary>What the game charges for a new edge: units × (cost per unit + steps × elevation cost per unit).</summary>
        public static long EdgeCost(float length, uint unitCost, uint elevationCost, int elevationSteps)
        {
            long perUnit = unitCost + (long)Math.Max(0, elevationSteps) * elevationCost;
            return Clamp(EdgeUnits(length) * perUnit);
        }

        /// <summary>The bound for a created edge: one unit less than the game bills, without the elevation surcharge.</summary>
        public static long CreatedEdgeFloor(float length, uint unitCost) => Clamp((long)(EdgeUnits(length) - 1) * unitCost);

        /// <summary>What a removed edge counts against the created ones: one unit more than the game bills at ground level.</summary>
        public static long RemovedEdgeCeiling(float length, uint unitCost) => Clamp((long)(EdgeUnits(length) + 1) * unitCost);

        /// <summary>
        /// The bound for an upgraded edge (old and new composition on the curve the edge has now): the game's
        /// non-negative price difference, one unit of the new price less.
        /// </summary>
        public static long UpgradeFloor(float length, int elevationSteps, uint oldUnitCost, uint oldElevationCost, uint newUnitCost, uint newElevationCost)
        {
            long diff = EdgeCost(length, newUnitCost, newElevationCost, elevationSteps) - EdgeCost(length, oldUnitCost, oldElevationCost, elevationSteps);
            long slack = newUnitCost + (long)Math.Max(0, elevationSteps) * newElevationCost;
            return Math.Max(0, diff - slack);
        }

        /// <summary>
        /// The bound for the nets of one proposal: what its created edges cost (<see cref="CreatedEdgeFloor"/>) minus what its
        /// removed edges would (<see cref="RemovedEdgeCeiling"/>), never negative, plus its upgrades (<see cref="UpgradeFloor"/>).
        /// </summary>
        public static long NetFloor(long createdFloors, long removedCeilings, long upgradeFloors)
        {
            long laid = Math.Max(0, Math.Max(0, createdFloors) - Math.Max(0, removedCeilings));
            return Clamp(laid + Math.Max(0, upgradeFloors));
        }

        /// <summary>A tree's price: the prefab's construction cost times the multiplier of its growth stage.</summary>
        public static long TreeCost(uint constructionCost, TreeGrowth growth, int teenMultiplier, int adultMultiplier, int elderlyMultiplier)
        {
            int multiplier;
            switch (growth)
            {
                case TreeGrowth.Teen: multiplier = teenMultiplier; break;
                case TreeGrowth.Adult: multiplier = adultMultiplier; break;
                case TreeGrowth.Elderly: multiplier = elderlyMultiplier; break;
                default: multiplier = 1; break;
            }
            return Clamp(constructionCost * (long)Math.Max(0, multiplier));
        }

        private static float Sanitize(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        private static long Clamp(long v) => Math.Max(0, Math.Min(int.MaxValue, v));
    }
}
