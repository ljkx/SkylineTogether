using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkylineTogether.Core.Json;

namespace SkylineTogether.Core.Replay
{
    /// <summary>How an entity differs at a checkpoint.</summary>
    public enum DivergenceKind : byte
    {
        /// <summary>Both sides have it with different integrity hashes.</summary>
        Differs = 1,
        /// <summary>The host has it, the replaying client does not.</summary>
        MissingHere = 2,
        /// <summary>The replaying client has it, the host does not.</summary>
        ExtraHere = 3,
    }

    /// <summary>One entity that differed at one or more checkpoints.</summary>
    public sealed class EntityDivergence
    {
        public uint NetId;
        public string Family = "";
        public DivergenceKind Kind;
        /// <summary>The differing components (from the recorded members), or why they are unknown.</summary>
        public string Components = "";
        public uint FirstCheckpoint;
        public uint LastCheckpoint;
        public int Occurrences;
    }

    /// <summary>The result of one checkpoint.</summary>
    public sealed class CheckpointResult
    {
        public uint Index;
        public uint HostFrame;
        public double HostSeconds;
        /// <summary>Real seconds since the replaying client went live when the checkpoint was compared.</summary>
        public double ReplaySeconds;
        public int Buckets;
        public long Entities;
        public int Differs;
        public int Missing;
        public int Extra;
        /// <summary>The end record arrived (every bucket of the checkpoint was compared).</summary>
        public bool Complete;
        public int Lines = -1;
        public List<string> LineDifferences = new List<string>();
        /// <summary>Null: not compared (no icon data, or the icons never settled).</summary>
        public bool? IconsMatch;
        public List<string> IconDifferences = new List<string>();
        public int GlobalsCompared;
        public List<string> GlobalDifferences = new List<string>();
        public int StrictGlobalDifferences;

        public int EntityDifferences => Differs + Missing + Extra;
    }

    /// <summary>
    /// The verdict of a replay (solo testing, ARCH §13.5): every checkpoint's comparison (entity digests per family,
    /// UI-level line rows, icons, global state), the entities that differed with the components that differ, apply
    /// errors, and timings. "Passed" needs a complete stream, at least one complete checkpoint, no apply error, and no
    /// difference at the last complete checkpoint (earlier ones that went away are listed as transient). Pure; the game
    /// layer feeds it and writes <see cref="ToText"/> and <see cref="ToJson"/> next to the recording.
    /// </summary>
    public sealed class ReplayVerdict
    {
        public const int MaxEntities = 2000;
        public const int MaxErrors = 200;
        public const int MaxLinesPerList = 100;

        private readonly Dictionary<uint, EntityDivergence> _entities = new Dictionary<uint, EntityDivergence>();
        private readonly List<CheckpointResult> _checkpoints = new List<CheckpointResult>();
        private readonly List<string> _errors = new List<string>();
        private readonly List<string> _notes = new List<string>();
        private int _droppedEntities;
        private int _droppedErrors;

        public ReplayVerdict(RecordingHeader header, string recordingName)
        {
            Header = header ?? new RecordingHeader();
            RecordingName = recordingName ?? "";
        }

        public RecordingHeader Header { get; }

        public string RecordingName { get; }

        /// <summary>Every recorded message was played (the recording's end marker was reached).</summary>
        public bool StreamCompleted;

        /// <summary>Why the replay ended (the recording's end, or what stopped it).</summary>
        public string EndReason = "";

        /// <summary>Recorded seconds the replay covered.</summary>
        public double RecordedSeconds;

        /// <summary>Real seconds from the start of the replay to its verdict.</summary>
        public double WallSeconds;

        /// <summary>Real seconds from the start until the client's city was loaded (download plus load).</summary>
        public double LoadSeconds = -1;

        public double Speed = 1;
        public long MessagesDelivered;
        public long BytesDelivered;

        /// <summary>What the replaying client sent (it went nowhere), per message kind.</summary>
        public readonly Dictionary<string, long> ClientSent = new Dictionary<string, long>();

        /// <summary>Feature status lines at the end (what the panel showed).</summary>
        public string FinalStatus = "";

        public IReadOnlyList<CheckpointResult> Checkpoints => _checkpoints;

        public IReadOnlyList<string> Errors => _errors;

        public IReadOnlyList<string> Notes => _notes;

        public IReadOnlyCollection<EntityDivergence> Entities => _entities.Values;

