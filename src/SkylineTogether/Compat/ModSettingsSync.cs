using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Modding;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Host → clients channel for the simulation-relevant settings of other mods (docs/design/MOD-COMPATIBILITY.md;
    /// <see cref="SyncRecordKind.ModSettings"/>): the host's values of the properties each adapter lists
    /// (<see cref="ModAdapter.SyncedSettings"/>) apply to everyone for the session. The host sends what changed (polled
    /// once a second) and everything when a player joins; a client writes the values into the mod's live
    /// <c>ModSetting</c> object (through the property setter, so mods that react to a change do so), calls the setting's
    /// <c>Apply()</c>, and puts its own values back when the session ends (saving them again, because a mod's UI may
    /// have saved the settings file while the host's values were in effect). Only bool, number, enum and string
    /// properties are touched; buttons and anything else never are. Main thread only.
    /// </summary>
    public sealed class ModSettingsSync
    {
        private const double PollInterval = 1.0;

        private readonly ILog _log;
        private readonly HostSettingsTracker _host = new HostSettingsTracker();
        private readonly ClientSettingsTracker _client = new ClientSettingsTracker();
        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);
        private double _lastPoll = double.NegativeInfinity;
        private bool _resendAll = true;

        public ModSettingsSync(ILog log)
        {
            _log = log;
        }

        /// <summary>Number of settings currently replaced by the host's values (client).</summary>
        public int OverriddenCount => _client.Count;

        /// <summary>The next host capture sends every setting (a player joined).</summary>
        public void ResendAll() => _resendAll = true;

        public void Reset()
        {
            _host.Reset();
            _resendAll = true;
            _lastPoll = double.NegativeInfinity;
        }

        // -------------------------------------------------------------------------------------------------- host

        /// <summary>Host: appends a <see cref="SyncRecordKind.ModSettings"/> record with the changed settings (rate-limited).</summary>
        public void HostCapture(SyncBatch.Writer w, double now)
        {
            if (!_resendAll && now - _lastPoll < PollInterval) return;
            _lastPoll = now;
            bool all = _resendAll;
            _resendAll = false;
            List<SettingEntry> current = CurrentValues(WarnOnce);
            List<SettingEntry> changed = _host.Diff(current, all);
            for (int start = 0; start < changed.Count; start += SyncBatch.MaxModSettings)
            {
                w.ModSettings(changed.GetRange(start, Math.Min(SyncBatch.MaxModSettings, changed.Count - start)));
            }
        }

        /// <summary>
        /// The current values of every setting the active adapters list (<see cref="ModAdapter.SyncedSettings"/>): what the
        /// host pushes, and the settings list of the handshake manifest (ARCHITECTURE.md#handshake-and-admission), whose
        /// differences the joiner is told about. <paramref name="warn"/> (key, text, exception) reports read failures; null
        /// ignores them. Main thread.
        /// </summary>
        public static List<SettingEntry> CurrentValues(Action<string, string, Exception> warn = null)
        {
            var current = new List<SettingEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModAdapter a in ModCompat.ActiveAdapters)
            {
                foreach (string key in a.SyncedSettings)
                {
                    if (!seen.Add(a.AssemblyName + "\n" + key)) continue; // a built-in and a declared rule may name the same setting
                    object settings = FindSettings(a, a.SettingsTypeFor(key), warn);
                    if (settings == null) continue;
                    PropertyInfo p = Property(settings, key);
                    if (p == null || !p.CanRead) continue;
                    try
                    {
                        current.Add(new SettingEntry { Mod = a.AssemblyName, Key = key, Value = SettingValueText.Format(p.GetValue(settings)), SimulationRelevant = true, Pushable = true });
                    }
                    catch (Exception ex)
                    {
                        warn?.Invoke(a.ModName + "." + key, "reading " + a.ModName + " setting " + key + " failed", ex);
                    }
                }
            }
            return current;
        }

        // -------------------------------------------------------------------------------------------------- client

        /// <summary>Client: applies the host's values of a <see cref="SyncRecordKind.ModSettings"/> record.</summary>
        public void Apply(SyncRecord record)
        {
            if (record?.Settings == null) return;
            var touched = new List<object>();
            foreach (SettingEntry s in record.Settings)
            {
                ModAdapter a = ModCompat.FindWithSetting(s.Mod, s.Key);
                if (a == null) continue; // only declared settings of loaded mods
                object settings = FindSettings(a, a.SettingsTypeFor(s.Key), WarnOnce);
                PropertyInfo p = settings == null ? null : Property(settings, s.Key);
                if (p == null || !p.CanRead || !p.CanWrite) continue;
                try
                {
                    if (!SettingValueText.TryParse(s.Value, p.PropertyType, out object value))
                    {
                        WarnOnce(s.Mod + "." + s.Key + "=", "the host's " + a.ModName + " setting " + s.Key + " has a value this version cannot use: " + s.Value);
                        continue;
                    }
                    object old = p.GetValue(settings);
                    if (Equals(old, value)) continue;
                    _client.RememberOriginal(s.Mod, s.Key, SettingValueText.Format(old));
                    p.SetValue(settings, value);
                    if (!touched.Contains(settings)) touched.Add(settings);
                    _log.Info(LogCategory.Compat, "mod setting from the host: " + a.ModName + " " + s.Key + " = " + s.Value);
                }
                catch (Exception ex)
                {
                    WarnOnce(s.Mod + "." + s.Key, "applying the host's " + a.ModName + " setting " + s.Key + " failed", ex);
                }
            }
            foreach (object settings in touched) CallApply(settings, false);
        }

        /// <summary>Client, session end: puts the player's own values back (and saves them again).</summary>
        public void RestoreClient()
        {
            if (_client.Count == 0) return;
            var touched = new List<object>();
            foreach (SettingEntry s in _client.Originals())
            {
                try
                {
                    ModAdapter a = ModCompat.FindWithSetting(s.Mod, s.Key);
                    object settings = a == null ? null : FindSettings(a, a.SettingsTypeFor(s.Key), WarnOnce);
                    PropertyInfo p = settings == null ? null : Property(settings, s.Key);
                    if (p == null || !p.CanWrite || !SettingValueText.TryParse(s.Value, p.PropertyType, out object value)) continue;
                    p.SetValue(settings, value);
                    if (!touched.Contains(settings)) touched.Add(settings);
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Compat, "restoring the mod setting " + s + " failed", ex);
                }
            }
            foreach (object settings in touched) CallApply(settings, true);
            _log.Info(LogCategory.Compat, "restored " + _client.Count + " mod setting(s) after the session");
            _client.Clear();
        }

        // -------------------------------------------------------------------------------------------------- reflection

        /// <summary>
        /// The mod's live settings object: a field or property of the mod's <c>IMod</c> instance (any visibility) whose
        /// type is <paramref name="typeName"/> (or any <c>ModSetting</c>). The static registry of <c>ModSetting</c> is not
        /// used: mods create a second "defaults" instance that replaces the live one there.
        /// </summary>
        private static object FindSettings(ModAdapter a, string typeName, Action<string, string, Exception> warn)
        {
            try
            {
                Type wanted = typeName != null ? a.FindType(typeName) : null;
                object own = a.SettingsInstance();
                if (own != null && Matches(own.GetType(), wanted)) return own;
                foreach (object mod in ModInstances(a))
                {
                    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    foreach (PropertyInfo p in mod.GetType().GetProperties(Flags))
                    {
                        if (p.GetIndexParameters().Length != 0 || !p.CanRead) continue;
                        if (!Matches(p.PropertyType, wanted)) continue;
                        object v = p.GetValue(mod);
                        if (v != null) return v;
                    }
                    foreach (FieldInfo f in mod.GetType().GetFields(Flags))
                    {
                        if (!Matches(f.FieldType, wanted)) continue;
                        object v = f.GetValue(mod);
                        if (v != null) return v;
                    }
                }
            }
            catch (Exception ex)
            {
                warn?.Invoke(a.ModName + ".settings", "finding the settings of " + a.ModName + " failed", ex);
            }
            return null;
        }

        private static bool Matches(Type t, Type wanted) => wanted != null ? wanted.IsAssignableFrom(t) : typeof(ModSetting).IsAssignableFrom(t);

        private static IEnumerable<object> ModInstances(ModAdapter a)
        {
            ModManager manager = GameManager.instance?.modManager;
            if (manager == null) yield break;
            foreach (ModManager.ModInfo info in manager)
            {
                Assembly assembly = null;
                try
                {
                    if (info.asset != null && info.asset.isLoaded) assembly = info.asset.assembly;
                }
                catch (Exception)
                {
                    assembly = null;
                }
                if (assembly == null || assembly != a.Assembly) continue;
                foreach (IMod mod in info.instances)
                {
                    if (mod != null) yield return mod;
                }
            }
        }

        private static PropertyInfo Property(object settings, string name)
        {
            PropertyInfo p = settings.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return p != null && SettingValueText.IsSupported(p.PropertyType) && p.GetIndexParameters().Length == 0 ? p : null;
        }

        private void CallApply(object settings, bool save)
        {
            try
            {
                if (!(settings is Game.Settings.Setting s)) return;
                if (save) s.ApplyAndSave();
                else s.Apply();
            }
            catch (Exception ex)
            {
                WarnOnce(settings.GetType().FullName + ".apply", "applying " + settings.GetType().Name + " failed", ex);
            }
        }

        private void WarnOnce(string key, string text, Exception ex = null)
        {
            if (_warned.Add(key)) _log.Warn(LogCategory.Compat, "mod compatibility: " + text, ex);
        }
    }
}
