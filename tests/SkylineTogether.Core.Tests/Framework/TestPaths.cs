using System;
using System.IO;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>Locates files of the repository (for tests that validate shipped data files when they exist).</summary>
    public static class TestPaths
    {
        private static readonly Lazy<string> RepoRootLazy = new Lazy<string>(FindRepoRoot);

        /// <summary>The folder that contains <c>SkylineTogether.sln</c>, or null when the tests run outside the repository.</summary>
        public static string RepoRoot => RepoRootLazy.Value;

        /// <summary>Resolves a repository-relative path (forward slashes); false when the repository or the file is absent.</summary>
        public static bool TryGetRepoFile(string relativePath, out string fullPath)
        {
            fullPath = null;
            string root = RepoRoot;
            if (root == null) return false;
            string candidate = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(candidate)) return false;
            fullPath = candidate;
            return true;
        }

        private static string FindRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "SkylineTogether.sln"))) return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            return null;
        }
    }
}
