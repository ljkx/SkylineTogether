namespace SkylineTogether.Core
{
    /// <summary>
    /// A STRUCT in a plan: the payloads of the added types are <see cref="ApplyPlan.Components"/>[<see cref="FirstComponent"/>
    /// ... <see cref="FirstComponent"/> + <c>Header.AddedComponentCount</c> - 1].
    /// </summary>
    public struct StructOp
    {
        public StructHeader Header;
        public int FirstComponent;
    }
}
