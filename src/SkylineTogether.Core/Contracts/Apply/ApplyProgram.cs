using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// An ordered list of apply operations (ARCH §4.7.2-4.7.4), planned in Core from an <see cref="ApplyPlan"/> and
    /// executed by the FakeWorld (tests) and the game's <c>WorldApplier</c>; both execute the same programs, so the
    /// Core tests specify the game executor. Payload ranges refer to <see cref="Source"/>'s arena.
    /// </summary>
    public sealed class ApplyProgram
    {
        public readonly List<ApplyOp> Ops = new List<ApplyOp>();

        /// <summary>The plan whose arena holds the payloads of <see cref="Ops"/>.</summary>
        public ApplyPlan Source;

        /// <summary>Number of creation groups (CreateGroup operations) in the program.</summary>
        public int CreatedGroups
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Ops.Count; i++)
                {
                    if (Ops[i].Kind == ApplyOpKind.CreateGroup) count++;
                }
                return count;
            }
        }

        public void Clear()
        {
            Ops.Clear();
            Source = null;
        }

        /// <summary>
        /// Checks the structural conventions of <see cref="ApplyOpKind"/> before execution, so an executor can refuse a
        /// malformed program instead of failing halfway: groups numbered 0, 1, 2, ... with a positive count and a type
        /// set; binds only into existing groups and never more than their count; every group finalised once, after all
        /// its entities were bound; slots present where an op needs one; payloads inside the source arena; defined
        /// event kinds. Returns false with the first problem in <paramref name="error"/>.
        /// </summary>
        public bool Validate(out string error)
        {
            var groupCounts = new List<int>();
            var groupBinds = new List<int>();
            var groupFinalized = new List<bool>();
            for (int i = 0; i < Ops.Count; i++)
            {
                ApplyOp op = Ops[i];
                string problem = CheckOp(op, groupCounts, groupBinds, groupFinalized);
                if (problem != null)
                {
                    error = "op " + i.ToString(CultureInfo.InvariantCulture) + " (" + op + "): " + problem;
                    return false;
                }
            }
            for (int g = 0; g < groupCounts.Count; g++)
            {
                if (groupBinds[g] != groupCounts[g])
                {
                    error = "group " + g.ToString(CultureInfo.InvariantCulture) + " creates " + groupCounts[g].ToString(CultureInfo.InvariantCulture)
                            + " entities but binds " + groupBinds[g].ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }
            error = null;
            return true;
        }

        private string CheckOp(in ApplyOp op, List<int> groupCounts, List<int> groupBinds, List<bool> groupFinalized)
        {
            switch (op.Kind)
            {
                case ApplyOpKind.CreateGroup:
                    if (op.GroupIndex != groupCounts.Count) return "groups must be numbered in order (expected " + groupCounts.Count.ToString(CultureInfo.InvariantCulture) + ")";
                    if (op.Count <= 0) return "a creation group needs a positive count";
                    if (op.TypeSet.IsNone) return "a creation group needs a type set";
                    groupCounts.Add(op.Count);
                    groupBinds.Add(0);
                    groupFinalized.Add(false);
                    return null;
                case ApplyOpKind.Bind:
                    if (op.Slot.IsNone) return "bind without a slot";
                    if (op.GroupIndex >= 0)
                    {
                        if (op.GroupIndex >= groupCounts.Count) return "bind into a group that was not created";
                        if (groupFinalized[op.GroupIndex]) return "bind into a finalised group";
                        if (groupBinds[op.GroupIndex] >= groupCounts[op.GroupIndex]) return "more binds than entities in the group";
                        groupBinds[op.GroupIndex]++;
                    }
                    else if (op.Aux == 0)
                    {
                        return "a prediction bind needs the LocalId in Aux";
                    }
                    return null;
                case ApplyOpKind.Finalize:
                    if (op.GroupIndex < 0 || op.GroupIndex >= groupCounts.Count) return "finalise of a group that was not created";
                    if (groupFinalized[op.GroupIndex]) return "group finalised twice";
                    if (groupBinds[op.GroupIndex] != groupCounts[op.GroupIndex]) return "group finalised before all its entities were bound";
                    groupFinalized[op.GroupIndex] = true;
                    return null;
                case ApplyOpKind.WriteValue:
                case ApplyOpKind.Hold:
                    if (op.Slot.IsNone) return "value op without a slot";
                    return CheckPayload(op.Payload);
                case ApplyOpKind.Park:
                    if (op.Slot.IsNone) return "park without a slot";
                    if (op.Aux == 0 || op.Aux > NetSlot.MaxValue) return "park needs the missing slot in Aux";
                    return CheckPayload(op.Payload);
                case ApplyOpKind.ReconcileSignature:
                    if (op.Slot.IsNone) return "signature reconcile without a slot";
                    if (op.TypeSet.IsNone) return "signature reconcile without a type set";
                    return null;
                case ApplyOpKind.SynthesizeEvent:
                    if (op.Slot.IsNone) return "event without a slot";
                    if (op.Aux > (uint)SynthesizedEventKind.PathUpdated) return "unknown synthesized event kind";
                    return null;
                case ApplyOpKind.Unbind:
                case ApplyOpKind.MarkDeleted:
                case ApplyOpKind.SetEnabled:
                case ApplyOpKind.AddTags:
                case ApplyOpKind.ClosureTag:
                case ApplyOpKind.DeriveStreetLights:
                    return op.Slot.IsNone ? "op without a slot" : null;
                default:
                    return "unknown op kind";
            }
        }

        private string CheckPayload(PayloadRef payload)
        {
            if (payload.Offset < 0 || payload.Length < 0) return "negative payload range";
            if (Source != null && !Source.Arena.Contains(payload)) return "payload range outside the source arena";
            return null;
        }
    }
}
