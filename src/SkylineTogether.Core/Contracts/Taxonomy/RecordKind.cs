namespace SkylineTogether.Core
{
    /// <summary>Record kinds inside epoch sections (ARCH §4.6.3); the values travel on the wire.</summary>
    public enum RecordKind : byte
    {
        TypeSetDef = 1,
        Spawn,
        Despawn,
        Struct,
        Value,
        Touch,
        Bind,
        Pose,
        Keyed,
        KeyedDelete,
        Zone,
        Icon,
        Name,
        PrefabState,
        SysState,
        Result,
        Hash,
        Event,
        TerrainStamp,
        TypeSetKeyframe
    }
}