        /// <summary>The last checkpoint whose every bucket was compared, or null.</summary>
        public CheckpointResult LastComplete
        {
            get
            {
                for (int i = _checkpoints.Count - 1; i >= 0; i--)
                {
                    if (_checkpoints[i].Complete) return _checkpoints[i];
                }
                return null;
            }
        }

        /// <summary>Entities that still differed at the last complete checkpoint (or in the incomplete one after it).</summary>
        public List<EntityDivergence> Persistent
        {
            get
            {
                var list = new List<EntityDivergence>();
                CheckpointResult last = LastComplete;
                foreach (EntityDivergence d in _entities.Values)
                {
                    if (last == null || d.LastCheckpoint >= last.Index) list.Add(d);
                }
                list.Sort((a, b) => a.NetId.CompareTo(b.NetId));
                return list;
            }
        }

        /// <summary>Entities that differed at some checkpoint and matched again later.</summary>
        public List<EntityDivergence> Transient
        {
            get
            {
                var list = new List<EntityDivergence>();
                CheckpointResult last = LastComplete;
                if (last == null) return list;
                foreach (EntityDivergence d in _entities.Values)
                {
                    if (d.LastCheckpoint < last.Index) list.Add(d);
                }
                list.Sort((a, b) => a.NetId.CompareTo(b.NetId));
                return list;
            }
        }

        public bool Passed
        {
            get
            {
                CheckpointResult last = LastComplete;
                if (!StreamCompleted || last == null || _errors.Count > 0 || _droppedErrors > 0) return false;
                if (Persistent.Count > 0) return false;
                if (last.LineDifferences.Count > 0) return false;
                if (last.IconsMatch == false) return false;
                return last.StrictGlobalDifferences == 0;
            }
        }

        /// <summary>"passed", "failed", or "incomplete" (nothing could be compared).</summary>
        public string Status => Passed ? "passed" : LastComplete == null && _errors.Count == 0 && _entities.Count == 0 ? "incomplete" : "failed";

        // ------------------------------------------------------------------------------------------------ feeding

        /// <summary>The checkpoint with this index (created on first use).</summary>
        public CheckpointResult Checkpoint(uint index)
        {
            for (int i = _checkpoints.Count - 1; i >= 0; i--)
            {
                if (_checkpoints[i].Index == index) return _checkpoints[i];
            }
            var c = new CheckpointResult { Index = index };
            _checkpoints.Add(c);
            return c;
        }

        /// <summary>One bucket of a checkpoint was compared (<paramref name="entities"/> on the host).</summary>
        public void OnBucketCompared(uint checkpoint, int entities)
        {
            CheckpointResult c = Checkpoint(checkpoint);
            c.Buckets++;
            c.Entities += entities;
        }

        /// <summary>An entity differed at a checkpoint.</summary>
        public void OnEntity(uint checkpoint, uint netId, DivergenceKind kind, string family, string components)
        {
            CheckpointResult c = Checkpoint(checkpoint);
            switch (kind)
            {
                case DivergenceKind.MissingHere: c.Missing++; break;
                case DivergenceKind.ExtraHere: c.Extra++; break;
                default: c.Differs++; break;
            }
            if (!_entities.TryGetValue(netId, out EntityDivergence d))
            {
                if (_entities.Count >= MaxEntities)
                {
                    _droppedEntities++;
                    return;
                }
                d = new EntityDivergence { NetId = netId, FirstCheckpoint = checkpoint };
                _entities.Add(netId, d);
            }
            d.Kind = kind;
            if (!string.IsNullOrEmpty(family)) d.Family = family;
            if (!string.IsNullOrEmpty(components)) d.Components = components;
            d.LastCheckpoint = checkpoint;
            d.Occurrences++;
        }

        /// <summary>The checkpoint's end arrived: every bucket was compared; the summary fields come with it.</summary>
        public void OnCheckpointEnd(uint checkpoint, uint hostFrame, double hostSeconds, double replaySeconds)
        {
            CheckpointResult c = Checkpoint(checkpoint);
            c.Complete = true;
            c.HostFrame = hostFrame;
            c.HostSeconds = hostSeconds;
            c.ReplaySeconds = replaySeconds;
        }

        public void OnLines(uint checkpoint, int lines, IEnumerable<string> differences)
        {
            CheckpointResult c = Checkpoint(checkpoint);
            c.Lines = lines;
            c.LineDifferences.Clear();
            foreach (string d in differences)
            {
                if (c.LineDifferences.Count >= MaxLinesPerList) break;
                c.LineDifferences.Add(d);
            }
        }

