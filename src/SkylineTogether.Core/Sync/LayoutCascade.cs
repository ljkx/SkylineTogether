using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Which vehicles go when a client deletes one (ARCHITECTURE.md#movers): the game deletes a vehicle's whole layout at
    /// once (<c>VehicleUtils.DeleteVehicle</c>: every <c>LayoutElement</c> of the controller), and its interpolation reads
    /// the layout's members unchecked, so a head left alive with a layout that names a destroyed trailer crashes the game.
    /// Deleting a trailer (<c>Controller.m_Controller</c> is another vehicle whose layout lists it) therefore deletes its
    /// controller's whole layout; deleting a controller deletes its layout. The vehicle itself is always part of the
    /// result. Generic over the entity type so the rule is testable without the game. Pure.
    /// </summary>
    public static class LayoutCascade
    {
        /// <summary>
        /// The vehicles to delete with <paramref name="vehicle"/>, the layout's controller first, each once.
        /// <paramref name="controllerOf"/>: the vehicle's <c>Controller</c> (or <paramref name="none"/>);
        /// <paramref name="layoutOf"/>: a controller's <c>LayoutElement</c> list (null or empty when it has none);
        /// <paramref name="alive"/>: exists and is not being deleted already.
        /// </summary>
        public static void Collect<T>(T vehicle, T none, Func<T, T> controllerOf, Func<T, IReadOnlyList<T>> layoutOf, Func<T, bool> alive, List<T> output)
            where T : IEquatable<T>
        {
            if (controllerOf == null) throw new ArgumentNullException(nameof(controllerOf));
            if (layoutOf == null) throw new ArgumentNullException(nameof(layoutOf));
            if (alive == null) throw new ArgumentNullException(nameof(alive));
            if (output == null) throw new ArgumentNullException(nameof(output));
            T root = vehicle;
            T controller = controllerOf(vehicle);
            // a trailer of a live controller whose layout lists it: the whole layout goes (a stale Controller link that
            // the controller's layout does not name keeps the controller)
            if (!controller.Equals(none) && !controller.Equals(vehicle) && alive(controller) && Lists(layoutOf(controller), vehicle)) root = controller;
            Add(output, root, none);
            IReadOnlyList<T> layout = layoutOf(root);
            if (layout != null)
            {
                for (int i = 0; i < layout.Count; i++)
                {
                    T member = layout[i];
                    if (member.Equals(none) || !alive(member)) continue;
                    Add(output, member, none);
                }
            }
            Add(output, vehicle, none);
        }

        private static bool Lists<T>(IReadOnlyList<T> layout, T vehicle) where T : IEquatable<T>
        {
            if (layout == null) return false;
            for (int i = 0; i < layout.Count; i++)
            {
                if (layout[i].Equals(vehicle)) return true;
            }
            return false;
        }

        private static void Add<T>(List<T> output, T e, T none) where T : IEquatable<T>
        {
            if (e.Equals(none)) return;
            for (int i = 0; i < output.Count; i++)
            {
                if (output[i].Equals(e)) return;
            }
            output.Add(e);
        }
    }
}
