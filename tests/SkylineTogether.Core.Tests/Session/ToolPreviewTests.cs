using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>The tool preview model, its wire format and limits, smoothing, throttling and relay.</summary>
    public sealed class ToolPreviewTests
    {
        private static PreviewPoint P(float x, float y, float z) => new PreviewPoint(x, y, z);

        private static byte[] Encode(ToolPreview preview, int maxBytes = ToolPreviewCodec.MaxBytes)
        {
            var w = new ByteWriter();
            int length = ToolPreviewCodec.Encode(preview, w, maxBytes);
            Assert.Equal(length, w.Length);
            return w.ToArray();
        }

        private static ToolPreview RoundTrip(ToolPreview preview)
        {
            byte[] bytes = Encode(preview);
            var back = new ToolPreview();
            Assert.True(ToolPreviewCodec.Decode(bytes, 0, bytes.Length, back));
            return back;
        }

        private static ToolPreview Sample()
        {
            var p = new ToolPreview();
            p.AddCurve(ToolPreviewFlags.None, P(1000f, 40f, -2000f), P(1010f, 41f, -1990f), P(1030f, 42f, -1985f), P(1050f, 43f, -1980f), 12.5f);
            p.AddSegment(ToolPreviewFlags.Secondary, P(990f, 40f, -2010f), P(995f, 40f, -2020f), 8f);
            p.AddCircle(ToolPreviewFlags.Delete, P(1001f, 40f, -2001f), 15f);
            p.AddRect(ToolPreviewFlags.Error, P(1100f, 45f, -1900f), 32f, 48f, 2.5f);
            p.BeginPolyline(ToolPreviewFlags.Closed, 0f);
            p.AddPolylinePoint(P(900f, 30f, -2100f));
            p.AddPolylinePoint(P(950f, 31f, -2150f));
            p.AddPolylinePoint(P(920f, 32f, -2200f));
            p.EndPolyline();
            p.AddZoneBlock(ToolPreviewFlags.None, P(1200f, 50f, -1800f), -1f, 10, 6, 0x0FFC00000F03UL);
            return p;
        }

        // ================================================================== model

        [Test]
        public void ShapesKeepTheirFloatsFlagsAndBits()
        {
            ToolPreview p = Sample();
            Assert.Equal(6, p.Count);
            Assert.Equal(ToolPreviewShape.Curve, p.ShapeAt(0));
            Assert.Equal(ToolPreview.CurveFloats, p.LengthOf(0));
            Assert.Equal(12.5f, p.Data[p.StartOf(0) + 12]);
            Assert.Equal(1050f, p.PointAt(0, 3).X);
            Assert.Equal(ToolPreviewFlags.Delete, p.FlagsAt(2));
            Assert.Equal(3, p.PolylinePointCount(4));
            Assert.Equal(950f, p.PointAt(4, 1).X);
            Assert.Equal(0x0FFC00000F03UL, p.BitsAt(5));
            Assert.False(p.Truncated);
            p.Clear();
            Assert.True(p.IsEmpty);
            Assert.Equal(0, p.FloatCount);
        }

        [Test]
        public void CapacityLimitsTruncateInsteadOfThrowing()
        {
            var p = new ToolPreview();
            for (int i = 0; i < ToolPreview.MaxShapes; i++) Assert.True(p.AddCircle(ToolPreviewFlags.None, P(i, 0, 0), 1f));
            Assert.False(p.Truncated);
            Assert.False(p.AddCircle(ToolPreviewFlags.None, P(0, 0, 0), 1f));
            Assert.True(p.Truncated);
            Assert.Equal(ToolPreview.MaxShapes, p.Count);

            var line = new ToolPreview();
            Assert.True(line.BeginPolyline(ToolPreviewFlags.None, 2f));
            for (int i = 0; i < ToolPreview.MaxPolylinePoints; i++) Assert.True(line.AddPolylinePoint(P(i, 0, i)));
            Assert.False(line.AddPolylinePoint(P(0, 0, 0)), "a polyline holds 64 points");
            Assert.True(line.Truncated);
            Assert.True(line.EndPolyline());

            var single = new ToolPreview();
            single.BeginPolyline(ToolPreviewFlags.None, 2f);
            single.AddPolylinePoint(P(1, 2, 3));
            Assert.False(single.EndPolyline(), "a polyline of one point is dropped");
            Assert.Equal(0, single.Count);
            Assert.Equal(0, single.FloatCount);
            Assert.False(single.AddPolylinePoint(P(1, 2, 3)), "no open polyline");

            var zones = new ToolPreview();
            Assert.False(zones.AddZoneBlock(ToolPreviewFlags.None, P(0, 0, 0), 0f, 10, 7, 1UL), "70 cells exceed a block");
            Assert.True(zones.AddZoneBlock(ToolPreviewFlags.None, P(0, 0, 0), 0f, 2, 2, ulong.MaxValue));
            Assert.Equal(0xFUL, zones.BitsAt(0), "bits beyond the block are dropped");
        }

        // ================================================================== wire format

        [Test]
        public void EveryShapeRoundTripsWithinTheQuantum()
        {
            ToolPreview p = Sample();
            ToolPreview back = RoundTrip(p);
            Assert.True(back.SameLayout(p));
            Assert.Equal(p.Count, back.Count);
            for (int i = 0; i < p.Count; i++)
            {
                Assert.Equal(p.ShapeAt(i), back.ShapeAt(i));
                Assert.Equal(p.FlagsAt(i), back.FlagsAt(i));
                Assert.Equal(p.BitsAt(i), back.BitsAt(i));
            }
            float q = ToolPreviewCodec.Quantum(0);
            for (int k = 0; k < p.FloatCount; k++)
            {
                if (k == p.StartOf(5) + 3) continue; // the zone yaw is compared below (it wraps)
                Assert.Near(p.Data[k], back.Data[k], q, "float " + k); // positions 1/16 m, sizes 1/16 m, yaw 1e-4 rad
            }
            Assert.Near(2.5, back.Data[back.StartOf(3) + 5], 1e-3, "yaw");
            Assert.Near(2 * Math.PI - 1, back.Data[back.StartOf(5) + 3], 1e-3, "negative yaw comes back in [0, 2pi)");
        }

        [Test]
        public void EmptyPreviewIsZeroBytes()
        {
            var p = new ToolPreview();
            Assert.Equal(0, Encode(p).Length);
            var back = Sample();
            Assert.True(ToolPreviewCodec.Decode(new byte[0], 0, 0, back));
            Assert.True(back.IsEmpty);
        }

        [Test]
        public void QuantumGrowsWithTheExtentOfThePreview()
        {
            var p = new ToolPreview();
            p.AddCircle(ToolPreviewFlags.None, P(-7000f, 100f, -7000f), 10f);
            p.AddCircle(ToolPreviewFlags.None, P(7000f, 300f, 7000f), 10f); // a line across the whole map
            byte[] bytes = Encode(p);
            Assert.Equal(3, (int)bytes[2], "14 km need 1/2 m steps");
            ToolPreview back = RoundTrip(p);
            Assert.Near(7000f, back.PointAt(1, 0).X, ToolPreviewCodec.Quantum(3));
            Assert.Near(7000f, back.PointAt(1, 0).Z, ToolPreviewCodec.Quantum(3));

            var small = new ToolPreview();
            small.AddCircle(ToolPreviewFlags.None, P(5f, 0f, 5f), 1f);
            small.AddCircle(ToolPreviewFlags.None, P(1500f, 0f, 5f), 1f);
            Assert.Equal(0, (int)Encode(small)[2], "within 2 km: 1/16 m");
        }

        [Test]
        public void NonFiniteValuesAreWrittenSafely()
        {
            var p = new ToolPreview();
            p.AddCircle(ToolPreviewFlags.None, P(10f, 20f, 30f), float.NaN);
            p.AddRect(ToolPreviewFlags.None, P(float.PositiveInfinity, 20f, 30f), -5f, 1e9f, float.NaN);
            ToolPreview back = RoundTrip(p);
            Assert.Equal(0f, back.Data[back.StartOf(0) + 3]);
            Assert.Equal(10f, back.PointAt(1, 0).X, "a non-finite coordinate falls back to the anchor");
            Assert.Equal(0f, back.Data[back.StartOf(1) + 3], "negative sizes clamp to 0");
            Assert.Equal(65535f / 16f, back.Data[back.StartOf(1) + 4], "huge sizes clamp");
        }

        [Test]
        public void TheBudgetKeepsTheFirstShapesAndFlagsTheRest()
        {
            var p = new ToolPreview();
            for (int i = 0; i < 100; i++) p.AddCurve(ToolPreviewFlags.None, P(i, 0, 0), P(i, 0, 1), P(i, 0, 2), P(i, 0, 3), 8f);
            byte[] bytes = Encode(p);
            Assert.True(bytes.Length <= ToolPreviewCodec.MaxBytes);
            Assert.True(ToolPreviewCodec.Truncated(bytes, 0, bytes.Length));
            var back = new ToolPreview();
            Assert.True(ToolPreviewCodec.Decode(bytes, 0, bytes.Length, back));
            Assert.True(back.Truncated);
            Assert.InRange(back.Count, 35, 40);
            for (int i = 0; i < back.Count; i++) Assert.Near(i, back.PointAt(i, 0).X, 0.07, "the first shapes survive, in order");

            byte[] tiny = Encode(p, 40);
            Assert.True(tiny.Length <= 40);
            back = new ToolPreview();
            Assert.True(ToolPreviewCodec.Decode(tiny, 0, tiny.Length, back));
            Assert.Equal(0, Encode(p, 20).Length, "nothing fits: no preview");

            Assert.False(ToolPreviewCodec.Truncated(Encode(Sample()), 0, Encode(Sample()).Length));
        }

        [Test]
        public void AFullPresenceMessageIsOneDatagram()
        {
            var p = new ToolPreview();
            var random = new DeterministicRandom(31);
            for (int i = 0; i < ToolPreview.MaxShapes; i++)
            {
                p.BeginPolyline(ToolPreviewFlags.Closed, 3f);
                for (int k = 0; k < ToolPreview.MaxPolylinePoints; k++) p.AddPolylinePoint(P((float)random.NextDouble() * 9000f, (float)random.NextDouble() * 500f, (float)random.NextDouble() * 9000f));
            }
            var preview = new ByteWriter();
            int length = ToolPreviewCodec.Encode(p, preview);
            Assert.True(length > 0 && length <= PresenceMessage.MaxPreviewBytes);
            var m = new PresenceMessage
            {
                From = new PeerId(255), CursorX = 1e30f, ToolKind = 255, Prefab = new PrefabRank(int.MaxValue), Color = uint.MaxValue,
                Preview = preview.Buffer, PreviewLength = length, BrushRadius = 1e30f
            };
            var body = new ByteWriter();
            PresenceMessage.Write(body, m);
            foreach (EnvelopeCodec codec in new[] { new EnvelopeCodec(null), new EnvelopeCodec(new ManagedLz4Compressor()) })
            {
                var envelope = new ByteWriter();
                codec.Encode(MessageKind.Presence, body.Buffer, 0, body.Length, envelope);
                Assert.True(envelope.Length <= Limits.MaxDatagramBytes, "envelope of " + envelope.Length + " bytes");
            }

            // the largest possible body, whatever its preview holds
            var max = new PresenceMessage { Prefab = new PrefabRank(int.MaxValue), Preview = new byte[PresenceMessage.MaxPreviewBytes], PreviewLength = PresenceMessage.MaxPreviewBytes };
            body.Reset();
            PresenceMessage.Write(body, max);
            Assert.True(body.Length + Limits.EnvelopeOverheadBytes <= Limits.MaxDatagramBytes);
        }

        [Test]
        public void AnotherVersionIsIgnoredNotRejected()
        {
            foreach (byte version in new byte[] { 1, 3 })
            {
                byte[] bytes = Encode(Sample());
                bytes[0] = version;
                ToolPreview into = Sample();
                Assert.False(ToolPreviewCodec.Decode(bytes, 0, bytes.Length, into), "version " + version);
                Assert.True(into.IsEmpty);
            }
        }

        // ================================================================== heights and elevation

        private static ToolPreview Elevated()
        {
            var p = new ToolPreview { Elevation = 12.5f };
            // a bridge rising from the bank (40 m) by two elevation steps, a tunnel 8 m below, the raised node, an elevated object
            p.AddCurve(ToolPreviewFlags.Elevated, P(2000f, 40f, 3000f), P(2020f, 45f, 3000f), P(2040f, 50f, 3000f), P(2060f, 52.5f, 3000f), 16f);
            p.AddCurve(ToolPreviewFlags.Underground, P(2060f, 32f, 3000f), P(2080f, 32f, 3010f), P(2100f, 32f, 3020f), P(2120f, 32f, 3030f), 12f);
            p.AddCircle(ToolPreviewFlags.Elevated | ToolPreviewFlags.Secondary, P(2060f, 52.5f, 3000f), 16f);
            p.AddRect(ToolPreviewFlags.Elevated | ToolPreviewFlags.Error, P(2200f, 61.25f, 3100f), 8f, 12f, 1f);
            return p;
        }

        [Test]
        public void HeightsElevationFlagsAndTheToolElevationRoundTrip()
        {
            ToolPreview p = Elevated();
            ToolPreview back = RoundTrip(p);
            Assert.True(back.SameLayout(p));
            for (int i = 0; i < p.Count; i++) Assert.Equal(p.FlagsAt(i), back.FlagsAt(i), "flags of shape " + i);
            Assert.Equal(12.5f, back.Elevation, "the tool elevation");
            float half = ToolPreviewCodec.Quantum(0) / 2;
            for (int k = 0; k < 4; k++) Assert.Near(p.PointAt(0, k).Y, back.PointAt(0, k).Y, half, "bridge height " + k);
            Assert.Near(32f, back.PointAt(1, 2).Y, half, "tunnel depth");
            Assert.Near(52.5f, back.PointAt(2, 0).Y, half, "node height");
            Assert.Near(61.25f, back.PointAt(3, 0).Y, half, "object height");

            foreach (float elevation in new[] { 1.25f, -8f, 2.5f, -0.0625f, 400f })
            {
                p.Elevation = elevation;
                Assert.Equal(elevation, RoundTrip(p).Elevation, "elevation " + elevation);
            }
            p.Elevation = 1e6f;
            Assert.Equal(ToolPreviewCodec.MaxElevation, RoundTrip(p).Elevation, "clamped");
            p.Elevation = -1e6f;
            Assert.Equal(-ToolPreviewCodec.MaxElevation, RoundTrip(p).Elevation, "clamped below");
            p.Elevation = float.NaN;
            Assert.Equal(0f, RoundTrip(p).Elevation, "not a number: none");
            p.Elevation = 0.01f;
            Assert.Equal(0f, RoundTrip(p).Elevation, "below 1/32 m: none");
        }

        [Test]
        public void TheElevationCostsNothingUnlessTheToolHasOne()
        {
            // header 16, curve 28, segment 16, circle 10, rect 14, polyline of 3 points 23, zone block with 44 bits 18
            Assert.Equal(125, Encode(Sample()).Length, "the same size as before heights got their own quantum");
            ToolPreview flagged = Elevate(Sample());
            Assert.Equal(125, Encode(flagged).Length, "elevation flags use spare bits");
            flagged.Elevation = -8f;
            Assert.Equal(127, Encode(flagged).Length, "a tool elevation adds 2 bytes");
            Assert.Equal(0, Encode(new ToolPreview { Elevation = 5f }).Length, "no shapes: no preview, whatever the elevation");
        }

        /// <summary>A copy of <paramref name="p"/> with every shape marked elevated or underground in turn.</summary>
        private static ToolPreview Elevate(ToolPreview p)
        {
            var r = new ToolPreview();
            float[] d = p.Data;
            for (int i = 0; i < p.Count; i++)
            {
                ToolPreviewFlags f = p.FlagsAt(i) | (i % 2 == 0 ? ToolPreviewFlags.Elevated : ToolPreviewFlags.Underground);
                int o = p.StartOf(i);
                switch (p.ShapeAt(i))
                {
                    case ToolPreviewShape.Curve: r.AddCurve(f, p.PointAt(i, 0), p.PointAt(i, 1), p.PointAt(i, 2), p.PointAt(i, 3), d[o + 12]); break;
                    case ToolPreviewShape.Segment: r.AddSegment(f, p.PointAt(i, 0), p.PointAt(i, 1), d[o + 6]); break;
                    case ToolPreviewShape.Circle: r.AddCircle(f, p.PointAt(i, 0), d[o + 3]); break;
                    case ToolPreviewShape.Rect: r.AddRect(f, p.PointAt(i, 0), d[o + 3], d[o + 4], d[o + 5]); break;
                    case ToolPreviewShape.Polyline:
                        r.BeginPolyline(f, d[o]);
                        for (int k = 0; k < p.PolylinePointCount(i); k++) r.AddPolylinePoint(p.PointAt(i, k));
                        r.EndPolyline();
                        break;
                    case ToolPreviewShape.ZoneBlock: r.AddZoneBlock(f, p.PointAt(i, 0), d[o + 3], (int)d[o + 4], (int)d[o + 5], p.BitsAt(i)); break;
                }
            }
            return r;
        }

        [Test]
        public void HeightsKeepTheirPrecisionAcrossTheMap()
        {
            // a line across the whole map: 1/2 m horizontally, but heights keep 1/16 m
            var wide = new ToolPreview();
            wide.AddCircle(ToolPreviewFlags.Elevated, P(-7100f, 101.3125f, -7100f), 10f);
            wide.AddCircle(ToolPreviewFlags.Elevated, P(7100f, 113.8125f, 7100f), 10f);
            byte[] bytes = Encode(wide);
            Assert.Equal(3, bytes[2] & 0x0F, "14 km need 1/2 m steps horizontally");
            Assert.Equal(0, bytes[2] >> 4, "12.5 m of height difference keep 1/16 m");
            ToolPreview back = RoundTrip(wide);
            Assert.Equal(101.3125f, back.PointAt(0, 0).Y, "on the 1/16 m grid: exact");
            Assert.Equal(113.8125f, back.PointAt(1, 0).Y);
            Assert.Near(7100f, back.PointAt(1, 0).X, ToolPreviewCodec.Quantum(3) / 2);

            // the full height range of a map (terrain heights reach about 4 km) and a deep tunnel: 1/8 m
            var tall = new ToolPreview();
            tall.AddCircle(ToolPreviewFlags.None, P(0f, 0f, 0f), 10f);
            tall.AddCircle(ToolPreviewFlags.Elevated, P(10f, 4000f, 10f), 10f);
            tall.AddCircle(ToolPreviewFlags.Underground, P(20f, -300f, 20f), 10f);
            bytes = Encode(tall);
            Assert.Equal(0, bytes[2] & 0x0F, "a small footprint keeps 1/16 m horizontally");
            Assert.Equal(1, bytes[2] >> 4, "4 km of height: 1/8 m");
            back = RoundTrip(tall);
            Assert.Near(4000f, back.PointAt(1, 0).Y, ToolPreviewCodec.Quantum(1) / 2);
            Assert.Near(-300f, back.PointAt(2, 0).Y, ToolPreviewCodec.Quantum(1) / 2);

            // every elevation step of the net tool (1.25 m at the finest) stays distinct at any horizontal extent
            var steps = new ToolPreview();
            for (int i = 0; i < 40; i++) steps.AddCircle(ToolPreviewFlags.Elevated, P(i * 350f - 7000f, 200f + i * 1.25f, 0f), 4f);
            back = RoundTrip(steps);
            for (int i = 1; i < 40; i++) Assert.Near(1.25f, back.PointAt(i, 0).Y - back.PointAt(i - 1, 0).Y, 1e-3, "step " + i);
        }

        [Test]
        public void TheNameTagShowsTheToolElevation()
        {
            Assert.Equal("+10 m", PresenceToolKind.ElevationLabel(10f));
            Assert.Equal("-8 m", PresenceToolKind.ElevationLabel(-8f));
            Assert.Equal("+2.5 m", PresenceToolKind.ElevationLabel(2.5f));
            Assert.Equal("+1.25 m", PresenceToolKind.ElevationLabel(1.25f));
            Assert.Equal("-0.06 m", PresenceToolKind.ElevationLabel(-0.0625f));
            Assert.Equal(null, PresenceToolKind.ElevationLabel(0f), "on the ground: no label");
            Assert.Equal(null, PresenceToolKind.ElevationLabel(0.01f));
            Assert.Equal(null, PresenceToolKind.ElevationLabel(float.NaN));
            Assert.Equal(0, PresenceToolKind.ElevationKey(float.PositiveInfinity));
            Assert.Equal(160, PresenceToolKind.ElevationKey(10f), "the key is the wire step");
            var p = Elevated();
            p.Elevation = -12.5f;
            Assert.Equal("-12.5 m", PresenceToolKind.ElevationLabel(RoundTrip(p).Elevation), "after the wire");
        }

        [Test]
        public void TheWorstCaseWithHeightsAndElevationIsStillOneDatagram()
        {
            var random = new DeterministicRandom(77);
            var p = new ToolPreview { Elevation = -ToolPreviewCodec.MaxElevation };
            const ToolPreviewFlags all = ToolPreviewFlags.Elevated | ToolPreviewFlags.Underground | ToolPreviewFlags.Error | ToolPreviewFlags.Delete | ToolPreviewFlags.Secondary;
            for (int i = 0; i < ToolPreview.MaxShapes; i++)
            {
                PreviewPoint a = P((float)random.NextDouble() * 14000f - 7000f, (float)random.NextDouble() * 4600f - 500f, (float)random.NextDouble() * 14000f - 7000f);
                p.AddCurve(all, a, a, a, a, 4000f);
            }
            var preview = new ByteWriter();
            int length = ToolPreviewCodec.Encode(p, preview);
            Assert.True(length > 0 && length <= PresenceMessage.MaxPreviewBytes, "preview of " + length + " bytes");
            var back = new ToolPreview();
            Assert.True(ToolPreviewCodec.Decode(preview.Buffer, 0, length, back));
            Assert.True(back.Truncated, "128 curves do not fit: the tail is dropped");
            Assert.Equal(-ToolPreviewCodec.MaxElevation, back.Elevation);
            Assert.Equal(all, back.FlagsAt(0));
            var m = new PresenceMessage
            {
                From = new PeerId(255), CursorX = 1e30f, ToolKind = 255, Prefab = new PrefabRank(int.MaxValue), Color = uint.MaxValue,
                Preview = preview.Buffer, PreviewLength = length, BrushRadius = 1e30f
            };
            var body = new ByteWriter();
            PresenceMessage.Write(body, m);
            foreach (EnvelopeCodec codec in new[] { new EnvelopeCodec(null), new EnvelopeCodec(new ManagedLz4Compressor()) })
            {
                var envelope = new ByteWriter();
                codec.Encode(MessageKind.Presence, body.Buffer, 0, body.Length, envelope);
                Assert.True(envelope.Length <= Limits.MaxDatagramBytes, "envelope of " + envelope.Length + " bytes");
            }
        }

        [Test]
        public void MalformedPreviewsAreRejected()
        {
            byte[] good = Encode(Sample());
            int shapeStart = 3 + 12 + 1; // header, count of 6 as one byte

            Reject(Patch(good, 1, 0x04), "unknown header flag");
            Reject(Patch(good, 2, 5), "quantisation shift above 4");
            Reject(Patch(good, 2, 0x50), "vertical quantisation shift above 4");
            Reject(Patch(good, shapeStart, 9), "unknown shape kind");
            Reject(Patch(good, shapeStart + 1, 0x40), "unknown shape flag");
            byte[] zeroElevation = Concat(Concat(Cut(good, 15), new byte[] { 0, 0 }), Tail(good, 15)); // after the anchor
            zeroElevation[1] = 0x02;
            Reject(zeroElevation, "a tool elevation of 0 is never written");
            zeroElevation[15] = 0x10; // little-endian 16 steps
            var elevated = new ToolPreview();
            Assert.True(ToolPreviewCodec.Decode(zeroElevation, 0, zeroElevation.Length, elevated), "the same bytes with 1 m decode");
            Assert.Equal(1f, elevated.Elevation);
            Reject(Concat(good, new byte[] { 0 }), "trailing byte");
            Reject(Cut(good, good.Length - 1), "cut short");
            var nan = (byte[])good.Clone();
            nan[3] = 0x00; nan[4] = 0x00; nan[5] = 0xC0; nan[6] = 0x7F;
            Reject(nan, "NaN anchor");

            var p = new ToolPreview();
            p.BeginPolyline(ToolPreviewFlags.None, 1f);
            p.AddPolylinePoint(P(0, 0, 0));
            p.AddPolylinePoint(P(1, 0, 0));
            p.EndPolyline();
            byte[] line = Encode(p);
            Reject(Patch(line, shapeStart + 2, 1), "a polyline of one point");
            Reject(Patch(line, shapeStart + 2, 65), "a polyline of 65 points");

            var z = new ToolPreview();
            z.AddZoneBlock(ToolPreviewFlags.None, P(0, 0, 0), 0f, 2, 2, 0xFUL);
            byte[] zone = Encode(z);
            Reject(Patch(zone, zone.Length - 1, 0x1F), "cell bits beyond the block");
            Reject(Patch(zone, zone.Length - 2, 0xFF), "a block of 16 x 16 cells");

            var oversized = new byte[ToolPreviewCodec.MaxBytes + 1];
            oversized[0] = ToolPreviewCodec.Version;
            Reject(oversized, "above the limit");

            var count = (byte[])good.Clone();
            count[15] = 0x7F; // 127 shapes declared, far more than the bytes hold
            Reject(count, "count overrun");
        }

        [Test]
        public void FuzzedPreviewsOnlyEverThrowDecodeException()
        {
            var seeds = new List<byte[]> { Encode(Sample()) };
            var big = new ToolPreview();
            for (int i = 0; i < 30; i++) big.AddZoneBlock(ToolPreviewFlags.None, P(i * 8, 0, 0), i, 10, 6, (ulong)i * 0x9E3779B97F4AUL);
            seeds.Add(Encode(big));
            var random = new DeterministicRandom(4242);
            var into = new ToolPreview();
            int accepted = 0;
            for (int n = 0; n < 6000; n++)
            {
                byte[] seed = seeds[n % seeds.Count];
                var data = (byte[])seed.Clone();
                int flips = 1 + random.Next(4);
                for (int f = 0; f < flips; f++) data[random.Next(data.Length)] = (byte)random.Next(256);
                int length = random.Next(4) == 0 ? random.Next(data.Length + 1) : data.Length;
                try
                {
                    if (ToolPreviewCodec.Decode(data, 0, length, into)) accepted++;
                    Assert.True(into.Count <= ToolPreview.MaxShapes && into.FloatCount <= ToolPreview.MaxFloats);
                }
                catch (DecodeException)
                {
                    Assert.True(into.IsEmpty, "a rejected preview leaves nothing behind");
                }
            }
            Assert.True(accepted > 0);
        }

        [Test]
        public void EncodingDecodingAndSmoothingAllocateNothing()
        {
            ToolPreview p = Elevate(Sample());
            p.Elevation = 7.5f;
            var w = new ByteWriter(2048);
            var back = new ToolPreview();
            var shown = new ToolPreview();
            for (int warm = 0; warm < 3; warm++) Cycle(p, w, back, shown);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) Cycle(p, w, back, shown);
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before, "steady state allocates nothing");
        }

        private static void Cycle(ToolPreview p, ByteWriter w, ToolPreview back, ToolPreview shown)
        {
            w.Reset();
            int length = ToolPreviewCodec.Encode(p, w);
            ToolPreviewCodec.Decode(w.Buffer, 0, length, back);
            ToolPreviewSmoother.Step(shown, back, 0.3f);
        }

        // ================================================================== smoothing

        [Test]
        public void SmoothingGlidesWhileTheLayoutHoldsAndSnapsOtherwise()
        {
            var shown = new ToolPreview();
            var target = new ToolPreview();
            target.AddRect(ToolPreviewFlags.None, P(0, 0, 0), 10, 20, 0.1f);
            Assert.False(ToolPreviewSmoother.Step(shown, target, 0.5f), "first keyframe snaps");
            Assert.Equal(0f, shown.Data[0]);

            target.Clear();
            target.AddRect(ToolPreviewFlags.Error, P(10, 0, 0), 10, 20, (float)(2 * Math.PI) - 0.1f);
            Assert.True(ToolPreviewSmoother.Step(shown, target, 0.5f));
            Assert.Near(5f, shown.Data[0], 1e-4);
            Assert.Near(0f, shown.Data[5], 1e-4, "the angle takes the short way through 0");
            Assert.Equal(ToolPreviewFlags.Error, shown.FlagsAt(0), "flags follow the keyframe at once");

            target.Clear();
            target.AddRect(ToolPreviewFlags.None, P(10 + ToolPreviewSmoother.SnapDistance * 2, 0, 0), 10, 20, 0f);
            Assert.False(ToolPreviewSmoother.Step(shown, target, 0.5f), "a jump snaps");
            Assert.Equal(target.Data[0], shown.Data[0]);

            target.AddCircle(ToolPreviewFlags.None, P(0, 0, 0), 5f);
            Assert.False(ToolPreviewSmoother.Step(shown, target, 0.5f), "another layout snaps");
            Assert.Equal(2, shown.Count);

            var zone = new ToolPreview();
            zone.AddZoneBlock(ToolPreviewFlags.None, P(0, 0, 0), 0f, 4, 2, 0x1UL);
            var zoneShown = new ToolPreview();
            ToolPreviewSmoother.Step(zoneShown, zone, 0.5f);
            zone.Clear();
            zone.AddZoneBlock(ToolPreviewFlags.None, P(2, 0, 0), 0f, 4, 2, 0xFFUL);
            ToolPreviewSmoother.Step(zoneShown, zone, 0.5f);
            Assert.Equal(0xFFUL, zoneShown.BitsAt(0), "painted cells follow at once");
            Assert.Near(1f, zoneShown.Data[0], 1e-4);

            target.Clear();
            Assert.False(ToolPreviewSmoother.Step(shown, target, 0.5f));
            Assert.True(shown.IsEmpty, "an empty keyframe removes the preview at once");
        }

        [Test]
        public void SmoothingInterpolatesHeights()
        {
            var shown = new ToolPreview();
            var target = new ToolPreview();
            target.AddCurve(ToolPreviewFlags.Elevated, P(0, 10, 0), P(10, 10, 0), P(20, 10, 0), P(30, 10, 0), 8f);
            target.AddRect(ToolPreviewFlags.Elevated, P(50, 30, 0), 8, 8, 0f);
            target.Elevation = 10f;
            ToolPreviewSmoother.Step(shown, target, 0.5f);

            target.Clear();
            target.AddCurve(ToolPreviewFlags.Elevated, P(0, 20, 0), P(10, 20, 0), P(20, 20, 0), P(30, 20, 0), 8f); // one elevation step up
            target.AddRect(ToolPreviewFlags.Underground, P(50, 10, 0), 8, 8, 0f);
            target.Elevation = 20f;
            Assert.True(ToolPreviewSmoother.Step(shown, target, 0.5f), "a height change glides");
            for (int k = 0; k < 4; k++) Assert.Near(15f, shown.PointAt(0, k).Y, 1e-4, "half way up, point " + k);
            Assert.Near(20f, shown.PointAt(1, 0).Y, 1e-4, "the object moves down half way");
            Assert.Equal(ToolPreviewFlags.Underground, shown.FlagsAt(1), "the elevation flags follow at once");
            Assert.Equal(20f, shown.Elevation, "the tool elevation follows at once");
            ToolPreviewSmoother.Step(shown, target, 0.5f);
            Assert.Near(17.5f, shown.PointAt(0, 1).Y, 1e-4);

            target.Clear();
            target.AddCurve(ToolPreviewFlags.Elevated, P(0, 20 + ToolPreviewSmoother.SnapDistance * 2, 0), P(10, 20, 0), P(20, 20, 0), P(30, 20, 0), 8f);
            target.AddRect(ToolPreviewFlags.None, P(50, 10, 0), 8, 8, 0f);
            Assert.False(ToolPreviewSmoother.Step(shown, target, 0.5f), "a vertical jump snaps");
            Assert.Equal(0f, shown.Elevation, "and takes the elevation of the keyframe");
        }

        // ================================================================== throttle, table, relay

        [Test]
        public void APreviewChangeIsSentAtOnceAndRepeated()
        {
            var t = new PresenceThrottle();
            Assert.True(t.Due(0));
            Assert.True(t.ShouldSend(0, 0, 0, 0, 2, 5, 0, 0));
            Assert.False(t.Due(0.05));
            int sent = 0;
            for (int i = 1; i <= 54; i++) // 0.9 s: before the keepalive
            {
                if (t.ShouldSend(i / 60.0, 0, 0, 0, 2, 5, 0, 0)) sent++;
            }
            Assert.Equal(2, sent, "the first state is repeated twice, then the keepalive waits");

            Assert.True(t.ShouldSend(2.0, 0, 0, 0, 2, 5, 0, 77), "a new preview goes out at once");
            sent = 0;
            var times = new List<double>();
            for (int i = 1; i <= 50; i++)
            {
                double now = 2.0 + i / 60.0;
                if (t.ShouldSend(now, 0, 0, 0, 2, 5, 0, 77))
                {
                    sent++;
                    times.Add(now);
                }
            }
            Assert.Equal(2, sent, "two copies of an unchanged preview");
            Assert.True(times[0] - 2.0 >= t.RepeatInterval - 1e-9);

            Assert.True(t.ShouldSend(3.0, 0, 0, 0, 2, 5, 0, 0), "the cleared preview (apply, cancel) goes out at once");
        }

        [Test]
        public void TheTableDecodesPreviewsAndSurvivesBadOnes()
        {
            var table = new PresenceTable();
            byte[] bytes = Encode(Sample());
            var m = new PresenceMessage { From = new PeerId(3), CursorX = 1, ToolKind = PresenceToolKind.Pack(PresenceTool.Net, true), Preview = bytes, PreviewLength = bytes.Length };
            table.Update(m, 1, PeerId.Host);
            PresenceTable.Entry e = table.Find(new PeerId(3));
            Assert.Equal(6, e.Preview.Count);

            var broken = (byte[])bytes.Clone();
            broken[16] = 99; // unknown shape kind
            m.Preview = broken;
            m.CursorX = 2;
            table.Update(m, 2, PeerId.Host);
            Assert.Equal(1, table.BadPreviews);
            Assert.True(e.Preview.IsEmpty);
            Assert.Equal(2f, e.X, "the cursor still updates");

            m.Preview = bytes;
            table.Update(m, 3, PeerId.Host);
            Assert.Equal(6, e.Preview.Count);
            m.PreviewLength = 0;
            table.Update(m, 4, PeerId.Host);
            Assert.True(e.Preview.IsEmpty, "no preview bytes: the player stopped building");
        }

        private static PresenceMessage WithPreview(byte[] bytes)
        {
            return new PresenceMessage { CursorX = 5, ToolKind = PresenceToolKind.Pack(PresenceTool.Net, true), Preview = bytes, PreviewLength = bytes.Length };
        }

        [Test]
        public void PreviewsAreRelayedButNotAViewers()
        {
            var rig = new SessionRig();
            var (a, _, _) = rig.AddClient("Anna");
            var (b, _, _) = rig.AddClient("Ben");
            rig.Run(400);
            var bGot = new List<PresenceMessage>();
            b.PresenceReceived += bGot.Add;
            byte[] bytes = Encode(Sample());

            Assert.True(a.SendPresence(WithPreview(bytes)));
            rig.Run(3);
            Assert.Equal(1, bGot.Count);
            Assert.SequenceEqual(bytes, bGot[0].Preview, "the preview is relayed byte for byte");

            Assert.True(rig.Host.SetRole(a.LocalPeer, SessionRole.Viewer));
            rig.Run(10);
            rig.Clock.Advance(1);
            Assert.True(a.SendPresence(WithPreview(bytes)));
            rig.Run(3);
            Assert.Equal(2, bGot.Count);
            Assert.Equal(0, bGot[1].PreviewLength, "a viewer's preview is dropped by the host");
            Assert.Equal(5f, bGot[1].CursorX, "the viewer's cursor still shows");
        }

        // ================================================================== helpers

        private static void Reject(byte[] data, string why)
        {
            var into = Sample();
            Assert.Throws<DecodeException>(() => ToolPreviewCodec.Decode(data, 0, data.Length, into), why);
            Assert.True(into.IsEmpty, why + ": cleared");
        }

        private static byte[] Patch(byte[] data, int index, byte value)
        {
            var copy = (byte[])data.Clone();
            copy[index] = value;
            return copy;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Array.Copy(a, r, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private static byte[] Tail(byte[] data, int from)
        {
            var r = new byte[data.Length - from];
            Array.Copy(data, from, r, 0, r.Length);
            return r;
        }

        private static byte[] Cut(byte[] data, int length)
        {
            var r = new byte[length];
            Array.Copy(data, r, length);
            return r;
        }
    }
}
