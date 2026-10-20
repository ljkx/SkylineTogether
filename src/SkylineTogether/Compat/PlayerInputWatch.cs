using System;
using SkylineTogether.Core;
using SkylineTogether.Core.Modding;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Client: when the local player last gave input that can edit mod state (<see cref="EditInput"/>: the primary button,
    /// held or not, or the wheel over a panel; a key that is no camera key, held or not; gamepad buttons and the d-pad).
    /// Mod state that changes on a client while its player did nothing of that kind is derived by the client's own copy of
    /// a mod system, not edited by the player, and is not proposed to the host yet (<see cref="ModStateProposalRule"/>).
    /// Polled once per frame at the capture. If the input devices cannot be read, the player always counts as acting (the
    /// behaviour before this rule). Main thread.
    /// </summary>
    public sealed class PlayerInputWatch
    {
        private double _lastInput = double.NegativeInfinity;
        private bool _broken;
        // the primary button went down over a panel (a drag that leaves the panel still edits)
        private bool _pressOverUi;

        /// <summary>The time of the last edit input seen (seconds; negative infinity before any).</summary>
        public double LastInput => _broken ? double.PositiveInfinity : _lastInput;

        /// <summary>Records whether this frame's input can edit mod state.</summary>
        public void Poll(double now)
        {
            if (_broken) return;
            try
            {
                InputSample s = Sample();
                if (EditInput.IsEdit(s)) _lastInput = now;
            }
            catch (Exception ex)
            {
                _broken = true;
                Mod.Log?.Warn(LogCategory.Compat, "reading the input devices failed; mod state changes are proposed as before", ex);
            }
        }

        public void Reset()
        {
            _lastInput = double.NegativeInfinity;
            _pressOverUi = false;
        }

        private InputSample Sample()
        {
            var s = new InputSample();
            Game.Input.InputManager input = Game.Input.InputManager.instance;
            bool overUi = input != null && input.mouseOverUI;
            s.TextFocus = input != null && input.hasInputFieldFocus;
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                ButtonControl left = mouse.leftButton;
                if (left != null)
                {
                    if (left.wasPressedThisFrame) _pressOverUi = overUi;
                    s.PointerEdge = left.wasPressedThisFrame || left.wasReleasedThisFrame;
                    s.PointerHeld = left.isPressed;
                    if (!left.isPressed && !left.wasReleasedThisFrame) _pressOverUi = false;
                }
                s.Wheel = mouse.scroll != null && mouse.scroll.ReadValue().y != 0f;
            }
            s.PointerOverUi = overUi || (s.PointerHeld || s.PointerEdge) && _pressOverUi;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.anyKey.isPressed || keyboard.anyKey.wasReleasedThisFrame))
            {
                foreach (KeyControl k in keyboard.allKeys)
                {
                    if (k == null) continue;
                    bool edge = k.wasPressedThisFrame || k.wasReleasedThisFrame;
                    if (!edge && !k.isPressed) continue;
                    Key key = k.keyCode;
                    if (key == Key.Enter || key == Key.NumpadEnter || key == Key.Tab)
                    {
                        if (edge) s.CommitKey = true;
                        continue;
                    }
                    if (IsCameraOrModifier(key)) continue;
                    if (edge) s.KeyEdge = true;
                    else s.KeyHeld = true;
                }
            }
            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                s.PadButton = Active(pad.buttonSouth) || Active(pad.buttonEast) || Active(pad.buttonWest) || Active(pad.buttonNorth)
                              || Active(pad.leftShoulder) || Active(pad.rightShoulder) || Active(pad.leftTrigger) || Active(pad.rightTrigger)
                              || Active(pad.startButton) || Active(pad.selectButton)
                              || (pad.dpad != null && (Active(pad.dpad.up) || Active(pad.dpad.down) || Active(pad.dpad.left) || Active(pad.dpad.right)));
            }
            return s;
        }

        private static bool Active(ButtonControl b) => b != null && (b.isPressed || b.wasReleasedThisFrame);

        /// <summary>
        /// The game's default camera keys (move, rotate, zoom, tilt), the arrows and page keys, Escape and the lone
        /// modifiers: holding or tapping them moves the view or closes a panel, it edits nothing. (Rebound camera keys
        /// count as input; the window then only stays open longer.)
        /// </summary>
        private static bool IsCameraOrModifier(Key key)
        {
            switch (key)
            {
                case Key.W:
                case Key.A:
                case Key.S:
                case Key.D:
                case Key.Q:
                case Key.E:
                case Key.R:
                case Key.F:
                case Key.T:
                case Key.G:
                case Key.UpArrow:
                case Key.DownArrow:
                case Key.LeftArrow:
                case Key.RightArrow:
                case Key.PageUp:
                case Key.PageDown:
                case Key.Escape:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LeftMeta:
                case Key.RightMeta:
                    return true;
                default:
                    return false;
            }
        }
    }
}
