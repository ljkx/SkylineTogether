using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Contracts.DataFileSamples;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>gates.json parsing (ARCH §6.3): entries, the session-load set and tool-scoped gates.</summary>
    public sealed class GateTableTests
    {
        private static ConfigValidationException Invalid(string singleQuoted)
        {
            return Assert.Throws<ConfigValidationException>(() => GateTable.Parse(J(singleQuoted)), "expected gates.json to be rejected: " + singleQuoted);
        }

        [Test]
        public void ParsesTheSample()
        {
            GateTable table = GateTable.Parse(J(Gates));
            Assert.Equal(1, table.Version);
            Assert.Equal(4, table.Entries.Count);
            GateEntry xp = table.Entries[0];
            Assert.Equal("Game.Simulation.XPSystem", xp.TypeName);
            Assert.Equal("ModificationEnd", xp.Phase);
            Assert.True(xp.Critical);
            Assert.Equal(GateAction.Off, xp.Action);
            Assert.Equal("rewards must not apply twice", xp.Reason);
            Assert.Equal(GateAction.OffAndFeed, table.Entries[1].Action);
            Assert.False(table.Entries[1].Critical);
            Assert.Null(table.Entries[2].Reason);
            Assert.Equal(GateAction.Keep, table.Entries[3].Action, "enum names match case-insensitively");
            Assert.Equal(string.Empty, table.Entries[3].Phase);

            Assert.Equal(3, table.SessionLoadEntries.Count);
            foreach (GateEntry entry in table.SessionLoadEntries) Assert.Equal(GateAction.OffDuringSessionLoad, entry.Action);
            Assert.Equal("Game.Serialization.ResetBuildOrderSystem", table.SessionLoadEntries[0].TypeName);
            Assert.True(table.SessionLoadEntries[1].Critical);

            Assert.Equal(2, table.ToolScopedEntries.Count);
            ToolScopedGateEntry route = table.ToolScopedEntries[0];
            Assert.Equal("Game.Tools.RouteToolSystem", route.ToolTypeName);
            Assert.SequenceEqual(new[] { "Game.Routes.WaypointConnectionSystem", "Game.Routes.RoutePathSystem" }, route.Systems);
            Assert.Equal(2.0, route.LingerSeconds);
            Assert.Equal("RouteGuard", route.Guard);
            Assert.Equal(ToolScopedGateEntry.DefaultLingerSeconds, table.ToolScopedEntries[1].LingerSeconds);
            Assert.Null(table.ToolScopedEntries[1].Guard);
        }

        [Test]
        public void SectionsAreOptional()
        {
            GateTable table = GateTable.Parse(J("{ 'version': 2 }"));
            Assert.Equal(2, table.Version);
            Assert.Equal(0, table.Entries.Count);
            Assert.Equal(0, table.SessionLoadEntries.Count);
            Assert.Equal(0, table.ToolScopedEntries.Count);
        }

        [Test]
        public void VersionIsRequiredAndPositive()
        {
            Assert.Contains("required member \"version\"", Invalid("{ 'entries': [] }").Message);
            Assert.Contains("outside", Invalid("{ 'version': 0 }").Message);
            Assert.Contains("integer", Invalid("{ 'version': 1.5 }").Message);
            Assert.Contains("expected a number", Invalid("{ 'version': '1' }").Message);
        }

        [Test]
        public void RejectsSchemaViolations()
        {
            Assert.Contains("expected an object", Assert.Throws<ConfigValidationException>(() => GateTable.Parse(J("[]"))).Message);
            Assert.Contains("unknown member \"entires\"", Invalid("{ 'version': 1, 'entires': [] }").Message);
            Assert.Contains("expected an array", Invalid("{ 'version': 1, 'entries': {} }").Message);
            Assert.Contains("required member \"type\"", Invalid("{ 'version': 1, 'entries': [ { 'phase': 'x' } ] }").Message);
            Assert.Contains("whitespace", Invalid("{ 'version': 1, 'entries': [ { 'type': 'Game.Simulation. XPSystem' } ] }").Message);
            Assert.Contains("malformed type name", Invalid("{ 'version': 1, 'entries': [ { 'type': 'Game..XPSystem' } ] }").Message);
            Assert.Contains("unknown GateAction \"Disable\"", Invalid("{ 'version': 1, 'entries': [ { 'type': 'A.B', 'action': 'Disable' } ] }").Message);
            Assert.Contains("unknown GateAction", Invalid("{ 'version': 1, 'entries': [ { 'type': 'A.B', 'action': '1' } ] }").Message);
            Assert.Contains("true or false", Invalid("{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critical': 'yes' } ] }").Message);
            Assert.Contains("unknown member \"critcal\"", Invalid("{ 'version': 1, 'entries': [ { 'type': 'A.B', 'critcal': true } ] }").Message);
        }

        [Test]
        public void ActionsBelongInTheirSections()
        {
            Assert.Contains("belong in \"sessionLoad\"", Invalid("{ 'version': 1, 'entries': [ { 'type': 'A.B', 'action': 'OffDuringSessionLoad' } ] }").Message);
            Assert.Contains("belong in \"toolScoped\"", Invalid("{ 'version': 1, 'entries': [ { 'type': 'A.B', 'action': 'ToolScoped' } ] }").Message);
            Assert.Contains("OffDuringSessionLoad", Invalid("{ 'version': 1, 'sessionLoad': [ { 'type': 'A.B', 'action': 'Off' } ] }").Message);
        }

        [Test]
        public void ASystemAppearsOnlyOnce()
        {
            Assert.Contains("already listed in \"entries\"", Invalid("{ 'version': 1, 'entries': [ { 'type': 'A.B' }, { 'type': 'A.B', 'action': 'Keep' } ] }").Message);
            Assert.Contains("already listed in \"entries\"", Invalid("{ 'version': 1, 'entries': [ { 'type': 'A.B' } ], 'sessionLoad': [ { 'type': 'A.B' } ] }").Message);
            Assert.Contains("already listed in \"sessionLoad\"", Invalid("{ 'version': 1, 'sessionLoad': [ { 'type': 'A.B' }, { 'type': 'A.B' } ] }").Message);
            Assert.Contains("tool-scoped and also listed in \"entries\"",
                            Invalid("{ 'version': 1, 'entries': [ { 'type': 'A.B' } ], 'toolScoped': [ { 'tool': 'T.Tool', 'systems': [ 'A.B' ] } ] }").Message);
            Assert.Contains("listed twice", Invalid("{ 'version': 1, 'toolScoped': [ { 'tool': 'T.Tool', 'systems': [ 'A.B' ] }, { 'tool': 'T.Tool', 'systems': [ 'C.D' ] } ] }").Message);
        }

        [Test]
        public void ToolScopedEntriesAreValidated()
        {
            Assert.Contains("must not be empty", Invalid("{ 'version': 1, 'toolScoped': [ { 'tool': 'T.Tool', 'systems': [] } ] }").Message);
            Assert.Contains("required member \"systems\"", Invalid("{ 'version': 1, 'toolScoped': [ { 'tool': 'T.Tool' } ] }").Message);
            Assert.Contains("outside", Invalid("{ 'version': 1, 'toolScoped': [ { 'tool': 'T.Tool', 'systems': [ 'A.B' ], 'lingerSeconds': -1 } ] }").Message);
            Assert.Contains("duplicate entry", Invalid("{ 'version': 1, 'toolScoped': [ { 'tool': 'T.Tool', 'systems': [ 'A.B', 'A.B' ] } ] }").Message);
            Assert.Contains("cannot scope itself", Invalid("{ 'version': 1, 'toolScoped': [ { 'tool': 'T.Tool', 'systems': [ 'T.Tool' ] } ] }").Message);
            Assert.Contains("identifier", Invalid("{ 'version': 1, 'toolScoped': [ { 'tool': 'T.Tool', 'systems': [ 'A.B' ], 'guard': 'Route Guard' } ] }").Message);
        }

        [Test]
        public void ErrorsNameThePathAndPosition()
        {
            string text = "{\n  'version': 1,\n  'entries': [\n    { 'type': 'A.B' },\n    { 'type': 'C.D', 'action': 'Nope' }\n  ]\n}";
            ConfigValidationException e = Invalid(text);
            Assert.Equal(GateTable.FileName, e.File);
            Assert.Equal("$.entries[1].action", e.Path);
            Assert.Equal(5, e.Line);
            Assert.Equal(32, e.Column);
            Assert.Contains("gates.json: $.entries[1].action (line 5, column 32)", e.Message);
        }

        [Test]
        public void CommentMembersAreIgnored()
        {
            GateTable table = GateTable.Parse(J("{ '_c': 1, 'version': 1, 'entries': [ { 'type': 'A.B', '_why': { 'any': 'thing' } } ] }"));
            Assert.Equal(1, table.Entries.Count);
        }
    }
}