        public void OnIcons(uint checkpoint, bool match, IEnumerable<string> differences)
        {
            CheckpointResult c = Checkpoint(checkpoint);
            c.IconsMatch = match;
            c.IconDifferences.Clear();
            if (differences == null) return;
            foreach (string d in differences)
            {
                if (c.IconDifferences.Count >= MaxLinesPerList) break;
                c.IconDifferences.Add(d);
            }
        }

        public void OnGlobals(uint checkpoint, int compared, IEnumerable<(string Name, bool Strict, string Text)> differences)
        {
            CheckpointResult c = Checkpoint(checkpoint);
            c.GlobalsCompared = compared;
            c.GlobalDifferences.Clear();
            c.StrictGlobalDifferences = 0;
            foreach ((string _, bool strict, string text) in differences)
            {
                if (strict) c.StrictGlobalDifferences++;
                c.GlobalDifferences.Add((strict ? "" : "(warning) ") + text);
            }
        }

        /// <summary>An exception while applying what the host sent (or another error during the replay).</summary>
        public void AddError(string text)
        {
            if (_errors.Count >= MaxErrors)
            {
                _droppedErrors++;
                return;
            }
            _errors.Add(text ?? "");
        }

        public void AddNote(string text)
        {
            if (_notes.Count < MaxErrors) _notes.Add(text ?? "");
        }

        // ------------------------------------------------------------------------------------------------ output

        /// <summary>One line for the panel.</summary>
        public string Summary
        {
            get
            {
                CheckpointResult last = LastComplete;
                int complete = 0;
                foreach (CheckpointResult c in _checkpoints) if (c.Complete) complete++;
                if (Passed)
                {
                    return "Passed: " + complete + (complete == 1 ? " checkpoint, " : " checkpoints, ") + ReplayDiff.N(last.Entities) + " objects"
                           + (last.Lines >= 0 ? ", " + last.Lines + " lines" : "") + (last.IconsMatch == true ? ", icons" : "")
                           + (last.GlobalsCompared > 0 ? ", global state" : "") + " equal to the host" + (Transient.Count > 0 ? " (" + Transient.Count + " objects differed for a while)" : "");
                }
                var parts = new List<string>();
                List<EntityDivergence> persistent = Persistent;
                if (persistent.Count > 0) parts.Add(persistent.Count + (persistent.Count == 1 ? " object differs" : " objects differ") + " (" + FamilyBreakdown(persistent) + ")");
                if (last != null && last.LineDifferences.Count > 0) parts.Add(last.LineDifferences.Count + " line difference(s)");
                if (last != null && last.IconsMatch == false) parts.Add("icons differ");
                if (last != null && last.StrictGlobalDifferences > 0) parts.Add(last.StrictGlobalDifferences + " global channel(s) differ");
                if (_errors.Count > 0) parts.Add(_errors.Count + (_errors.Count == 1 ? " apply error" : " apply errors"));
                if (last == null) parts.Add("no checkpoint was completed");
                if (!StreamCompleted) parts.Add("the replay did not reach the end (" + EndReason + ")");
                return (Status == "incomplete" ? "Incomplete: " : "Failed: ") + string.Join("; ", parts);
            }
        }

