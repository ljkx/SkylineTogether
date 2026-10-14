using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// w9/perf (trust): the host's own price and checks for a client's map tile purchase
    /// (<see cref="ActionKind.PurchaseMapTiles"/>). The client's observed cost is never charged: the host prices the
    /// still unowned tiles from its own world, the way the game's purchase panel does (studied in
    /// <c>MapTilePurchaseSystem</c>; reimplemented here, not copied):
    /// <list type="bullet">
    /// <item>a tile's value is the sum over its map features of <c>amount × baseline(feature) × 10 × featureCost × tileCostFactor</c>
    /// (each term rounded to float, summed as float), where the amounts are the tile's current feature amounts and the
    /// feature costs and the cost factor come from the tile's prefab;</item>
    /// <item>the selected values are sorted and the most valuable tile is multiplied by the number of tiles already owned,
    /// the next by that number plus one, and so on; the sum rounds to whole money.</item>
    /// </list>
    /// Permits are the larger of nine and the number of start tiles, plus the tiles of every unlocked milestone, minus
    /// the tiles already owned. The game has no adjacency rule (any unowned tile can be bought), so neither has the host.
    /// Pure; the game layer reads the ECS data.
    /// </summary>
    public static class MapTilePricing
    {
        /// <summary>Tiles every city may own without milestones.</summary>
        public const int AutoUnlockedTiles = 9;

        /// <summary>A map has 23 × 23 tiles; a purchase naming more is malformed.</summary>
        public const int MaxTilesPerPurchase = 1024;

        /// <summary>Feature amounts a tile can have (the game has nine map features).</summary>
        public const int MaxFeatures = 32;

        private const double TileSizeModifier = 1.0 / (623.304347826087 * 623.304347826087);
        private const double ResourceModifier = 8.0718994140625E-07;

        /// <summary>The per-feature normalisation: area features by the tile area, resources by a fixed scale, water and fish by 1.</summary>
        public static double BaselineModifier(int feature)
        {
            switch (feature)
            {
                case 0: // area
                case 1: // buildable land
                    return TileSizeModifier;
                case 2: // fertile land
                case 3: // forest
                case 4: // oil
                case 5: // ore
                case 7: // ground water
                    return ResourceModifier;
                default: // 6 surface water, 8 fish and anything a later version adds
                    return 1.0;
            }
        }

        /// <summary>
        /// The value of one tile: <paramref name="amounts"/> are the tile's feature amounts, <paramref name="featureCosts"/>
        /// its prefab's per-feature costs (a feature without a cost is free), <paramref name="costFactor"/> the prefab's
        /// purchase cost factor. Non-finite or negative inputs count as zero, so a corrupted tile is never cheaper than
        /// free and never makes the price NaN.
        /// </summary>
        public static float TileValue(IReadOnlyList<float> amounts, IReadOnlyList<float> featureCosts, float costFactor)
        {
            if (amounts == null || featureCosts == null) return 0f;
            double factor = Sane(costFactor);
            float value = 0f;
            int n = Math.Min(Math.Min(amounts.Count, featureCosts.Count), MaxFeatures);
            for (int j = 0; j < n; j++)
            {
                value += (float)(Sane(amounts[j]) * BaselineModifier(j) * 10.0 * Sane(featureCosts[j]) * factor);
            }
            return float.IsInfinity(value) || float.IsNaN(value) ? float.MaxValue : value;
        }

        /// <summary>
        /// The price of buying the tiles valued <paramref name="tileValues"/> (sorted in place) when
        /// <paramref name="ownedTiles"/> are owned already; clamped to <see cref="int.MaxValue"/>.
        /// </summary>
        public static int Cost(List<float> tileValues, int ownedTiles)
        {
            if (tileValues == null || tileValues.Count == 0) return 0;
            tileValues.Sort();
            int owned = Math.Max(0, ownedTiles);
            float sum = 0f;
            for (int k = 0; k < tileValues.Count; k++)
            {
                sum += tileValues[tileValues.Count - k - 1] * (owned + k);
            }
            if (float.IsNaN(sum) || sum >= int.MaxValue) return int.MaxValue;
            if (sum <= 0) return 0;
            return (int)Math.Round((double)sum, MidpointRounding.ToEven);
        }

        /// <summary>The tiles the city may still buy: max(9, start tiles) + milestone tiles − owned, at least 0.</summary>
        public static int AvailablePermits(int startTiles, long milestoneTiles, int ownedTiles)
        {
            long total = Math.Max(AutoUnlockedTiles, startTiles) + Math.Max(0, milestoneTiles);
            return (int)Math.Max(0, Math.Min(int.MaxValue, total - Math.Max(0, ownedTiles)));
        }

        /// <summary>
        /// The host's verdict on a purchase of <paramref name="tiles"/> still unowned tiles at <paramref name="cost"/>
        /// (from <see cref="Cost"/>), with <paramref name="permits"/> left and <paramref name="money"/> in the treasury.
        /// </summary>
        public static RejectReason Validate(int tiles, int permits, int cost, long money, bool unlimitedMoney)
        {
            if (tiles < 0 || tiles > MaxTilesPerPurchase || cost < 0) return RejectReason.Invalid;
            if (tiles == 0) return RejectReason.None; // bought by somebody else meanwhile: nothing to charge
            if (tiles > permits) return RejectReason.Locked;
            if (!unlimitedMoney && cost > money) return RejectReason.NotEnoughMoney;
            return RejectReason.None;
        }

        /// <summary>
        /// Whether the client's observed cost is far enough from the host's price to be worth a log line (amounts drift
        /// between the client's pricing and the host's: forests grow, resources deplete).
        /// </summary>
        public static bool Differs(long observed, int hostCost) => Math.Abs(observed - hostCost) > Math.Max(100, hostCost / 20);

        private static double Sane(float v) => float.IsNaN(v) || float.IsInfinity(v) || v < 0 ? 0.0 : v;
    }
}
