using System;
using System.Globalization;
using System.Text;
using Colossal.UI.Binding;
using Game.UI;
using SkylineTogether.Diagnostics;
using SkylineTogether.Replay;
using SkylineTogether.Session;

namespace SkylineTogether.UI
{
    /// <summary>
    /// Solo testing bindings (group <c>skylineTogether</c>, ARCHITECTURE.md#solo-test-record-and-replay): <c>replay</c> with the recording and replay
    /// state, the recordings and the last verdict, refreshed at most twice a second; triggers to record, stop, replay
    /// and refresh the list. tools/ui_smoke.mjs checks these names like the panel's own.
    /// </summary>
    public sealed class ReplayUISystem : UISystemBase
    {
        private const string Group = SessionUISystem.Group;

        private string _json = "{}";
        private double _at = double.NegativeInfinity;
        private int _version = -1;
        private double _retry;
        private Func<string> _build;

        protected override void OnCreate()
        {
            base.OnCreate();
            _build = Build;
            AddUpdateBinding(new GetterValueBinding<string>(Group, "replay", () => Guard.GetRetrying(Features.Ui, "solo testing state", _build, _json, ref _retry)));
            AddBinding(new TriggerBinding(Group, "recordStart", () => Trigger("recordStart", () => Mod.Replay?.StartRecording())));
            AddBinding(new TriggerBinding(Group, "recordStop", () => Trigger("recordStop", () => Mod.Replay?.StopRecording())));
            AddBinding(new TriggerBinding<string, int>(Group, "replayStart", (id, speed) => Trigger("replayStart", () => Mod.Replay?.StartReplay(id, Math.Max(0, Math.Min(8, speed))))));
            AddBinding(new TriggerBinding(Group, "replayStop", () => Trigger("replayStop", () => Mod.Replay?.StopReplay())));
            AddBinding(new TriggerBinding(Group, "replayRefresh", () => Trigger("replayRefresh", () => Mod.Replay?.RefreshRecordings())));
        }

        private static void Trigger(string what, Action action) => Guard.RunAlways(Features.Ui, "panel action " + what, action);

        /// <summary>
        /// <c>{canRecord, recording, stopping, solo, recordStatus, canReplay, replaying, replayStatus, progress, root,
        /// recordings: [{id, city, when, duration, mb, verdict}], verdict: {status, summary, folder} | null}</c>.
        /// </summary>
        private string Build()
        {
            ReplayController r = Mod.Replay;
            SessionManager s = Mod.Sessions;
            if (r == null || s == null) return "{}";
            double now = s.Clock.Seconds;
            if (r.Version == _version && now - _at < 0.5) return _json;
            _version = r.Version;
            _at = now;
            var sb = new StringBuilder(1024);
            sb.Append('{');
            Prop(sb, "canRecord", r.CanRecord).Append(',');
            Prop(sb, "recording", r.Recording).Append(',');
            Prop(sb, "stopping", r.RecordingStopping).Append(',');
            Prop(sb, "solo", s.SoloHost).Append(',');
            Prop(sb, "recordStatus", r.RecordStatus).Append(',');
            Prop(sb, "canReplay", r.CanReplay).Append(',');
            Prop(sb, "replaying", r.Replaying).Append(',');
            Prop(sb, "replayStatus", r.ReplayStatus).Append(',');
            Str(sb, "progress").Append(':').Append(r.ReplayProgress.ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            Prop(sb, "root", ReplayController.Root).Append(',');
            sb.Append("\"recordings\":[");
            bool first = true;
            foreach (RecordingInfo info in r.Recordings)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                Prop(sb, "id", info.Id).Append(',');
                Prop(sb, "city", info.City).Append(',');
                Prop(sb, "when", info.Created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(',');
                Prop(sb, "duration", info.Seconds >= 0 ? ReplayController.Duration(info.Seconds) : "").Append(',');
                Prop(sb, "mb", ReplayController.Mb(info.Bytes)).Append(',');
                Prop(sb, "verdict", info.Verdict ?? "");
                sb.Append('}');
            }
            sb.Append("],\"verdict\":");
            Core.Replay.ReplayVerdict v = r.LastVerdict;
            if (v == null) sb.Append("null");
            else
            {
                sb.Append('{');
                Prop(sb, "status", v.Status).Append(',');
                Prop(sb, "summary", v.Summary).Append(',');
                Prop(sb, "folder", r.LastVerdictFolder);
                sb.Append('}');
            }
            sb.Append('}');
            _json = sb.ToString();
            return _json;
        }

        private static StringBuilder Prop(StringBuilder sb, string name, string value)
        {
            Str(sb, name);
            sb.Append(':');
            return Str(sb, value);
        }

        private static StringBuilder Prop(StringBuilder sb, string name, bool value)
        {
            Str(sb, name);
            return sb.Append(':').Append(value ? "true" : "false");
        }

        private static StringBuilder Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"');
        }
    }
}
