using System;

namespace SkylineTogether.Core
{
    /// <summary>Permission flags gating proposal categories (ARCH §5.6, §14.4).</summary>
    [Flags]
    public enum Permission : uint
    {
        None = 0,
        Build = 1,
        Demolish = 2,
        Terraform = 4,
        Zoning = 8,
        /// <summary>Taxes, budget, fees, loan.</summary>
        Economy = 16,
        Policies = 32,
        /// <summary>Dev tree purchases.</summary>
        Progression = 64,
        MapTiles = 128,
        Speed = 256,
        ModTools = 512,
        Names = 1024,
        Chat = 2048,
        /// <summary>Every flag above.</summary>
        All = 4095
    }
}
