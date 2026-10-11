namespace SkylineTogether.Core
{
    /// <summary>Fault categories routed to the safe-mode controller (ARCH §12.1-12.2).</summary>
    public enum FaultCategory : byte
    {
        /// <summary>A structural apply exception: the follower freezes and resyncs.</summary>
        StructuralApply,
        /// <summary>A value-codec exception: the type is quarantined and its class rebaselined.</summary>
        ValueApply,
        /// <summary>A host capture-domain exception.</summary>
        Capture,
        /// <summary>A host commit exception: the proposal is rejected with "host error".</summary>
        Commit,
        Snapshot,
        Transport,
        /// <summary>A malformed message or record.</summary>
        Decode,
        Ui,
        Probe,
        /// <summary>The client load hand-over failed (ARCH §6.1).</summary>
        Handover
    }
}
