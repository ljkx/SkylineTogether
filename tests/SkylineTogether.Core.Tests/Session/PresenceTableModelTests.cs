using System;
using System.Collections.Generic;
using System.Linq;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Model-based test of <see cref="PresenceTable"/> (the cursors and tool previews of the other players): random
    /// updates (with valid, empty and garbage previews), removals, departures and pruning keep it equal to a plain model,
    /// sorted by peer, free of the local player, and its version moves whenever the visible membership changes.
    /// </summary>
    public sealed class PresenceTableModelTests
    {
        [Test]
        public void ThePresenceTableAgreesWithAModel()
        {
            var preview = new ToolPreview();
            preview.AddCircle(ToolPreviewFlags.None, new PreviewPoint(1, 2, 3), 10f);
            var w = new ByteWriter();
            ToolPreviewCodec.Encode(preview, w);
            byte[] validPreview = w.ToArray();

            Prop.ForAll("presence table", 2100, 200, g =>
            {
                var table = new PresenceTable { StaleSeconds = g.Pick(1.0, 5.0) };
                var local = new PeerId((byte)g.Int(1, 6));
                var model = new Dictionary<byte, (float X, double At)>();
                double now = 0;
                int bad = 0;
                for (int step = 0; step < 200; step++)
                {
                    now += g.Double();
                    int versionBefore = table.Version;
                    int countBefore = table.Entries.Count;
                    switch (g.Int(6))
                    {
                        case 0:
                        case 1:
                        case 2:
                        {
                            var from = new PeerId((byte)g.Int(0, 8));
                            var m = new PresenceMessage { From = from, CursorX = g.Int(-1000, 1000) };
                            switch (g.Int(3))
                            {
                                case 0:
                                    m.Preview = validPreview;
                                    m.PreviewLength = validPreview.Length;
                                    break;
                                case 1:
                                    m.Preview = g.Bytes(40);
                                    m.PreviewLength = g.Int(0, m.Preview.Length + 5);   // may even claim more than it has
                                    break;
                            }
                            int badBefore = table.BadPreviews;
                            table.Update(m, now, local);
                            if (table.BadPreviews > badBefore) bad++;
                            if (from != local) model[from.Value] = (m.CursorX, now);
                            break;
                        }
                        case 3:
                        {
                            var peer = new PeerId((byte)g.Int(0, 8));
                            Assert.Equal(model.Remove(peer.Value), table.Remove(peer));
                            break;
                        }
                        case 4:
                        {
                            var present = new HashSet<PeerId>();
                            for (int k = g.Int(0, 6); k > 0; k--) present.Add(new PeerId((byte)g.Int(0, 8)));
                            table.RetainOnly(present);
                            foreach (byte id in model.Keys.ToList()) if (!present.Contains(new PeerId(id))) model.Remove(id);
                            break;
                        }
                        default:
                            table.Prune(now);
                            foreach (byte id in model.Keys.ToList()) if (now - model[id].At > table.StaleSeconds) model.Remove(id);
                            break;
                    }
                    IReadOnlyList<PresenceTable.Entry> entries = table.Entries;
                    Assert.SequenceEqual(model.Keys.OrderBy(k => k), entries.Select(e => e.Peer.Value), "peers (sorted)");
                    foreach (PresenceTable.Entry e in entries)
                    {
                        Assert.Equal(model[e.Peer.Value].X, e.X, "latest cursor of " + e.Peer);
                        Assert.Same(e, table.Find(e.Peer));
                    }
                    Assert.Null(table.Find(local), "never the local player");
                    if (entries.Count != countBefore) Assert.True(table.Version > versionBefore, "membership changes bump the version");
                }
                Assert.True(table.BadPreviews == bad);
            });
        }
    }
}
