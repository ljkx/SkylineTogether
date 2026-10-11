using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests
{
    /// <summary>Entry point: runs every discovered test (see <see cref="TestRunner"/> for the arguments).</summary>
    public static class Program
    {
        public static int Main(string[] args) => TestRunner.Run(typeof(Program).Assembly, args);
    }
}
