namespace SkylineTogether.Core.Modding
{
    /// <summary>
    /// One frame of the local player's input, classified by the game layer (<c>Compat.PlayerInputWatch</c>) for
    /// <see cref="EditInput.IsEdit"/>. Camera input (camera keys, the right and middle mouse buttons, the gamepad sticks,
    /// the wheel over the world) is not part of it.
    /// </summary>
    public struct InputSample
    {
        /// <summary>The primary (left) mouse button went down or up this frame.</summary>
        public bool PointerEdge;
        /// <summary>The primary mouse button is held (a slider being dragged, a button held down).</summary>
        public bool PointerHeld;
        /// <summary>The pointer is over the UI (a panel), or the held button went down over it.</summary>
        public bool PointerOverUi;
        /// <summary>The wheel turned this frame.</summary>
        public bool Wheel;
        /// <summary>A key other than a camera key or a lone modifier went down or up this frame.</summary>
        public bool KeyEdge;
        /// <summary>Such a key is held (a hotkey's key repeat makes no new edge).</summary>
        public bool KeyHeld;
        /// <summary>Enter or Tab went down or up this frame (what commits a text field).</summary>
        public bool CommitKey;
        /// <summary>A text field has the keyboard focus (the chat, a name): typing in it is no edit.</summary>
        public bool TextFocus;
        /// <summary>A gamepad face, shoulder or menu button or the d-pad went down or up (UI navigation), or is held.</summary>
        public bool PadButton;
    }

    /// <summary>
    /// Whether a frame's input is one that can edit mod state through a panel or a hotkey (<see cref="ModStateProposalRule"/>):
    /// the primary button or the wheel over a panel (pressed, released or held: a slider dragged for longer than the
    /// window still counts), a key that is no camera key (pressed, released or held), gamepad buttons and the d-pad. Moving
    /// the camera, clicking in the world without a tool (a selection; mod tools count through the tool path) and typing
    /// in a text field (the chat) are no edits, so a mod system's derivation meanwhile is not proposed. Pure.
    /// </summary>
    public static class EditInput
    {
        public static bool IsEdit(in InputSample s)
        {
            if ((s.PointerEdge || s.PointerHeld || s.Wheel) && s.PointerOverUi) return true;
            if (s.TextFocus) return s.CommitKey;
            if (s.KeyEdge || s.KeyHeld || s.CommitKey) return true;
            return s.PadButton;
        }
    }
}
