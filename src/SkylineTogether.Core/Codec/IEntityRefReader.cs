namespace SkylineTogether.Core.Codec
{
    /// <summary>
    /// The entity seam of <see cref="CanonicalReader"/>: resolves a decoded <see cref="NetRef"/> to a machine-local entity
    /// handle (ARCH §4.3.1, §4.7.5). The game implements it with <c>EntityRefBridge</c> over the entity map; tests use
    /// fakes.
    /// <para>
    /// The reader calls the seam for <b>every</b> reference it decodes, <see cref="NetRef.Null"/> included, in read
    /// order, so an implementation can number the references of a record (the presence-safe stand-in resolvers use that
    /// ordinal, ARCH §4.11.3). An implementation must not throw for a well-formed reference; whatever it throws reaches
    /// the caller of the reader unchanged (the applier's decode-then-commit catches it and leaves the live value alone).
    /// </para>
    /// </summary>
    public interface IEntityRefReader
    {
        /// <summary>
        /// The entity <c>(index, version)</c> for <paramref name="value"/>; <c>(0, 0)</c> is <c>Entity.Null</c>.
        /// <paramref name="missing"/> is true when the reference names something that does not resolve yet (an unbound
        /// slot, an unresolved derived key): the record is then parked and applied again later (ARCH §4.7.5), and
        /// <paramref name="missingSlot"/> names the slot to wait for (<see cref="NetSlot.None"/> when there is none).
        /// For a missing reference the implementation returns the null entity or, for presence-safe types, a local
        /// stand-in; the reader passes the returned handle through unchanged.
        /// </summary>
        (int index, int version) DecodeEntity(in NetRef value, out bool missing, out NetSlot missingSlot);
    }
}
