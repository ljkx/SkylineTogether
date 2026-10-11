namespace SkylineTogether.Core
{
    /// <summary>Outcome of an observed value against the host's hash shadow.</summary>
    public enum ObserveResult : byte
    {
        /// <summary>The canonical hash equals the shadow; nothing to send.</summary>
        Unchanged,
        /// <summary>The value changed; shadow and dirty bits were updated (or the value was queued for emission).</summary>
        Changed,
        /// <summary>The pair had no shadow entry yet.</summary>
        New
    }
}
