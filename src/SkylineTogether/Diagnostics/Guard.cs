using System;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// The try/catch around every entry point the game calls (systems' <c>OnUpdate</c>, event handlers, UI triggers and
    /// getters, Steam callbacks, the driver's <c>Update</c>), ARCHITECTURE.md#feature-health-and-guards <c>SafeRun</c>: nothing throws into the game
    /// loop. Failures are counted per feature (<see cref="FeatureHealth"/>), logged with a rate limit, and repeated
    /// failures turn the feature off with a toast; a critical feature that trips ends the session instead (a client is
    /// then offered a rejoin with a fresh snapshot). Main thread only.
    /// </summary>
    public static class Guard
    {
        /// <summary>Raised when a critical feature tripped (the session manager ends the session).</summary>
        public static event Action<FeatureHealth> CriticalTripped;

        /// <summary>Solo testing: every failure reported to the guard (feature, where, exception), before it is counted.</summary>
        public static event Action<FeatureHealth, string, Exception> Reported;

        private static double Now
        {
            get
            {
                try
                {
                    return UnityEngine.Time.realtimeSinceStartupAsDouble;
                }
                catch (Exception)
                {
                    return Environment.TickCount / 1000.0; // outside Unity (never expected)
                }
            }
        }

        /// <summary>Runs <paramref name="body"/> when the feature is available; false when it was skipped or failed.</summary>
        public static bool Run(FeatureHealth feature, string where, Action body)
        {
            if (feature != null && !feature.IsAvailable) return false;
            try
            {
                body();
                return true;
            }
            catch (Exception ex)
            {
                Report(feature, where, ex);
                return false;
            }
        }

        /// <summary>Runs <paramref name="body"/> even when the feature is off (cleanup paths), counting failures all the same.</summary>
        public static bool RunAlways(FeatureHealth feature, string where, Action body)
        {
            try
            {
                body();
                return true;
            }
            catch (Exception ex)
            {
                Report(feature, where, ex);
                return false;
            }
        }

        /// <summary>A getter for the UI: <paramref name="fallback"/> when the feature is off or the getter throws.</summary>
        public static T Get<T>(FeatureHealth feature, string where, Func<T> getter, T fallback)
        {
            if (feature != null && !feature.IsAvailable) return fallback;
            try
            {
                return getter();
            }
            catch (Exception ex)
            {
                Report(feature, where, ex);
                return fallback;
            }
        }

        /// <summary>
        /// A getter that keeps working after its feature tripped, but then tries only every <paramref name="retrySeconds"/>
        /// (returning <paramref name="fallback"/> in between): the panel is also the way out of a broken session.
        /// </summary>
        public static T GetRetrying<T>(FeatureHealth feature, string where, Func<T> getter, T fallback, ref double nextRetry, double retrySeconds = 1.0)
        {
            if (feature != null && !feature.IsAvailable)
            {
                double now = Now;
                if (now < nextRetry) return fallback;
                nextRetry = now + retrySeconds;
            }
            try
            {
                return getter();
            }
            catch (Exception ex)
            {
                Report(feature, where, ex);
                return fallback;
            }
        }

        /// <summary>Records a caught exception of <paramref name="feature"/> (rate-limited log; may turn the feature off).</summary>
        public static void Report(FeatureHealth feature, string where, Exception ex)
        {
            try
            {
                Reported?.Invoke(feature, where, ex); // solo testing: the replay verdict lists every failure
            }
            catch (Exception)
            {
                // a listener must never break the guard
            }
            try
            {
                ILog log = Mod.Log;
                if (feature == null)
                {
                    log?.Error(LogCategory.Diagnostics, where + " failed", ex);
                    return;
                }
                FailureVerdict v = feature.ReportFailure(ex, Now);
                if (v.Log && log != null)
                {
                    string text = "[" + feature.Id + "] " + where + " failed" + (v.Suppressed > 0 ? " (" + v.Suppressed + " similar errors not logged)" : "");
                    if (feature.TotalFailures == 1) log.Error(LogCategory.Diagnostics, text, ex);
                    else log.Warn(LogCategory.Diagnostics, text + ": " + (ex != null ? ex.GetType().Name + ": " + ex.Message : "?"));
                }
                if (v.JustTripped) OnTripped(feature);
            }
            catch (Exception)
            {
                // the guard itself must never throw
            }
        }

        /// <summary>Turns a feature off at once (not after repeated errors), with the same toast and consequences.</summary>
        public static void Disable(FeatureHealth feature, string reason)
        {
            if (feature == null || !feature.Trip(reason)) return;
            Mod.Log?.Warn(LogCategory.Diagnostics, "[" + feature.Id + "] turned off: " + reason);
            OnTripped(feature);
        }

        private static void OnTripped(FeatureHealth feature)
        {
            Features.Registry.Touch();
            Mod.Log?.Error(LogCategory.Diagnostics, "[" + feature.Id + "] " + feature.Title + " turned off after repeated errors (" + feature.TripReason + ")");
            if (feature.Critical)
            {
                Action<FeatureHealth> handler = CriticalTripped;
                try
                {
                    handler?.Invoke(feature);
                }
                catch (Exception ex)
                {
                    Mod.Log?.Error(LogCategory.Diagnostics, "ending the session after a critical failure failed", ex);
                }
            }
            else
            {
                SkylineTogether.Session.SessionManager.Notify(Settings.LocaleSource.Resolve("st.toast.featureOff", "FEATURE", Features.LocalTitle(feature)), Colossal.PSI.Common.ProgressState.Warning);
            }
        }
    }
}
