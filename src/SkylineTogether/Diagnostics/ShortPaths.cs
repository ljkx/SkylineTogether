using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// The 8.3 short form of a folder (<c>LEONAR~1</c> for "Leonardo Smith") through Windows' <c>GetShortPathNameW</c>, for the path
    /// redactor (<see cref="Core.Diagnostics.PathRedactor.AddShortForms"/>): TEMP and the paths in exception messages can
    /// name the profile folder that way, also as a hashed short name that no pattern predicts.
    /// </summary>
    internal static class ShortPaths
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);

        /// <summary>Adds the short forms of the profile and AppData folders to the redactor; never throws.</summary>
        public static void Install()
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
                Core.Diagnostics.PathRedactor.AddShortForms(Of);
            }
            catch (Exception)
            {
                // no short names: the redactor keeps the long forms and the pattern of the short ones
            }
        }

        /// <summary>The short form of an existing path, or null (no short form, not Windows, or an error).</summary>
        public static string Of(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var buffer = new StringBuilder(path.Length + 16);
                uint length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
                if (length > buffer.Capacity)
                {
                    buffer = new StringBuilder((int)length + 1);
                    length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
                }
                return length == 0 || length > buffer.Capacity ? null : buffer.ToString(0, (int)length);
            }
            catch (Exception)
            {
                return null; // DllNotFoundException, EntryPointNotFoundException on other platforms
            }
        }
    }
}
