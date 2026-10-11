namespace SkylineTogether.Core
{
    /// <summary>A KEYED or KEYED_DEL record in a plan: state on a derived entity, addressed by its key (ARCH §4.10.7).</summary>
    public struct KeyedOp
    {
        public DerivedKey Key;
        public TypeRank Type;
        public PayloadRef Payload;
        /// <summary>True for KEYED_DEL (a keyed deletion); <see cref="Type"/> and <see cref="Payload"/> are unused then.</summary>
        public bool Delete;
    }
}
