namespace SkylineTogether.Core
{
    /// <summary>A TYPESET definition (ARCH §3.6, §4.6.3): registry id and the sorted type ranks of the signature.</summary>
    public struct TypeSetDefinition
    {
        public TypeSetId Id;
        /// <summary>Type ranks in ascending order, without duplicates.</summary>
        public TypeRank[] Types;
    }
}
