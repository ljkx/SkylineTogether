namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// One difference between the host's and a client's line-state rows (ARCH §4.11.6, <c>/st lines compare</c>):
    /// the line, the field name (a <see cref="LineStateRow"/> field, or <see cref="LineStateReport.LineField"/> for a
    /// missing or extra line, <see cref="LineStateReport.DuplicateField"/> for a line listed twice) and both values as
    /// invariant text.
    /// </summary>
    public sealed class LineStateDifference
    {
        public NetSlot Line;
        public string Field;
        public string HostValue;
        public string ClientValue;

        public override string ToString() => Line + " " + Field + ": host " + HostValue + ", client " + ClientValue;
    }
}
