using System.Globalization;

namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// One sample of a mover's <c>Game.Objects.TransformFrame</c> ring slot (R15 §1, <c>Game.Objects/TransformFrame.cs:7-22</c>):
    /// position, velocity, rotation, <c>TransformFlags</c>, and for creatures the animation state, activity and state
    /// timer. The movers write one frame per 16 ticks (<c>CarMoveSystem.cs:420-423</c>), and the position is the
    /// midpoint of the 16-tick step (<c>CarMoveSystem.cs:181-182, 262</c>); <c>ObjectInterpolateSystem.CalculateTransform</c>
    /// blends two consecutive frames with a Hermite curve whose tangents are the velocities
    /// (<c>ObjectInterpolateSystem.cs:4352-4360</c>), so velocity is part of the visual result.
    /// <para>
    /// <see cref="Synthetic"/> marks frames the client made up (extrapolated, interpolated or pinned, ARCHITECTURE.md#movers);
    /// such frames are never used as delta references and are replaced as soon as the real frame arrives.
    /// Positions and velocities are world metres and metres per second; the rotation is a unit quaternion (x, y, z, w).
    /// </para>
    /// </summary>
    public struct MotionFrame
    {
        public float X, Y, Z, VX, VY, VZ, QX, QY, QZ, QW;

        /// <summary><c>Game.Objects.TransformFlags</c> (18 bits in the current game build; all 32 bits travel).</summary>
        public uint Flags;

        /// <summary><c>Game.Objects.TransformState</c> and the activity id (creatures; 0 for vehicles).</summary>
        public byte State, Activity;

        /// <summary>Animation state timer; the movers advance it by one per frame while state and activity stay the same
        /// (<c>Game.Objects/ObjectUtils.cs</c> <c>UpdateAnimation</c>).</summary>
        public ushort StateTimer;

        /// <summary>The client made this frame up (never a delta reference).</summary>
        public bool Synthetic;

        public override string ToString()
        {
            return "pos(" + F(X) + ", " + F(Y) + ", " + F(Z) + ") vel(" + F(VX) + ", " + F(VY) + ", " + F(VZ) + ") rot("
                   + F(QX) + ", " + F(QY) + ", " + F(QZ) + ", " + F(QW) + ") flags 0x" + Flags.ToString("X", CultureInfo.InvariantCulture)
                   + " state " + State.ToString(CultureInfo.InvariantCulture) + "/" + Activity.ToString(CultureInfo.InvariantCulture)
                   + "/" + StateTimer.ToString(CultureInfo.InvariantCulture) + (Synthetic ? " synthetic" : string.Empty);
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
