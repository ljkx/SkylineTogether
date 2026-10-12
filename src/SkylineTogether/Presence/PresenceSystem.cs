using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Input;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using SkylineTogether.Core;
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
    /// Remote presence in the world (ARCH K15, §11.4). Each frame, while a session is active: raycasts the local
    /// cursor against terrain and water (its own <see cref="RaycastSystem"/> input, the pattern of the game's tooltip
    /// systems; the result is one frame old), sends the cursor, tool and prefab through
    /// <see cref="PresenceController"/> at up to 10 Hz, and draws every remote player's cursor as a projected ring in
    /// the player's colour with <see cref="OverlayRenderSystem.GetBuffer"/> (never ECS <c>Temp</c> entities, which
    /// belong to the local tool). Name tags are projected to the screen and handed to the UI. Runs in the Rendering
    /// phase right before <see cref="OverlayRenderSystem"/>.
    /// </summary>
    public sealed class PresenceSystem : GameSystemBase
    {
        private const float CursorLift = 0.5f;
        private const float SmoothingPerSecond = 14f;

        private readonly Dictionary<byte, float3> _shown = new Dictionary<byte, float3>();

        /// <summary>polish: this frame's tags; <see cref="PresenceController.SetTags"/> copies them only when they changed
        /// noticeably, so the UI is not rebuilt for every projected frame.</summary>
        private readonly List<PresenceTag> _tagScratch = new List<PresenceTag>();

        /// <summary>polish: the activity text per peer for its last tool and prefab (no string building per frame).</summary>
        private readonly Dictionary<byte, (byte Tool, int Prefab, string Text)> _activity = new Dictionary<byte, (byte, int, string)>();
        private readonly PresenceMessage _outgoing = new PresenceMessage { CurveControlPoints = new float[0], Footprints = new float[0] };
        private OverlayRenderSystem _overlay;
        private RaycastSystem _raycast;
        private CameraUpdateSystem _camera;
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
            _tools = World.GetOrCreateSystemManaged<ToolSystem>();
            _ranks = new PrefabRankIndex(Mod.Log);
        }

        protected override void OnUpdate()
        {
            PresenceController presence = Mod.Presence;
            if (presence == null) return;
            if (_subscribed != presence)
            {
                _subscribed = presence;
                presence.SessionChanged += _ranks.Invalidate;
                presence.SessionChanged += _activity.Clear; // polish: titles come from the rank index
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
            if (!presence.Throttle.ShouldSend(presence.Now, position.x, position.y, position.z, toolByte, rank, brush)) return;
            _outgoing.CursorX = position.x;
            _outgoing.CursorY = position.y;
            _outgoing.CursorZ = position.z;
            _outgoing.ToolKind = toolByte;
            _outgoing.Prefab = rank >= 0 ? new PrefabRank(rank) : PrefabRank.None;
            _outgoing.Color = PresenceController.ColorOf(presence.LocalPeer);
            _outgoing.BrushRadius = brush;
            presence.Send(_outgoing);
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

        private string CachedActivity(PresenceTable.Entry e)
        {
            int prefab = e.Prefab.IsNone ? -1 : e.Prefab.Value;
            byte tool = (byte)e.Tool;
            if (_activity.TryGetValue(e.Peer.Value, out var c) && c.Tool == tool && c.Prefab == prefab) return c.Text;
            string text = Activity(e);
            _activity[e.Peer.Value] = (tool, prefab, text);
            return text;
        }

        private string Activity(PresenceTable.Entry e)
        {
            string verb = PresenceToolKind.Verb(e.Tool);
            if (verb == null) return "";
            string title = e.Prefab.IsNone ? null : _ranks.TitleOf(World, e.Prefab.Value);
            return string.IsNullOrEmpty(title) ? verb : verb + " " + title;
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
