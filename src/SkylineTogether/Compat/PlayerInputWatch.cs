using System;
using SkylineTogether.Core;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Client: when the local player last pressed or released a button (mouse, keyboard, gamepad). Mod state that changes
    /// on a client while its player did nothing is derived by the client's own copy of a mod system, not edited by the
    /// player, and is never proposed to the host (<see cref="Core.Modding.ModStateProposalRule"/>). Polled once per frame
    /// at the capture. If the input devices cannot be read, the player always counts as acting (the behaviour before this
    /// rule). Main thread.
    /// </summary>
    public sealed class PlayerInputWatch
    {
        private double _lastInput = double.NegativeInfinity;
        private bool _broken;

        /// <summary>The time of the last button edge seen (seconds; negative infinity before any).</summary>
        public double LastInput => _broken ? double.PositiveInfinity : _lastInput;

        /// <summary>Records whether a button went down or up this frame.</summary>
        public void Poll(double now)
        {
            if (_broken) return;
            try
            {
                if (AnyEdge()) _lastInput = now;
            }
            catch (Exception ex)
            {
                _broken = true;
                Mod.Log?.Warn(LogCategory.Compat, "reading the input devices failed; mod state changes are proposed as before", ex);
            }
        }

        public void Reset() => _lastInput = double.NegativeInfinity;

        private static bool AnyEdge()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && (Edge(mouse.leftButton) || Edge(mouse.rightButton) || Edge(mouse.middleButton))) return true;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.anyKey.wasPressedThisFrame || keyboard.anyKey.wasReleasedThisFrame)) return true;
            Gamepad pad = Gamepad.current;
            if (pad == null) return false;
            return Edge(pad.buttonSouth) || Edge(pad.buttonEast) || Edge(pad.buttonWest) || Edge(pad.buttonNorth)
                   || Edge(pad.leftShoulder) || Edge(pad.rightShoulder) || Edge(pad.leftTrigger) || Edge(pad.rightTrigger)
                   || Edge(pad.startButton) || Edge(pad.selectButton);
        }

        private static bool Edge(ButtonControl b) => b != null && (b.wasPressedThisFrame || b.wasReleasedThisFrame);
    }
}
