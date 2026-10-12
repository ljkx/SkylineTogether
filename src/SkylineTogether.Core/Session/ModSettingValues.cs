using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The invariant text form of a mod setting value (<see cref="SettingEntry.Value"/>): booleans as <c>true</c>/<c>false</c>,
    /// integers and floating-point numbers in invariant culture (floats round-trip exactly), enums by member name, strings
    /// verbatim. Only these types are synchronised; anything else is refused by <see cref="IsSupported"/>. Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class SettingValueText
    {
        /// <summary>True for the value types a mod setting may have to be synchronised.</summary>
        public static bool IsSupported(Type type)
        {
            if (type == null) return false;
            return type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double)
                   || type == typeof(string) || type.IsEnum;
        }

        /// <summary>The text form of a value (null → empty string).</summary>
        public static string Format(object value)
        {
            switch (value)
            {
                case null:
                    return "";
                case bool b:
                    return b ? "true" : "false";
                case float f:
                    return f.ToString("R", CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("R", CultureInfo.InvariantCulture);
                case int i:
                    return i.ToString(CultureInfo.InvariantCulture);
                case long l:
                    return l.ToString(CultureInfo.InvariantCulture);
                case string s:
                    return s;
                case Enum e:
                    return e.ToString();
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
        }

        /// <summary>Parses <paramref name="text"/> as <paramref name="type"/>; false for malformed text or an unsupported type.</summary>
        public static bool TryParse(string text, Type type, out object value)
        {
            value = null;
            if (text == null || !IsSupported(type)) return false;
            if (type == typeof(string))
            {
                value = text;
                return true;
            }
            if (type == typeof(bool))
            {
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) value = true;
                else if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) value = false;
                return value != null;
            }
            if (type == typeof(int))
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return false;
                value = i;
                return true;
            }
            if (type == typeof(long))
            {
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return false;
                value = l;
                return true;
            }
            if (type == typeof(float))
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || float.IsNaN(f) || float.IsInfinity(f)) return false;
                value = f;
                return true;
            }
            if (type == typeof(double))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsNaN(d) || double.IsInfinity(d)) return false;
                value = d;
                return true;
            }
            // enums by member name only (a number could select a member that does not exist in this version)
            if (text.Length == 0 || char.IsDigit(text[0]) || text[0] == '-') return false;
            try
            {
                object parsed = Enum.Parse(type, text, false);
                if (!Enum.IsDefined(type, parsed)) return false;
                value = parsed;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Host side of the mod settings channel: which settings changed since they were last sent. Keys are
    /// <c>mod|key</c>; the first call (or one after <see cref="Reset"/> or with <c>all</c>) returns every setting.
    /// </summary>
    public sealed class HostSettingsTracker
    {
        private readonly Dictionary<string, string> _sent = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The settings to send: those whose value differs from the last one sent (every one when <paramref name="all"/>).</summary>
        public List<SettingEntry> Diff(IReadOnlyList<SettingEntry> current, bool all)
        {
            var changed = new List<SettingEntry>();
            if (current == null) return changed;
            foreach (SettingEntry s in current)
            {
                if (s == null) continue;
                string key = KeyOf(s.Mod, s.Key);
                string value = s.Value ?? "";
                if (!all && _sent.TryGetValue(key, out string old) && old == value) continue;
                _sent[key] = value;
                changed.Add(new SettingEntry { Mod = s.Mod, Key = s.Key, Value = value, SimulationRelevant = s.SimulationRelevant, Pushable = s.Pushable });
            }
            return changed;
        }

        public void Reset() => _sent.Clear();

        internal static string KeyOf(string mod, string key) => (mod ?? "") + "|" + (key ?? "");
    }

    /// <summary>
    /// Client side of the mod settings channel: the player's own values, remembered the first time the host's value
    /// replaces them, so the session end can put every one back.
    /// </summary>
    public sealed class ClientSettingsTracker
    {
        private readonly Dictionary<string, SettingEntry> _originals = new Dictionary<string, SettingEntry>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        /// <summary>Remembers the player's value of a setting unless it is already remembered; true when newly remembered.</summary>
        public bool RememberOriginal(string mod, string key, string currentValue)
        {
            string k = HostSettingsTracker.KeyOf(mod, key);
            if (_originals.ContainsKey(k)) return false;
            _originals[k] = new SettingEntry { Mod = mod, Key = key, Value = currentValue ?? "" };
            _order.Add(k);
            return true;
        }

        /// <summary>True when the setting was replaced by the host's value during this session.</summary>
        public bool IsOverridden(string mod, string key) => _originals.ContainsKey(HostSettingsTracker.KeyOf(mod, key));

        /// <summary>The remembered values in the order they were first replaced.</summary>
        public List<SettingEntry> Originals()
        {
            var list = new List<SettingEntry>(_order.Count);
            foreach (string k in _order) list.Add(_originals[k]);
            return list;
        }

        public int Count => _order.Count;

        public void Clear()
        {
            _originals.Clear();
            _order.Clear();
        }
    }
}
