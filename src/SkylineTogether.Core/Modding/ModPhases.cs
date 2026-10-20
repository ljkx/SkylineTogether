using System;

namespace SkylineTogether.Core.Modding
{
    /// <summary>What a system update phase means for multiplayer (<see cref="ModPhases.KindOf"/>).</summary>
    public enum PhaseKind : byte
    {
        /// <summary>Not a phase of the game's list (or unreadable): treated like <see cref="Other"/>.</summary>
        Unknown = 0,
        /// <summary><c>GameSimulation</c>: every system of it is off on a following client (follower clock).</summary>
        Simulation = 1,
        /// <summary><c>PreSimulation</c>, <c>PostSimulation</c>, <c>LoadSimulation</c>: run with the simulation step.</summary>
        SimulationStep = 2,
        /// <summary><c>Modification1</c> ... <c>ModificationEnd</c>: derivation from changed entities, runs on every peer.</summary>
        Modification = 3,
        /// <summary>Tool phases (<c>PreTool</c>, <c>ToolUpdate</c>, <c>ApplyTool</c>, ...).</summary>
        Tool = 4,
        /// <summary>UI, tooltips, rendering, culling, raycasts, gizmos: never shared state.</summary>
        Presentation = 5,
        /// <summary><c>Serialize</c>, <c>Deserialize</c>: save and load.</summary>
        Serialization = 6,
        /// <summary><c>PrefabUpdate</c>, <c>PrefabReferences</c>: prefab data (the rules everyone plays by).</summary>
        Prefab = 7,
        /// <summary><c>EditorSimulation</c>: the editor only.</summary>
        Editor = 8,
        /// <summary><c>MainLoop</c>, <c>LateUpdate</c>, <c>Cleanup</c>: may do anything.</summary>
        Other = 9,
    }

    /// <summary>
    /// The meaning of the game's <c>SystemUpdatePhase</c> names for the mod classification (decompiled
    /// <c>Game/SystemUpdatePhase.cs</c>; <c>SimulationSystem</c> runs PreSimulation, GameSimulation and PostSimulation
    /// each step and LoadSimulation while a city loads). Names, not enum values, so Core needs no game reference. Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ModPhases
    {
        public static PhaseKind KindOf(string phase)
        {
            switch (phase ?? "")
            {
                case "GameSimulation":
                    return PhaseKind.Simulation;
                case "PreSimulation":
                case "PostSimulation":
                case "LoadSimulation":
                    return PhaseKind.SimulationStep;
                case "Modification1":
                case "Modification2":
                case "Modification2B":
                case "Modification3":
                case "Modification4":
                case "Modification4B":
                case "Modification5":
                case "ModificationEnd":
                    return PhaseKind.Modification;
                case "PreTool":
                case "PostTool":
                case "ToolUpdate":
                case "ClearTool":
                case "ApplyTool":
                    return PhaseKind.Tool;
                case "UIUpdate":
                case "UITooltip":
                case "Rendering":
                case "PreCulling":
                case "CompleteRendering":
                case "DebugGizmos":
                case "Raycast":
                    return PhaseKind.Presentation;
                case "Serialize":
                case "Deserialize":
                    return PhaseKind.Serialization;
                case "PrefabUpdate":
                case "PrefabReferences":
                    return PhaseKind.Prefab;
                case "EditorSimulation":
                    return PhaseKind.Editor;
                case "MainLoop":
                case "LateUpdate":
                case "Cleanup":
                    return PhaseKind.Other;
                default:
                    return PhaseKind.Unknown;
            }
        }

        /// <summary>True for phases whose systems may change shared state (everything but presentation and the editor).</summary>
        public static bool MayChangeWorld(PhaseKind kind) => kind != PhaseKind.Presentation && kind != PhaseKind.Editor;

        // namespaces of game and engine code that only presents things to the local player
        private static readonly string[] PresentationNamespaces =
        {
            "Game.UI.", "Colossal.UI.", "Colossal.Localization.", "Game.Rendering.", "Game.Audio.", "Game.Input.", "cohtml.",
            "Game.Debug.", "UnityEngine.UI.", "Game.Achievements.",
        };

        // the game's UI types that write shared state when the player uses them (policies, upgrades, the info panel's
        // actions, names, colours, visual customisation, map tiles, water sources, transport lines): patching one of them
        // changes what the world becomes, not only what a player sees
        private static readonly string[] WorldWritingUiTypes =
        {
            "Game.UI.InGame.PoliciesUISystem", "Game.UI.InGame.UpgradesSection", "Game.UI.InGame.ActionsSection", "Game.UI.NameSystem",
            "Game.UI.InGame.ColorSection", "Game.UI.InGame.VisualCustomizeSection", "Game.UI.Editor.MapPanelSystem",
            "Game.UI.Editor.WaterPanelSystem", "Game.UI.InGame.TransportationOverviewUISystem",
        };

        /// <summary>
        /// True when a Harmony patch target (<c>Namespace.Type.Method</c>) is presentation code (UI, localization, audio,
        /// rendering, input): such a patch changes what one player sees, never the shared world. The game's UI types that
        /// write shared state (<see cref="IsWorldWritingUiTarget"/>) are not presentation. Everything else counts as
        /// world-affecting (fail closed), patches of other mods included.
        /// </summary>
        public static bool IsPresentationTarget(string method)
        {
            if (string.IsNullOrEmpty(method) || IsWorldWritingUiTarget(method)) return false;
            foreach (string ns in PresentationNamespaces)
            {
                if (method.StartsWith(ns, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>True when a Harmony patch target is a method of a game UI type that writes shared state.</summary>
        public static bool IsWorldWritingUiTarget(string method)
        {
            if (string.IsNullOrEmpty(method)) return false;
            foreach (string type in WorldWritingUiTypes)
            {
                // the type itself and its nested types (jobs, callbacks): "Type.Method" or "Type+Nested..."/"Type/Nested..."
                if (method.Length > type.Length && method.StartsWith(type, StringComparison.Ordinal))
                {
                    char next = method[type.Length];
                    if (next == '.' || next == '+' || next == '/') return true;
                }
            }
            return false;
        }
    }
}
