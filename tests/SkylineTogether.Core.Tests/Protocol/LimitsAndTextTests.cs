using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// The size limits (ARCHITECTURE.md#fragmentation-and-compression, #security-model) are consistent with each other and with the largest message of
    /// every kind, and the free-text rules strip exactly the characters that must never reach the UI.
    /// </summary>
    public sealed class LimitsAndTextTests
    {
        private static int Size<T>(Action<ByteWriter, T> write, T message)
        {
            var w = new ByteWriter();
            write(w, message);
            return w.Length;
        }

        private static string Wide(int chars) => new string((char)0x4E2D, chars);   // 3 UTF-8 bytes per character

        [Test]
        public void PlanConstantsArePinned()
        {
            Assert.Equal(4, ProtocolVersion.Current); // 4: the join flags of the hello (ProtocolVersion history)
            Assert.Equal(512 * 1024, Limits.MaxMessageBytes);
            Assert.Equal(480 * 1024, Limits.FragmentThreshold);
            Assert.Equal(8 * 1024 * 1024, Limits.MaxUncompressedBytes);
            Assert.Equal(256 * 1024, Limits.MaxBulkChunkBytes);
            Assert.Equal(1200, Limits.MaxDatagramBytes);
            Assert.Equal(500, Limits.MaxChatChars);
            Assert.Equal(64, Limits.MaxNameChars);
            Assert.Equal(4 * 1024 * 1024, Limits.MaxProposalBytes);
            Assert.Equal(256, Limits.CompressThreshold);
            Assert.Equal(4, Limits.MaxPartialMessagesPerPeer);
        }

        [Test]
        public void DerivedLimitsCoverTheLargestMessagesAndEpochs()
        {
            Assert.True(Limits.FragmentThreshold < Limits.MaxMessageBytes);
            Assert.True(Fragmenter.ChunkBytes + Fragmenter.MaxFragmentHeaderBytes <= Limits.FragmentThreshold);
            // the fragment header at its largest: kind, flags, id (5), index, count, total length (4)
            Assert.True(2 + 5 + 1 + 1 + 4 <= Fragmenter.MaxFragmentHeaderBytes);
            long largestMessage = Limits.MaxUncompressedBytes + Limits.EnvelopeOverheadBytes;
            Assert.True((largestMessage + Fragmenter.ChunkBytes - 1) / Fragmenter.ChunkBytes <= Limits.MaxFragmentsPerMessage, "fragments of the largest message");
            Assert.True(2 + 5 + 1 <= Limits.EnvelopeOverheadBytes, "kind, flags, size varint and codec byte");
        }

        [Test]
        public void KnownKindsAndPerKindLimits()
        {
            for (int k = 0; k < 256; k++)
            {
                var kind = (MessageKind)k;
                Assert.Equal((k >= 1 && k <= 21) || k == 60 || (k >= 65 && k <= 67) || k == 90 || k == 91 || k == 100 || k == 110, Limits.IsKnownKind(kind), "kind " + k);
                int max = Limits.MaxBodyBytes(kind);
                Assert.True(max > 0 && max <= Limits.MaxUncompressedBytes + Limits.MaxBulkChunkBytes, "limit of kind " + k);
            }
            Assert.Equal(Limits.MaxUncompressedBytes, Limits.MaxBodyBytes(MessageKind.Epoch), "one epoch part");
            Assert.True(Limits.MaxBodyBytes(MessageKind.BlobChunk) >= Limits.MaxBulkChunkBytes, "a whole bulk chunk");
            Assert.True(Limits.MaxBodyBytes(MessageKind.Proposal) >= Limits.MaxProposalBytes, "a whole proposal part");
            Assert.True(Limits.MaxBodyBytes(MessageKind.Motion) >= Limits.MaxDatagramBytes, "a motion datagram");
        }

        [Test]
        public void TheLargestMessageOfEveryKindFitsItsLimit()
        {
            var digests = new HandshakeDigests
            {
                ProtocolVersion = int.MaxValue, ModBuild = ulong.MaxValue, GameVersion = new string('v', HandshakeCodec.MaxGameVersionBytes),
                GameFullVersion = new string('f', HandshakeCodec.MaxGameFullVersionBytes),
                Mods = ulong.MaxValue, Content = ulong.MaxValue, AssetPacks = ulong.MaxValue, ModSettings = ulong.MaxValue,
                Locale = new string('l', HandshakeCodec.MaxLocaleBytes)
            };
            var checks = new List<(MessageKind, int, string)>
            {
                (MessageKind.Hello, Size<HelloMessage>(HelloMessage.Write, new HelloMessage
                {
                    Digests = digests, SteamId = ulong.MaxValue, DisplayName = Wide(Limits.MaxNameChars), ClientNonce = new byte[HelloMessage.MaxNonceBytes],
                    CompressionMask = CompressionNegotiation.DefinedBits, JoinFlags = HelloJoinFlags.DefinedBits
                }), "Hello"),
                (MessageKind.Welcome, Size<WelcomeMessage>(WelcomeMessage.Write, new WelcomeMessage
                {
                    SessionId = uint.MaxValue, Role = SessionRole.Viewer, Permissions = Permission.All, SessionSettings = new byte[WelcomeMessage.MaxSessionSettingsBytes],
                    HostNonce = new byte[HelloMessage.MaxNonceBytes], EnabledSysEntries = new uint[WelcomeMessage.MaxSysEntries], Compression = CompressionKind.Lz4
                }), "Welcome"),
                (MessageKind.Refuse, Size<RefuseMessage>(RefuseMessage.Write, new RefuseMessage
                {
                    Reason = (RefuseReason)255, Detail = Wide(RefuseMessage.MaxDetailChars), MismatchReport = new byte[RefuseMessage.MaxMismatchReportBytes]
                }), "Refuse"),
                (MessageKind.Control, Size<ControlMessage>(ControlMessage.Write, new ControlMessage { Kind = ControlKind.LineRows, Payload = new byte[ControlMessage.MaxPayloadBytes] }), "Control"),
                (MessageKind.Chat, Size<ChatMessage>(ChatMessage.Write, new ChatMessage { From = new PeerId(255), Text = Wide(Limits.MaxChatChars), IsSystem = true }), "Chat"),
                (MessageKind.View, Size<ViewReport>(ViewReport.Write, new ViewReport
                {
                    Selected = new NetSlot(NetSlot.MaxValue), Followed = new NetSlot(NetSlot.MaxValue), PanelTarget = new NetSlot(NetSlot.MaxValue), InfomodeMask = uint.MaxValue
                }), "View"),
                (MessageKind.Presence, Size<PresenceMessage>(PresenceMessage.Write, new PresenceMessage
                {
                    Prefab = new PrefabRank(int.MaxValue), Preview = new byte[PresenceMessage.MaxPreviewBytes], PreviewLength = PresenceMessage.MaxPreviewBytes
                }), "Presence"),
                (MessageKind.Ping, Size<PingMessage>(PingMessage.Write, new PingMessage { Sequence = uint.MaxValue }), "Ping")
            };

            foreach ((MessageKind kind, int size, string name) in checks)
            {
                Assert.True(size <= Limits.MaxBodyBytes(kind), name + ": " + size + " bytes above the limit of " + Limits.MaxBodyBytes(kind));
            }

            // the largest snapshot manifest fits a control payload
            var renders = new string[SnapshotManifestCodec.MaxRenderPrefabIds];
            for (int i = 0; i < renders.Length; i++) renders[i] = new string('r', SnapshotManifestCodec.MaxPrefabIdBytes);
            var content = new string[SnapshotManifestCodec.MaxReferencedContent];
            for (int i = 0; i < content.Length; i++) content[i] = new string('c', SnapshotManifestCodec.MaxContentBytes);
            var families = new Dictionary<Family, int>();
            for (int f = 0; f <= (int)Family.Generic; f++) families[(Family)f] = int.MaxValue;
            // mod compatibility: the longest list of asset sources
            var sources = new List<AssetSourceUse>();
            for (int i = 0; i < SnapshotManifestCodec.MaxAssetSources; i++)
            {
                sources.Add(new AssetSourceUse
                {
                    Source = new AssetSource { Kind = AssetSourceKind.Package, Name = new string('n', AssetSource.MaxNameBytes), PlatformId = new string('p', AssetSource.MaxPlatformIdBytes) },
                    Prefabs = int.MaxValue,
                    Example = new string('e', AssetSourceUse.MaxExampleBytes),
                });
            }
            int manifest = Size<SnapshotManifest>(SnapshotManifestCodec.Write, new SnapshotManifest
            {
                Size = SnapshotManifestCodec.MaxSnapshotBytes, ChunkSize = Limits.MaxBulkChunkBytes, GameModeName = new string('g', SnapshotManifestCodec.MaxGameModeBytes),
                RenderPrefabIds = renders, ReferencedContent = content, FamilyCounts = families, AssetSources = sources
            });
            Assert.True(manifest <= ControlMessage.MaxPayloadBytes, "snapshot manifest of " + manifest + " bytes");
            Assert.True(ManifestDetailMessage.MaxEncodedBytes <= ControlMessage.MaxPayloadBytes);

            // and the envelope accepts each of them
            var codec = new EnvelopeCodec(null);
            var output = new ByteWriter();
            foreach ((MessageKind kind, int size, string name) in checks)
            {
                output.Reset();
                codec.Encode(kind, new byte[size], 0, size, output);
                Assert.Equal(size + 2, output.Length, name);
            }
        }

        [Test]
        public void SanitizeStripsExactlyTheDangerousCharacters()
        {
            for (int c = 0; c <= 0xFFFF; c++)
            {
                bool expected = c < 0x20 || (c >= 0x7F && c <= 0x9F) || (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069) || c == 0x2028 || c == 0x2029;
                Assert.Equal(expected, ProtocolText.IsStripped((char)c), "U+" + c.ToString("X4"));
            }
            Assert.Equal("", ProtocolText.Sanitize(null));
            Assert.Equal("", ProtocolText.Sanitize(""));
            const string clean = "Rathaus Süd 中文 \U0001F600";
            Assert.Same(clean, ProtocolText.Sanitize(clean), "a clean string is returned as is");
            string dirty = "a" + (char)0x202E + "b" + (char)0 + "c" + (char)0x2066 + (char)0x2069 + "\t\r\nd" + (char)0x85 + (char)0x2029 + "e";
            Assert.Equal("abcde", ProtocolText.Sanitize(dirty));
            Assert.Equal("", ProtocolText.Sanitize("" + (char)0x202E + (char)0x202A));
        }

        [Test]
        public void TruncateKeepsSurrogatePairsWhole()
        {
            Assert.Equal("", ProtocolText.Truncate(null, 5));
            Assert.Equal("", ProtocolText.Truncate("abc", 0));
            Assert.Equal("", ProtocolText.Truncate("abc", -1));
            const string shortText = "abc";
            Assert.Same(shortText, ProtocolText.Truncate(shortText, 3));
            Assert.Equal("ab", ProtocolText.Truncate("abc", 2));
            string pair = "ab" + "\U0001F600";                                        // a, b, high, low
            Assert.Equal("ab", ProtocolText.Truncate(pair, 3), "the pair would be split");
            Assert.Equal(pair, ProtocolText.Truncate(pair, 4));
            Assert.Equal("", ProtocolText.Truncate("\U0001F600", 1));
        }
    }
}