        private static string FamilyBreakdown(List<EntityDivergence> list)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (EntityDivergence d in list)
            {
                string f = string.IsNullOrEmpty(d.Family) ? "?" : d.Family;
                counts.TryGetValue(f, out int n);
                counts[f] = n + 1;
            }
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> p in counts) parts.Add(p.Key + ": " + p.Value);
            return string.Join(", ", parts);
        }

        public string ToText()
        {
            var sb = new StringBuilder(8192);
            string nl = "\r\n";
            sb.Append("Skyline Together replay verdict: ").Append(Status.ToUpperInvariant()).Append(nl);
            sb.Append(Summary).Append(nl).Append(nl);
            sb.Append("Recording: ").Append(RecordingName).Append(nl);
            sb.Append("City: ").Append(Header.CityName).Append(", session \"").Append(Header.SessionName).Append("\" hosted by ").Append(Header.HostName).Append(nl);
            if (Header.CreatedUtcTicks > 0) sb.Append("Recorded: ").Append(new DateTime(Header.CreatedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(nl);
            sb.Append("Game ").Append(Header.GameVersion).Append(", mod build ").Append(Header.ModBuild.ToString("X16", CultureInfo.InvariantCulture)).Append(", protocol ").Append(Header.ProtocolVersion).Append(nl);
            sb.Append("Replay: ").Append(ReplayDiff.Seconds(RecordedSeconds)).Append(" of recording at ").Append(Speed > 0 ? Speed.ToString("0.##", CultureInfo.InvariantCulture) + "x" : "full speed")
              .Append(" in ").Append(ReplayDiff.Seconds(WallSeconds)).Append(" (download and load ").Append(ReplayDiff.Seconds(LoadSeconds)).Append("), ")
              .Append(ReplayDiff.N(MessagesDelivered)).Append(" messages, ").Append(ReplayDiff.N(BytesDelivered / 1024)).Append(" KB").Append(nl);
            sb.Append("End: ").Append(StreamCompleted ? "the whole recording was played" : "stopped early").Append(" (").Append(EndReason).Append(")").Append(nl);
            if (ClientSent.Count > 0)
            {
                var sent = new List<string>();
                foreach (KeyValuePair<string, long> p in ClientSent) sent.Add(p.Key + " " + p.Value);
                sent.Sort(StringComparer.Ordinal);
                sb.Append("The replaying client sent (to nowhere): ").Append(string.Join(", ", sent)).Append(nl);
            }
            sb.Append(nl).Append("Checkpoints").Append(nl);
            foreach (CheckpointResult c in _checkpoints)
            {
                sb.Append("  #").Append(c.Index).Append(c.Complete ? "" : " (incomplete)").Append(" host frame ").Append(c.HostFrame).Append(" at ").Append(ReplayDiff.Seconds(c.HostSeconds))
                  .Append(": ").Append(ReplayDiff.N(c.Entities)).Append(" objects in ").Append(c.Buckets).Append(" buckets, ")
                  .Append(c.EntityDifferences == 0 ? "all equal" : c.Differs + " differ, " + c.Missing + " missing here, " + c.Extra + " extra here");
                if (c.Lines >= 0) sb.Append("; lines: ").Append(c.LineDifferences.Count == 0 ? c.Lines + " equal" : c.LineDifferences.Count + " difference(s)");
                if (c.IconsMatch.HasValue) sb.Append("; icons ").Append(c.IconsMatch.Value ? "equal" : "differ");
                if (c.GlobalsCompared > 0) sb.Append("; global state: ").Append(c.GlobalDifferences.Count == 0 ? c.GlobalsCompared + " channels equal" : c.GlobalDifferences.Count + " channel(s) differ");
                sb.Append(nl);
                foreach (string d in c.LineDifferences) sb.Append("      line ").Append(d).Append(nl);
                foreach (string d in c.IconDifferences) sb.Append("      icons ").Append(d).Append(nl);
                foreach (string d in c.GlobalDifferences) sb.Append("      global ").Append(d).Append(nl);
            }
            if (_checkpoints.Count == 0) sb.Append("  (none)").Append(nl);
            List<EntityDivergence> persistent = Persistent;
            sb.Append(nl).Append("Objects that differ at the end: ").Append(persistent.Count).Append(nl);
            AppendEntities(sb, persistent, nl);
            List<EntityDivergence> transient = Transient;
            if (transient.Count > 0)
            {
                sb.Append(nl).Append("Objects that differed for a while and matched again: ").Append(transient.Count).Append(nl);
                AppendEntities(sb, transient, nl);
            }
            if (_droppedEntities > 0) sb.Append("  (").Append(_droppedEntities).Append(" more objects not listed)").Append(nl);
            sb.Append(nl).Append("Apply errors: ").Append(_errors.Count + _droppedErrors).Append(nl);
            foreach (string e in _errors) sb.Append("  ").Append(e).Append(nl);
            if (_notes.Count > 0)
            {
                sb.Append(nl).Append("Notes").Append(nl);
                foreach (string n in _notes) sb.Append("  ").Append(n).Append(nl);
            }
            if (!string.IsNullOrEmpty(FinalStatus)) sb.Append(nl).Append("Status at the end: ").Append(FinalStatus).Append(nl);
            return sb.ToString();
        }

        private static void AppendEntities(StringBuilder sb, List<EntityDivergence> list, string nl)
        {
            foreach (EntityDivergence d in list)
            {
                sb.Append("  #").Append(d.NetId).Append(" (").Append(string.IsNullOrEmpty(d.Family) ? "?" : d.Family).Append(") ").Append(KindText(d.Kind));
                if (!string.IsNullOrEmpty(d.Components)) sb.Append(": ").Append(d.Components);
                sb.Append(" [checkpoints ").Append(d.FirstCheckpoint);
                if (d.LastCheckpoint != d.FirstCheckpoint) sb.Append("-").Append(d.LastCheckpoint);
                sb.Append("]").Append(nl);
            }
        }

        private static string KindText(DivergenceKind k)
        {
            switch (k)
            {
                case DivergenceKind.MissingHere: return "missing in the replay";
                case DivergenceKind.ExtraHere: return "only in the replay (not on the host)";
                default: return "differs";
            }
        }

        public string ToJson()
        {
            var text = new StringWriter(CultureInfo.InvariantCulture);
            var w = new JsonWriter(text, true);
            w.BeginObject();
            w.Name("status"); w.Value(Status);
            w.Name("summary"); w.Value(Summary);
            w.Name("recording"); w.Value(RecordingName);
            w.Name("city"); w.Value(Header.CityName ?? "");
            w.Name("session"); w.Value(Header.SessionName ?? "");
            w.Name("host"); w.Value(Header.HostName ?? "");
            w.Name("gameVersion"); w.Value(Header.GameVersion ?? "");
            w.Name("protocol"); w.Value(Header.ProtocolVersion);
            w.Name("streamCompleted"); w.Value(StreamCompleted);
            w.Name("endReason"); w.Value(EndReason ?? "");
            w.Name("recordedSeconds"); w.Value(Round(RecordedSeconds));
            w.Name("wallSeconds"); w.Value(Round(WallSeconds));
            w.Name("loadSeconds"); w.Value(Round(LoadSeconds));
            w.Name("speed"); w.Value(Speed);
            w.Name("messages"); w.Value(MessagesDelivered);
            w.Name("bytes"); w.Value(BytesDelivered);
            w.Name("clientSent");
            w.BeginObject();
            foreach (KeyValuePair<string, long> p in ClientSent)
            {
                w.Name(p.Key);
                w.Value(p.Value);
            }
            w.EndObject();
            w.Name("checkpoints");
            w.BeginArray();
            foreach (CheckpointResult c in _checkpoints)
            {
                w.BeginObject();
                w.Name("index"); w.Value((long)c.Index);
                w.Name("complete"); w.Value(c.Complete);
                w.Name("hostFrame"); w.Value((long)c.HostFrame);
                w.Name("hostSeconds"); w.Value(Round(c.HostSeconds));
                w.Name("buckets"); w.Value(c.Buckets);
                w.Name("entities"); w.Value(c.Entities);
                w.Name("differ"); w.Value(c.Differs);
                w.Name("missing"); w.Value(c.Missing);
                w.Name("extra"); w.Value(c.Extra);
                w.Name("lines"); w.Value(c.Lines);
                Strings(w, "lineDifferences", c.LineDifferences);
                w.Name("iconsMatch");
                if (c.IconsMatch.HasValue) w.Value(c.IconsMatch.Value);
                else w.Null();
                Strings(w, "iconDifferences", c.IconDifferences);
                w.Name("globalsCompared"); w.Value(c.GlobalsCompared);
                Strings(w, "globalDifferences", c.GlobalDifferences);
                w.EndObject();
            }
            w.EndArray();
            w.Name("entities");
            w.BeginArray();
            var all = new List<EntityDivergence>(_entities.Values);
            all.Sort((a, b) => a.NetId.CompareTo(b.NetId));
            CheckpointResult last = LastComplete;
            foreach (EntityDivergence d in all)
            {
                w.BeginObject();
                w.Name("id"); w.Value((long)d.NetId);
                w.Name("family"); w.Value(d.Family ?? "");
                w.Name("kind"); w.Value(d.Kind.ToString());
                w.Name("components"); w.Value(d.Components ?? "");
                w.Name("first"); w.Value((long)d.FirstCheckpoint);
                w.Name("last"); w.Value((long)d.LastCheckpoint);
                w.Name("persistent"); w.Value(last == null || d.LastCheckpoint >= last.Index);
                w.EndObject();
            }
            w.EndArray();
            Strings(w, "errors", _errors);
            Strings(w, "notes", _notes);
            w.Name("finalStatus"); w.Value(FinalStatus ?? "");
            w.EndObject();
            return text.ToString();
        }

        private static double Round(double v) => double.IsNaN(v) || double.IsInfinity(v) ? -1 : Math.Round(v, 3);

        private static void Strings(JsonWriter w, string name, IEnumerable<string> values)
        {
            w.Name(name);
            w.BeginArray();
            foreach (string s in values) w.Value(s ?? "");
            w.EndArray();
        }
    }
}
