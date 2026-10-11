namespace SkylineTogether.Core
{
    /// <summary>How a mod relates to the shared world (ARCH §10.4-10.5).</summary>
    public enum ModClass : byte
    {
        /// <summary>Changes the world: must match exactly on every peer (the default for unknown mods).</summary>
        WorldAffecting,
        /// <summary>Local UI or tooling only: eligible for the host's optional local-only relaxation.</summary>
        LocalOnly,
        /// <summary>Content (assets): covered by the prefab digest and the referenced-content check.</summary>
        Content
    }
}
