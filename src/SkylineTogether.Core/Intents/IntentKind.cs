namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// Semantic intents (ARCH §5.3): effects that have a public vanilla executor on the host. The value travels in the
    /// INTENT item (<see cref="ProposalFormat"/>); never reorder. Every member names the <see cref="SemanticIntent"/>
    /// fields it uses; the <see cref="SemanticIntent"/> factories build exactly that shape and
    /// <see cref="ProposalValidator"/> checks it (unused fields are ignored). Targets are checked for liveness and, when
    /// <see cref="ValidationFacts.FamilyOf"/> is supplied, for their family. Permissions: <see cref="IntentPermissions"/>.
    /// </summary>
    public enum IntentKind : ushort
    {
        /// <summary>
        /// <c>PoliciesUISystem.SetPolicy(target, policy, active, adjustment)</c> (<c>PoliciesUISystem.cs:186</c>).
        /// <see cref="SemanticIntent.Target"/> = building, district or route (none = the City),
        /// <see cref="SemanticIntent.Prefab"/> = policy prefab (unlocked), <see cref="SemanticIntent.BoolValue"/> = active,
        /// <see cref="SemanticIntent.FloatValue"/> = adjustment.
        /// </summary>
        SetPolicy = 1,

        /// <summary>
        /// <c>TaxSystem</c> setters (<c>TaxSystem.cs:482, 684</c>; they clamp). <see cref="SemanticIntent.IntArray"/> =
        /// changed (index, value) pairs of the 92-int tax array (<c>TaxSystem.cs:345</c>), indices unique.
        /// </summary>
        SetTaxRates,

        /// <summary>
        /// <c>CityServiceBudgetSystem.SetServiceBudget(prefab, percent)</c> (<c>:1084</c>; it does not clamp).
        /// <see cref="SemanticIntent.Prefab"/> = service prefab, <see cref="SemanticIntent.IntValue"/> = percent.
        /// </summary>
        SetServiceBudget,

        /// <summary>
        /// <c>ServiceFeeSystem.SetFee(resource, fees, value)</c> (<c>:508</c>; it does not clamp).
        /// <see cref="SemanticIntent.IntValue"/> = <c>PlayerResource</c>, <see cref="SemanticIntent.FloatValue"/> = fee.
        /// </summary>
        SetServiceFee,

        /// <summary>
        /// <c>LoanSystem.ChangeLoan(amount)</c> (<c>Game.Tools/LoanSystem.cs:130</c>; re-clamped against host money).
        /// <see cref="SemanticIntent.IntValue"/> = target loan amount.
        /// </summary>
        ChangeLoan,

        /// <summary>
        /// <c>DevTreeSystem.Purchase(Entity)</c> (<c>Game.City/DevTreeSystem.cs:157</c>; it validates points, lock state
        /// and requirements). <see cref="SemanticIntent.Prefab"/> = dev-tree node prefab.
        /// </summary>
        PurchaseDevNode,

        /// <summary>
        /// Re-implemented tile purchase (ARCH §5.3). <see cref="SemanticIntent.IntArray"/> = tile slots (unique),
        /// <see cref="SemanticIntent.IntValue"/> = cost observed by the client (part of the money check).
        /// </summary>
        PurchaseMapTiles,

        /// <summary>
        /// <c>Deleted</c> through <c>EndFrameBarrier</c> (district, service upgrade, line from the UI).
        /// <see cref="SemanticIntent.Target"/> = slot, <see cref="SemanticIntent.BaseRevision"/> = its revision.
        /// </summary>
        Delete,

        /// <summary>
        /// <c>NameSystem.SetCustomName</c>. <see cref="SemanticIntent.Target"/> = slot, <see cref="SemanticIntent.Text"/>
        /// = name of at most 64 characters (null or empty clears it).
        /// </summary>
        Rename,

        /// <summary>The <c>cityName</c> setter. <see cref="SemanticIntent.Text"/> = city name (not null).</summary>
        SetCityName,

        /// <summary>
        /// Route and <c>RouteVehicle</c> colours plus <c>Event+ColorUpdated</c> (R10 §3.5).
        /// <see cref="SemanticIntent.Target"/> = route slot, <see cref="SemanticIntent.IntValue"/> = packed RGBA colour.
        /// </summary>
        SetRouteColor,

        /// <summary>
        /// Direct write of the <c>CustomMeshColor</c> buffer. <see cref="SemanticIntent.Target"/> = object slot,
        /// <see cref="SemanticIntent.Blob"/> = canonical bytes of the whole buffer (empty = reset).
        /// </summary>
        SetMeshColor,

        /// <summary>
        /// The <c>Building</c> historical flag. <see cref="SemanticIntent.Target"/> = building slot,
        /// <see cref="SemanticIntent.BoolValue"/> = historical.
        /// </summary>
        SetHistorical,

        /// <summary>
        /// Direct write of the <c>VehicleModel</c> buffer, sent whole (avoids <c>RemoveAtSwapBack</c> order divergence,
        /// R10 §5). <see cref="SemanticIntent.Target"/> = route or depot slot, <see cref="SemanticIntent.Blob"/> =
        /// canonical bytes of the whole buffer.
        /// </summary>
        SetVehicleModels,

        /// <summary>
        /// Direct write of the <c>ServiceDistrict</c> buffer, sent whole. <see cref="SemanticIntent.Target"/> = building
        /// slot, <see cref="SemanticIntent.IntArray"/> = district slots (unique; empty = none).
        /// </summary>
        SetServiceDistricts,

        /// <summary>
        /// The mesh colour palette, sent whole. <see cref="SemanticIntent.Target"/> = palette slot or none,
        /// <see cref="SemanticIntent.Blob"/> = canonical bytes of the whole palette buffer.
        /// </summary>
        EditPalette,

        /// <summary><c>LifePathEventSystem.FollowCitizen</c>. <see cref="SemanticIntent.Target"/> = citizen slot.</summary>
        Follow,

        /// <summary><c>LifePathEventSystem.UnfollowCitizen</c>. <see cref="SemanticIntent.Target"/> = citizen slot.</summary>
        Unfollow,

        /// <summary>
        /// Rebuild of a destroyed building or upgrade in place (ARCH §5.2 rules, §5.3, §5.4.3): the host reproduces the
        /// <c>TempFlags.Upgrade</c> path of <c>ApplyObjectsSystem</c> (<c>ApplyObjectsSystem.cs:378-397</c>).
        /// <see cref="SemanticIntent.Target"/> = object slot, <see cref="SemanticIntent.BaseRevision"/> = its revision,
        /// <see cref="SemanticIntent.Prefab"/> = new prefab (same prefab class, ARCH §5.4.1 rule 5),
        /// <see cref="SemanticIntent.IntValue"/> = cost.
        /// </summary>
        ReplaceObjectPrefab
    }
}
