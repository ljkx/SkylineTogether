using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Input;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using SkylineTogether.Core;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace SkylineTogether.Presence
{
    /// <summary>
    /// Remote presence in the world (ARCHITECTURE.md#presence-and-tool-previews). Each frame, while a session is active: raycasts the local
    /// cursor against terrain and water (its own <see cref="RaycastSystem"/> input, the pattern of the game's tooltip
    /// systems; the result is one frame old), sends the cursor, tool and prefab through
    /// <see cref="PresenceController"/> at up to 10 Hz, and draws every remote player's cursor as a projected ring in
    /// the player's colour with <see cref="OverlayRenderSystem.GetBuffer"/> (never ECS <c>Temp</c> entities, which
    /// belong to the local tool). Name tags are projected to the screen and handed to the UI. Runs in the Rendering
    /// phase right before <see cref="OverlayRenderSystem"/>.
    /// <para>
    /// With every send it also captures the local tool's live preview (<see cref="ToolPreviewCapture"/>,
    /// read from the game's own <c>Temp</c> output, never written) and sends it as a whole (a keyframe) in the same
    /// message, so a change goes out at once and a lost message heals with the next one; the remote previews are eased
    /// between keyframes (<see cref="ToolPreviewSmoother"/>) and drawn as overlay outlines in the player's colour
    /// (<see cref="ToolPreviewRenderer"/>, elevated pieces at their real height), unless the player turned "Show other
    /// players' building previews" off. The net tool's elevation setting travels with the preview and is appended to the
    /// player's activity ("building Small Road (+10 m)").
    /// </para>
    /// </summary>
    public sealed class PresenceSystem : GameSystemBase
    {
        private const float CursorLift = 0.5f;
        private const float SmoothingPerSecond = 14f;

        private readonly Dictionary<byte, float3> _shown = new Dictionary<byte, float3>();

        /// <summary>This frame's tags; <see cref="PresenceController.SetTags"/> copies them only when they changed
        /// noticeably, so the UI is not rebuilt for every projected frame.</summary>
        private readonly List<PresenceTag> _tagScratch = new List<PresenceTag>();

        /// <summary>The activity text per peer for its last tool, prefab and tool elevation (no string building per frame).</summary>
        private readonly Dictionary<byte, (byte Tool, int Prefab, int Elevation, string Text)> _activity = new Dictionary<byte, (byte, int, int, string)>();
        private readonly PresenceMessage _outgoing = new PresenceMessage();

        /// <summary>The local tool preview, its encoded bytes (reused every send) and the reader of the game's tool output.</summary>
        private readonly ToolPreview _preview = new ToolPreview();
        private readonly ByteWriter _previewBytes = new ByteWriter(PresenceMessage.MaxPreviewBytes + 64);
        private ToolPreviewCapture _capture;

        /// <summary>A remote preview not refreshed for this long (lost keepalives) is no longer drawn.</summary>
        private const double PreviewStaleSeconds = 2.5;
        private OverlayRenderSystem _overlay;
        private RaycastSystem _raycast;
        private CameraUpdateSystem _camera;
        private TerrainSystem _terrain;
        private ToolSystem _tools;
        private PrefabRankIndex _ranks;
        private PresenceController _subscribed;
        private float _lastFrameTime;

        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _raycast = World.GetOrCreateSystemManaged<RaycastSystem>();
            _camera = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            _tools = World.GetOrCreateSystemManaged<ToolSystem>();
            _ranks = new PrefabRankIndex(Mod.Log);
            try
            {
                _capture = new ToolPreviewCapture(EntityManager);
            }
            catch (Exception ex)
            {
                _capture = null; // previews of the local tool stay off; remote ones are still drawn
                Diagnostics.Guard.Report(Diagnostics.Features.Previews, "creating the tool preview queries", ex);
            }
        }

        protected override void OnDestroy()
        {
            _capture?.Dispose();
            _capture = null;
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            PresenceController presence = Mod.Presence;
            if (presence == null) return;
            if (_subscribed != presence)
            {
                _subscribed = presence;
                presence.SessionChanged += _ranks.Invalidate;
                presence.SessionChanged += _activity.Clear; // titles come from the rank index
            }
            if (!presence.Active || !Diagnostics.Features.Presence.IsAvailable)
            {
                if (_shown.Count > 0 || presence.Tags.Count > 0)
                {
                    _shown.Clear();
                    _activity.Clear();
                    presence.SetTags(null);
                }
                return;
            }
            long start = Diagnostics.Perf.Start();
            try
            {
                Viewer viewer = null;
                Camera camera = _camera.TryGetViewer(out viewer) ? viewer.camera : null;
                SendLocal(presence, camera);
                DrawRemote(presence, camera);
            }
            catch (Exception ex)
            {
                // cosmetic: never let it break the frame
                Diagnostics.Guard.Report(Diagnostics.Features.Presence, "presence update", ex);
            }
            Diagnostics.Perf.Stop(Diagnostics.Perf.Presence, start);
        }

        // ---------------------------------------------------------------- local player

        private void SendLocal(PresenceController presence, Camera camera)
        {
            bool hit = false;
            float3 position = default;
            if (camera != null)
            {
                NativeArray<RaycastResult> results = _raycast.GetResult(this); // last frame's answer to our input
                if (results.Length > 0 && results[0].m_Owner != Entity.Null)
                {
                    hit = true;
                    position = results[0].m_Hit.m_HitPosition;
                }
                InputManager input = InputManager.instance;
                if (input != null && input.controlOverWorld)
                {
                    _raycast.AddInput(this, new RaycastInput
                    {
                        m_Line = ToolRaycastSystem.CalculateRaycastLine(camera),
                        m_TypeMask = TypeMask.Terrain | TypeMask.Water,
                        m_CollisionMask = CollisionMask.OnGround | CollisionMask.Overground,
                    });
                }
                else
                {
                    hit = false; // the cursor is over the UI
                }
            }
            if (!math.all(math.isfinite(position))) hit = false;

            ToolBaseSystem tool = _tools.activeTool;
            PresenceTool kind = ToolKindOf(tool);
            PrefabBase prefab = null;
            float brush = 0f;
            if (tool != null)
            {
                try
                {
                    prefab = tool.GetPrefab();
                    if (tool.brushing) brush = Math.Max(0f, tool.brushSize * 0.5f);
                }
                catch (Exception)
                {
                    prefab = null; // a tool between states
                }
            }
            int rank = kind == PresenceTool.Select || kind == PresenceTool.None ? -1 : _ranks.RankOf(World, prefab);
            byte toolByte = PresenceToolKind.Pack(kind, hit);
            if (!hit) position = default;
            if (!presence.Throttle.Due(presence.Now)) return; // the preview below is gathered at the send rate only
            int previewLength = CapturePreview(presence, kind, tool);
            ulong previewHash = previewLength > 0 ? XxHash64.Hash(_previewBytes.Buffer, 0, previewLength) | 1UL : 0UL;
            if (!presence.Throttle.ShouldSend(presence.Now, position.x, position.y, position.z, toolByte, rank, brush, previewHash)) return;
            _outgoing.Preview = _previewBytes.Buffer;
            _outgoing.PreviewLength = previewLength;
            presence.LastPreviewBytes = previewLength;
            _outgoing.CursorX = position.x;
            _outgoing.CursorY = position.y;
            _outgoing.CursorZ = position.z;
            _outgoing.ToolKind = toolByte;
            _outgoing.Prefab = rank >= 0 ? new PrefabRank(rank) : PrefabRank.None;
            _outgoing.Color = PresenceController.ColorOf(presence.LocalPeer);
            _outgoing.BrushRadius = brush;
            presence.Send(_outgoing);
        }

        /// <summary>
        /// Captures and encodes the local tool's preview into <see cref="_previewBytes"/>; returns its
        /// length (0: none — no building tool, a viewer, the feature off, or nothing in progress). Never throws.
        /// </summary>
        private int CapturePreview(PresenceController presence, PresenceTool kind, ToolBaseSystem tool)
        {
            _previewBytes.Reset();
            _preview.Clear();
            if (_capture == null || kind == PresenceTool.None || kind == PresenceTool.Select) return Captured(presence, 0);
            if (!Diagnostics.Features.Previews.IsAvailable || !presence.MaySendPreview) return Captured(presence, 0);
            try
            {
                _capture.Capture(_preview);
                if (tool is NetToolSystem net) _preview.Elevation = net.elevation; // the elevation steps (0 on the ground)
                return Captured(presence, ToolPreviewCodec.Encode(_preview, _previewBytes));
            }
            catch (Exception ex)
            {
                _previewBytes.Reset();
                _preview.Clear();
                Diagnostics.Guard.Report(Diagnostics.Features.Previews, "capturing the tool preview", ex);
                return Captured(presence, 0);
            }
        }

        private int Captured(PresenceController presence, int length)
        {
            presence.LastPreviewShapes = length > 0 ? _preview.Count : 0;
            presence.LastPreviewTruncated = length > 0 && (_preview.Truncated || ToolPreviewCodec.Truncated(_previewBytes.Buffer, 0, length));
            return length;
        }

        private static PresenceTool ToolKindOf(ToolBaseSystem tool)
        {
            if (tool == null) return PresenceTool.None;
            switch (tool.toolID)
            {
                case "Default Tool": return PresenceTool.Select;
                case "Net Tool": return PresenceTool.Net;
                case "Object Tool": return PresenceTool.Object;
                case "Zone Tool": return PresenceTool.Zone;
                case "Area Tool": return PresenceTool.Area;
                case "Terrain Tool": return PresenceTool.Terrain;
                case "Bulldoze Tool": return PresenceTool.Bulldoze;
                case "Route Tool": return PresenceTool.Route;
                case "Upgrade Tool": return PresenceTool.Upgrade;
                case "Water Tool": return PresenceTool.Water;
                case "Selection Tool": return PresenceTool.Selection;
                default: return PresenceTool.Other;
            }
        }

        // ---------------------------------------------------------------- remote players

        private void DrawRemote(PresenceController presence, Camera camera)
        {
            double now = presence.Now;
            presence.Table.Prune(now);
            float time = UnityEngine.Time.unscaledTime;
            float dt = Mathf.Clamp(time - _lastFrameTime, 0f, 0.25f);
            _lastFrameTime = time;
            float blend = 1f - Mathf.Exp(-SmoothingPerSecond * dt);

            IReadOnlyList<PresenceTable.Entry> entries = presence.Table.Entries;
            List<PresenceTag> tags = _tagScratch;
            tags.Clear();
            OverlayRenderSystem.Buffer buffer = default;
            bool haveBuffer = false;
            bool showPreviews = Diagnostics.Features.Previews.IsAvailable && (Mod.Settings == null || Mod.Settings.ShowBuildingPreviews);
            TerrainHeightData terrain = default;
            bool haveTerrain = false;
            float2 facing = default;
            if (camera != null)
            {
                Vector3 forward = camera.transform.forward;
                facing = new float2(forward.x, forward.z);
            }
            foreach (PresenceTable.Entry e in entries)
            {
                string name = presence.NameOf(e.Peer);
                uint rgba = e.Color != 0 ? e.Color : PresenceController.ColorOf(e.Peer);
                var tag = new PresenceTag
                {
                    Peer = e.Peer.Value,
                    Name = name,
                    Color = PresenceController.CssColor(rgba),
                    Activity = CachedActivity(e),
                };
                tags.Add(tag);
                if (showPreviews && now - e.ReceivedAt <= PreviewStaleSeconds)
                {
                    ToolPreviewSmoother.Step(e.ShownPreview, e.Preview, blend);
                    if (camera != null && !e.ShownPreview.IsEmpty)
                    {
                        if (!haveBuffer)
                        {
                            buffer = _overlay.GetBuffer(out JobHandle previewWriters);
                            previewWriters.Complete();
                            haveBuffer = true;
                        }
                        if (!haveTerrain)
                        {
                            terrain = _terrain.GetHeightData(); // the main-thread copy (only written on the main thread)
                            haveTerrain = true;
                        }
                        DrawPreview(buffer, e.ShownPreview, rgba, camera, facing, ref terrain);
                    }
                }
                else if (!e.ShownPreview.IsEmpty)
                {
                    e.ShownPreview.Clear();
                }
                if (!e.CursorVisible)
                {
                    _shown.Remove(e.Peer.Value);
                    continue;
                }
                var target = new float3(e.X, e.Y, e.Z);
                float3 shown = _shown.TryGetValue(e.Peer.Value, out float3 previous) && math.distance(previous, target) < 2000f
                    ? math.lerp(previous, target, blend)
                    : target;
                _shown[e.Peer.Value] = shown;
                if (camera == null) continue;

                if (!haveBuffer)
                {
                    buffer = _overlay.GetBuffer(out JobHandle writers);
                    writers.Complete(); // we write on the main thread; earlier writer jobs must be done
                    haveBuffer = true;
                }
                float distance = math.distance((float3)camera.transform.position, shown);
                float diameter = math.clamp(distance * 0.025f, 3f, 160f);
                Color color = ToColor(rgba, 1f);
                Color fill = ToColor(rgba, 0.28f);
                var up = new float2(0f, 1f);
                buffer.DrawCircle(color, fill, diameter * 0.12f, OverlayRenderSystem.StyleFlags.Projected, up, shown, diameter);
                buffer.DrawCircle(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, up, shown, diameter * 0.2f);
                if (e.BrushRadius > 0.5f)
                {
                    buffer.DrawCircle(color, ToColor(rgba, 0.06f), Math.Max(0.5f, diameter * 0.06f), OverlayRenderSystem.StyleFlags.Projected, up, shown, e.BrushRadius * 2f);
                }
                Vector3 screen = camera.WorldToScreenPoint(new Vector3(shown.x, shown.y + CursorLift, shown.z));
                if (screen.z > 0f && camera.pixelWidth > 0 && camera.pixelHeight > 0)
                {
                    tag.ScreenX = Mathf.Clamp01(screen.x / camera.pixelWidth);
                    tag.ScreenY = Mathf.Clamp01(1f - screen.y / camera.pixelHeight);
                    tag.OnScreen = screen.x >= 0f && screen.x <= camera.pixelWidth && screen.y >= 0f && screen.y <= camera.pixelHeight;
                }
            }
            if (_shown.Count > entries.Count) PruneShown(entries);
            presence.SetTags(tags);
        }

        /// <summary>One remote preview; a failure turns the previews off, never the cursors.</summary>
        private static void DrawPreview(OverlayRenderSystem.Buffer buffer, ToolPreview preview, uint rgba, Camera camera, float2 facing, ref TerrainHeightData terrain)
        {
            try
            {
                PreviewPoint anchor = preview.PointAt(0, 0);
                float distance = math.distance((float3)camera.transform.position, new float3(anchor.X, anchor.Y, anchor.Z));
                ToolPreviewRenderer.Draw(buffer, preview, ToColor(rgba, 1f), distance, facing, ref terrain);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Previews, "drawing a building preview", ex);
            }
        }

        private string CachedActivity(PresenceTable.Entry e)
        {
            int prefab = e.Prefab.IsNone ? -1 : e.Prefab.Value;
            byte tool = (byte)e.Tool;
            int elevation = PresenceToolKind.ElevationKey(e.Preview.Elevation);
            if (_activity.TryGetValue(e.Peer.Value, out var c) && c.Tool == tool && c.Prefab == prefab && c.Elevation == elevation) return c.Text;
            string text = Activity(e);
            _activity[e.Peer.Value] = (tool, prefab, elevation, text);
            return text;
        }

        private string Activity(PresenceTable.Entry e)
        {
            string verb = PresenceToolKind.Verb(e.Tool);
            if (verb == null) return "";
            string title = e.Prefab.IsNone ? null : _ranks.TitleOf(World, e.Prefab.Value);
            string text = string.IsNullOrEmpty(title) ? verb : verb + " " + title;
            string elevation = PresenceToolKind.ElevationLabel(e.Preview.Elevation);
            return elevation == null ? text : text + " (" + elevation + ")";
        }

        private void PruneShown(IReadOnlyList<PresenceTable.Entry> entries)
        {
            var keep = new HashSet<byte>();
            foreach (PresenceTable.Entry e in entries) keep.Add(e.Peer.Value);
            var gone = new List<byte>();
            foreach (byte peer in _shown.Keys)
            {
                if (!keep.Contains(peer)) gone.Add(peer);
            }
            foreach (byte peer in gone) _shown.Remove(peer);
        }

        private static Color ToColor(uint rgba, float alpha)
        {
            return new Color(((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, alpha);
        }
    }
}
