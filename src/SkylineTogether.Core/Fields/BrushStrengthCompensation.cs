using System;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Frame-time compensation of replayed terrain brush stamps (ARCH §4.10.9). <c>TerrainSystem.ApplyBrush</c> passes
    /// the calling machine's <c>Time.unscaledDeltaTime</c> to <c>ApplyToTerrain</c>, which computes the height change of
    /// a stamp as
    /// <code>
    /// s = brush.m_Strength
    /// if (|s| &lt; 0.002) s = sign(s) · 0.002 · (|s| / 0.002)^0.055          // f(s)
    /// delta = clamp(unscaledDeltaTime, 0.05, 1) · s · toolBaseStrength / heightScale
    /// </code>
    /// (<c>TerrainSystem.cs:3765-3774, 3968-3979</c> [V]). A stamp replayed on another machine, or several stamps
    /// replayed in one frame, would therefore change the terrain by a different amount. Each captured stamp records
    /// the originator's frame time <c>dt₀</c>; a replaying machine passes the strength <c>s′</c> with
    /// <c>clamp(dt_local) · f(s′) = clamp(dt₀) · f(s)</c> instead, so both apply the same height change.
    /// <para>
    /// <see cref="Map"/> reproduces the game's float arithmetic bit for bit, including <c>Unity.Mathematics</c>'
    /// <c>pow</c> (through double), <c>sign</c> and <c>clamp</c> (verified in the IL of <c>Unity.Mathematics.dll</c>:
    /// <c>clamp(x, a, b) = max(a, min(b, x))</c> with <c>min(x, y) = IsNaN(y) || x &lt; y ? x : y</c>, so a NaN frame
    /// time clamps to 1). The game skips a stamp whose strength or frame time is exactly 0; no strength can compensate a
    /// local frame time of 0, which Unity does not report in practice.
    /// </para>
    /// <para>
    /// f applies to the strength that <c>ApplyToTerrain</c> receives. <c>ApplyBrushesSystem.ApplyHeight</c> adjusts the
    /// captured <c>Brush.m_Strength</c> before it calls <c>ApplyBrush</c> (a negative Soften becomes <c>|s| · 2</c>, a
    /// negative Level or Slope is skipped; <c>ApplyBrushesSystem.cs:302-313</c> [V]), so a replaying machine applies
    /// that adjustment first and compensates the result.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class BrushStrengthCompensation
    {
        /// <summary>Frame-time clamp and strength-curve constants of <c>TerrainSystem.ApplyToTerrain</c>.</summary>
        public const float MinDelta = 0.05f, MaxDelta = 1f, LinearThreshold = 0.002f, LowExponent = 0.055f;

        private const double InverseExponent = 1.0 / LowExponent;

        /// <summary>
        /// <c>math.clamp(deltaTime, 0.05f, 1f)</c> exactly as the game computes it (NaN → 1, ±∞ → the bounds). The
        /// originator records this value as the stamp's clamped frame time.
        /// </summary>
        public static float ClampDelta(float deltaTime) => UnityMax(MinDelta, UnityMin(MaxDelta, deltaTime));

        /// <summary>The game's strength mapping f(s): the identity for |s| ≥ 0.002, a 0.055-power curve below.</summary>
        public static float Map(float strength)
        {
            float s = strength;
            if (Math.Abs(s) < LinearThreshold)
            {
                float sign = (s > 0f ? 1f : 0f) - (s < 0f ? 1f : 0f);
                float x = Math.Abs(s) / LinearThreshold;
                s = (float)Math.Pow(x, LowExponent) * LinearThreshold * sign;
            }
            return s;
        }

        /// <summary>
        /// The inverse f⁻¹: the identity for |m| ≥ 0.002, <c>sign(m) · 0.002 · (|m| / 0.002)^(1/0.055)</c> below
        /// (computed in double precision). A nonzero value never maps back to zero (the game would skip such a stamp):
        /// below the float range the result is the smallest subnormal of the same sign. NaN, ±0 and ±∞ map to
        /// themselves.
        /// </summary>
        public static float Unmap(float mapped)
        {
            if (float.IsNaN(mapped) || mapped == 0f || Math.Abs(mapped) >= LinearThreshold) return mapped;
            return InverseBelowThreshold(mapped);
        }

        /// <summary>
        /// The strength <c>s′</c> to pass to <c>ApplyBrush</c> on this machine so that the stamp changes the terrain as
        /// much as <paramref name="strength"/> did on the originator: <c>clamp(dt_local) · f(s′) = clamp(dt₀) · f(s)</c>.
        /// Equal clamped frame times return the strength unchanged (bit for bit); a NaN or infinite strength (invalid
        /// input) returns 0, which the game ignores; results are limited to the finite float range.
        /// </summary>
        public static float Compensate(float strength, float originDeltaTime, float localDeltaTime)
        {
            if (float.IsNaN(strength) || float.IsInfinity(strength)) return 0f;
            if (strength == 0f) return strength;
            float origin = ClampDelta(originDeltaTime);
            float local = ClampDelta(localDeltaTime);
            if (origin == local) return strength;
            // the clamped times lie in [0.05, 1], so the ratio lies in [0.05, 20]
            double target = Map(strength) * (double)origin / local;
            double magnitude = Math.Abs(target);
            if (magnitude >= LinearThreshold)
            {
                if (magnitude > float.MaxValue) return target < 0 ? -float.MaxValue : float.MaxValue;
                return (float)target;
            }
            return InverseBelowThreshold(target);
        }

        /// <summary>
        /// <c>clamp(deltaTime) · f(strength)</c> in the game's float order: the factor that <c>ApplyToTerrain</c>
        /// multiplies with <c>toolBaseStrength / heightScale</c>. Compensation keeps it equal across machines.
        /// </summary>
        public static float EffectiveStrength(float strength, float deltaTime) => ClampDelta(deltaTime) * Map(strength);

        /// <summary>f⁻¹ for 0 &lt; |m| &lt; 0.002.</summary>
        private static float InverseBelowThreshold(double mapped)
        {
            double s = Math.Pow(Math.Abs(mapped) / LinearThreshold, InverseExponent) * LinearThreshold;
            float result = (float)s;
            if (result == 0f) result = float.Epsilon;
            return mapped < 0 ? -result : result;
        }

        // Unity.Mathematics.math.min/max for floats (IL of Unity.Mathematics.dll)
        private static float UnityMin(float x, float y) => float.IsNaN(y) || x < y ? x : y;

        private static float UnityMax(float x, float y) => float.IsNaN(y) || x > y ? x : y;
    }
}
