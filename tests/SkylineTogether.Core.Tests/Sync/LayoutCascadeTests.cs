using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// <see cref="LayoutCascade"/> (R3 M2): deleting a trailer on a client deletes its controller's whole layout, as the
    /// game's <c>VehicleUtils.DeleteVehicle</c> does, so no live controller keeps a <c>LayoutElement</c> naming a
    /// destroyed trailer (the R1 #13 crash class); a controller takes its trailers; a stale controller link does not take
    /// a vehicle that no longer lists the trailer.
    /// </summary>
    public sealed class LayoutCascadeTests
    {
        // a truck (1) with two trailers (2, 3); a car (10) without a layout; a trailer (20) whose controller link names the
        // truck, which does not list it
        private static readonly Dictionary<int, int> Controller = new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 1, [20] = 1 };
        private static readonly Dictionary<int, int[]> Layout = new Dictionary<int, int[]> { [1] = new[] { 1, 2, 3 } };

        private static List<int> Cascade(int vehicle, ISet<int> deleted = null)
        {
            var output = new List<int>();
            LayoutCascade.Collect(vehicle, 0, v => Controller.TryGetValue(v, out int c) ? c : 0, v => Layout.TryGetValue(v, out int[] l) ? l : null,
                v => deleted == null || !deleted.Contains(v), output);
            return output;
        }

        [Test]
        public void ATrailerTakesItsControllerAndTheOtherTrailers()
        {
            Assert.SequenceEqual(new[] { 1, 2, 3 }, Cascade(3), "the controller first, then its layout");
            Assert.SequenceEqual(new[] { 1, 2, 3 }, Cascade(2));
        }

        [Test]
        public void AControllerTakesItsTrailers()
        {
            Assert.SequenceEqual(new[] { 1, 2, 3 }, Cascade(1));
        }

        [Test]
        public void AVehicleWithoutALayoutGoesAlone()
        {
            Assert.SequenceEqual(new[] { 10 }, Cascade(10));
        }

        [Test]
        public void AStaleControllerLinkKeepsTheController()
        {
            Assert.SequenceEqual(new[] { 20 }, Cascade(20), "the truck's layout does not name it");
        }

        [Test]
        public void VehiclesAlreadyBeingDeletedAreLeftOut()
        {
            Assert.SequenceEqual(new[] { 3 }, Cascade(3, new HashSet<int> { 1 }), "the controller is going already: the trailer alone");
            Assert.SequenceEqual(new[] { 1, 3 }, Cascade(1, new HashSet<int> { 2 }));
        }
    }
}
