using System;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>Exceptions, small models and helpers of core-contracts.</summary>
    public sealed class MiscContractTests
    {
        [Test]
        public void DecodeExceptionKeepsMessageAndInner()
        {
            var e = new DecodeException("bad varint");
            Assert.Equal("bad varint", e.Message);
            var inner = new InvalidOperationException("x");
            var wrapped = new DecodeException("outer", inner);
            Assert.Same(inner, wrapped.InnerException);
        }

        [Test]
        public void ConfigValidationExceptionFormatsFilePathAndPosition()
        {
            var full = new ConfigValidationException("gates.json", "$.entries[3].action", 12, 17, "unknown action");
            Assert.Equal("gates.json: $.entries[3].action (line 12, column 17): unknown action", full.Message);
            Assert.Equal("gates.json", full.File);
            Assert.Equal("$.entries[3].action", full.Path);
            Assert.Equal(12, full.Line);
            Assert.Equal(17, full.Column);
            Assert.Equal("unknown action", full.Problem);

            var syntax = new ConfigValidationException("policies.json", null, 3, 5, "unterminated string");
            Assert.Equal("policies.json: line 3, column 5: unterminated string", syntax.Message);

            var noPosition = new ConfigValidationException("adapters.json", "$.adapters[0].mod", "required");
            Assert.Equal("adapters.json: $.adapters[0].mod: required", noPosition.Message);
            Assert.Equal(0, noPosition.Line);

            var anonymous = new ConfigValidationException(null, null, "empty");
            Assert.Equal("<json>: empty", anonymous.Message);
        }

        [Test]
        public void PeerIdentityEqualityIgnoresTheDisplayName()
        {
            var a = new PeerIdentity(76561198000000001, "10.0.0.2:27015", "Anna");
            var b = new PeerIdentity(76561198000000001, "10.0.0.2:27015", "Anna (renamed)");
            var c = new PeerIdentity(76561198000000002, "10.0.0.2:27015", "Anna");
            Assert.True(a == b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.True(a != c);
            Assert.Contains("Anna", a.ToString());
            Assert.Equal(new PeerIdentity(0, null, null), default(PeerIdentity));
        }

        [Test]
        public void ModelDefaults()
        {
            Assert.Equal(8, new ListenOptions().MaxConnections);
            var manifest = new HandshakeManifest();
            Assert.NotNull(manifest.Mods);
            Assert.NotNull(manifest.Content);
            Assert.NotNull(manifest.FormatTagNames);
            Assert.NotNull(manifest.Settings);
            Assert.Null(manifest.PrefabIds, "filled only on a prefab digest mismatch");
            Assert.Null(manifest.DataFiles, "filled only on a data-file digest mismatch");
            Assert.NotNull(new SnapshotManifest().FamilyCounts);
            Assert.Equal("10.0.0.2:27015", new Endpoint { Host = "10.0.0.2", Port = 27015 }.ToString());
            Assert.Equal("steam:7/3", new Endpoint { SteamId = 7, VirtualPort = 3 }.ToString());
            Assert.Contains("policies.json", new DataFileDigestEntry { File = "policies.json", Digest = 0xABC, Overridden = true }.ToString());
        }

        [Test]
        public void LogExtensionsRespectLevelsAndNullLogs()
        {
            var log = new ListLog(LogLevel.Info);
            log.Trace(LogCategory.Net, "hidden");
            log.Debug(LogCategory.Net, "hidden");
            log.Info(LogCategory.Session, "joined");
            log.Warn(LogCategory.Apply, "parked", new InvalidOperationException("why"));
            log.Error(LogCategory.Config, "bad file");
            log.Critical(LogCategory.Probe, "dead");
            Assert.Equal(4, log.Lines.Count);
            Assert.Equal("Info [session] joined", log.Lines[0]);
            Assert.Equal("Warn [apply] parked | InvalidOperationException: why", log.Lines[1]);
            Assert.Equal(2, log.Count(LogLevel.Error));
            ILog nothing = null;
            nothing.Info("x", "no exception for a null log");
        }

        [Test]
        public void LogCategoriesMatchTheArchitecture()
        {
            Assert.Equal("net", LogCategory.Net);
            Assert.Equal("session", LogCategory.Session);
            Assert.Equal("capture", LogCategory.Capture);
            Assert.Equal("apply", LogCategory.Apply);
            Assert.Equal("motion", LogCategory.Motion);
            Assert.Equal("join", LogCategory.Join);
            Assert.Equal("gate", LogCategory.Gate);
            Assert.Equal("probe", LogCategory.Probe);
            Assert.Equal("ui", LogCategory.Ui);
        }
    }
}
