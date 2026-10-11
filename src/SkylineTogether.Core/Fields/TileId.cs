using System;
using System.Globalization;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Column <see cref="X"/> and row <see cref="Y"/> of a tile in a <see cref="TileGrid"/> (ARCH §4.10.9-4.10.10: 64×64
    /// heightmap tiles, 256×256 water tiles). Tile ids travel on the wire, so a decoded id must be checked with
    /// <see cref="TileGrid.Contains"/> before it is used.
    /// </summary>
    public readonly struct TileId : IEquatable<TileId>
    {
        public readonly ushort X, Y;

        public TileId(ushort x, ushort y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(TileId other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is TileId other && Equals(other);

        public override int GetHashCode() => unchecked((X << 16) | Y);

        public override string ToString() => "tile(" + X.ToString(CultureInfo.InvariantCulture) + ", " + Y.ToString(CultureInfo.InvariantCulture) + ")";

        public static bool operator ==(TileId a, TileId b) => a.Equals(b);

        public static bool operator !=(TileId a, TileId b) => !a.Equals(b);
    }
}
