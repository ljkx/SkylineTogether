namespace SkylineTogether.Core
{
    /// <summary>
    /// Operations of an <see cref="ApplyProgram"/> (ARCH §4.7.2-4.7.4). Programs are planned in Core
    /// (core-replication <c>ApplyPlanner</c>) and executed op by op, in order, by the FakeWorld of the tests and by the
    /// game's <c>WorldApplier</c>. The fields each op uses are listed per member; unused fields keep their defaults.
    /// </summary>
    public enum ApplyOpKind : byte
    {
        /// <summary>The slot binding is dead or replaced: forget it. Uses Slot.</summary>
        Unbind,

        /// <summary>Add <c>Deleted</c> to the entity bound to Slot (despawn, replacement); never <c>DestroyEntity</c>. Uses Slot.</summary>
        MarkDeleted,

        /// <summary>
        /// Create Count entities from the staging archetype of (Family, Prefab, TypeSet), i.e. without
        /// <c>Created</c>/<c>Updated</c>. GroupIndex numbers the groups of a program 0, 1, 2, ... in order. Flag = the
        /// group consists of movers, which get the shared <c>UpdateFrame</c> value UpdateFrame.
        /// </summary>
        CreateGroup,

        /// <summary>
        /// Bind Slot (Generation, Family, Prefab, TypeSet) to an entity: GroupIndex &gt;= 0 binds the next unbound
        /// entity of that creation group; GroupIndex &lt; 0 binds the predicted entity of <c>LocalId</c> Aux, which a
        /// BIND record attached (ARCH §5.5.1).
        /// </summary>
        Bind,

        /// <summary>Add or remove replicated types so the entity of Slot matches TypeSet (the executor computes the delta
        /// from the real archetype; presence of locally derived EmptyData types is never removed). Uses Slot, TypeSet.</summary>
        ReconcileSignature,

        /// <summary>Decode-then-commit Payload into (Slot, Type); merge-policy types go through their merge rule (ARCH §4.7.4).</summary>
        WriteValue,

        /// <summary>Set the enabled bit of (Slot, Type) to Flag.</summary>
        SetEnabled,

        /// <summary>The value (Slot, Type, Payload) waits for the missing slot Aux; it is written with null references now
        /// and re-applied when Aux binds (ARCH §4.7.5).</summary>
        Park,

        /// <summary>The value (Slot, Type, Payload) is held for a pending proposal of this client (ARCH §5.5.4).</summary>
        Hold,

        /// <summary>Add the tags Tags to the entity of Slot.</summary>
        AddTags,

        /// <summary>Expand the net closure of Slot (edge: both nodes; node: connected edges and their other nodes) and add
        /// Tags (normally <c>Updated</c>) to it; the executor reads the topology (ARCH §4.7.7).</summary>
        ClosureTag,

        /// <summary>Add <c>Created</c> + <c>Updated</c> (+ <c>Applied</c> when Flag marks movers) to the entities of
        /// creation group GroupIndex, after the whole transaction succeeded.</summary>
        Finalize,

        /// <summary>The light flags of the owner Slot changed: derive its street-light poles (ARCH §6.4).</summary>
        DeriveStreetLights,

        /// <summary>Create the derivation event (<see cref="SynthesizedEventKind"/> in Aux) about the entity of Slot;
        /// placed after the values it depends on.</summary>
        SynthesizeEvent
    }
}
