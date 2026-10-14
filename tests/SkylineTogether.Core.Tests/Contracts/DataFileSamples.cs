using SkylineTogether.Core.Json;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>
    /// Sample data files in the documented schema (written with single quotes; <see cref="J"/> turns them into JSON).
    /// It doubles as a schema example for the shipped locale files in <c>src/SkylineTogether/Data</c>.
    /// </summary>
    internal static class DataFileSamples
    {
        public static JsonValue J(string singleQuoted) => MiniJson.Parse(singleQuoted.Replace('\'', '"'));

        public const string Locale = @"{
  '_comment': 'English strings; the fallback for every other locale',
  'st.menu.coop': 'Co-op',
  'st.hud.sync.synced': 'Synced',
  'st.toast.rejected.conflict': 'Conflicts with the edit of {0}',
  'st.empty': ''
}";
    }
}
