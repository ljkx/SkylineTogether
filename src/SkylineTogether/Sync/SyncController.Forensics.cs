using System;
using Game.Tools;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Diagnostics;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    // Crash forensics of the client's own edits (ARCHITECTURE.md#logs).
    public sealed partial class SyncController
    {
        private readonly ToolApplyTally _tally = new ToolApplyTally();
        private readonly FrameMarkerClock _markers = new FrameMarkerClock();
        private int _appliesSinceMarker;

        /// <summary>Counts one entity of a frame in which the local tool applied (the tool's own change or the game's derivation).</summary>
        private void TallyToolChange(EntityManager em, Entity e, bool deleted)
        {
            try
            {
                bool byTool = em.HasComponent<Game.Common.Applied>(e) || _toolOriginals.Contains(e);
                ToolApplyTally.Change change = deleted ? ToolApplyTally.Change.Deleted
                    : em.HasComponent<Game.Common.Created>(e) ? ToolApplyTally.Change.Created : ToolApplyTally.Change.Updated;
                Family family = _codec.Families.Classify(em, e);
                _tally.Add(family.ToString(), change, byTool);
            }
            catch (Exception)
            {
                // a breadcrumb never breaks the capture
            }
        }

        /// <summary>
        /// Client: <c>tool apply: ...</c> after the capture of a frame in which the player's tool applied (vanilla tools) or a
        /// direct-write mod tool proposed something; <paramref name="proposal"/> is the proposal's sequence number, 0 when
        /// nothing was sent.
        /// </summary>
        private void WriteToolApply(long proposal)
        {
            if (_tally.IsEmpty && proposal == 0) return;
            _appliesSinceMarker++;
            string tool = null, prefab = null;
            try
            {
                ToolBaseSystem active = _tools?.activeTool;
                if (active != null)
                {
                    tool = active.toolID ?? active.GetType().Name;
                    prefab = active.GetPrefab()?.name;
                }
            }
            catch (Exception)
            {
                // the line names what it can
            }
            Forensics.Write(_tally.Format(tool, prefab, proposal));
            _tally.Clear();
        }

        /// <summary>
        /// Client, at the end of every frame: a <c>frame marker</c> line every few seconds while a tool other than the default
        /// one is selected, and when it is put away (a native crash is then bracketed in time by the last marker).
        /// </summary>
        private void WriteFrameMarker(double now)
        {
            bool building = _tools != null && _tools.activeTool != null && !(_tools.activeTool is DefaultToolSystem);
            if (!_markers.Due(now, building, out bool ended)) return;
            string tool = null;
            try
            {
                tool = _tools?.activeTool?.toolID;
            }
            catch (Exception)
            {
                // named when possible
            }
            Forensics.FlushSummary();
            Forensics.Write("frame marker: frame " + UnityEngine.Time.frameCount + ", " + (ended ? "tool put away" : "tool " + (tool ?? "?") + " selected") + "; "
                            + _appliesSinceMarker + " tool apply(s) and " + Forensics.CleanupsSinceMarker + " clean-up(s) since the last marker; "
                            + _inbox.Count + " host batch(es) waiting");
            _appliesSinceMarker = 0;
            Forensics.CleanupsSinceMarker = 0;
        }
    }
}
