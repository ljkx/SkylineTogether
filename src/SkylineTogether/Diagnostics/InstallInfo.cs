using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.PSI.Common;
using Game.Modding;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Session;
using SkylineTogether.Settings;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// Where this copy of Skyline Together was loaded from and whether the game found more than one copy
    /// (docs/dev/publishing-paradox-mods.md#installed-twice). The game loads a mod from the bytes of its DLL
    /// (<c>Assembly.Load(byte[])</c>), so <see cref="Assembly.Location"/> is empty: the file is found through the game's
    /// mod manager (<see cref="ModManager.TryGetExecutableAsset(Assembly, out ExecutableAsset)"/>) and its
    /// <see cref="ExecutableAsset"/>, which works for the local Mods folder and for Paradox Mods subscriptions alike.
    /// Nothing here writes a path into the log: copies are described as "local Mods folder" or "Paradox Mods (id, version)".
    /// </summary>
    public static class InstallInfo
    {
        /// <summary>The assembly (and DLL file) name of the game layer.</summary>
        public const string AssemblyName = "SkylineTogether";

        /// <summary>The UI module's file name.</summary>
        public const string UiModuleFile = "SkylineTogether.mjs";

        /// <summary>The notification id of the "installed twice" warning.</summary>
        public const string DuplicateNotificationId = "SkylineTogether.installedTwice";

        /// <summary>One copy of the mod the game's asset database knows.</summary>
        public sealed class InstalledCopy
        {
            /// <summary>"paradox" (a Paradox Mods subscription), "local" (the local Mods folder) or "other".</summary>
            public string Source = "other";

            /// <summary>The Paradox Mods id and version, for <see cref="Source"/> "paradox".</summary>
            public string PlatformId = "";
            public string PlatformVersion = "";

            /// <summary>The build id of this copy's DLL (<see cref="Core.Session.BuildId"/>), null when it could not be read.</summary>
            public ulong? Build;

            /// <summary>Whether this is the copy the game loaded (the one running now).</summary>
            public bool Loaded;

            /// <summary>"Paradox Mods (id 12345, version 3)", "local Mods folder" or "another folder", plus the build id.</summary>
            public string Describe()
            {
                string where;
                switch (Source)
                {
                    case "paradox":
                        where = "Paradox Mods" + (string.IsNullOrEmpty(PlatformId) ? "" : " (id " + PlatformId + (string.IsNullOrEmpty(PlatformVersion) ? "" : ", version " + PlatformVersion) + ")");
                        break;
                    case "local":
                        where = "local Mods folder";
                        break;
                    default:
                        where = "another folder";
                        break;
                }
                return where + ", build " + (Build.HasValue ? Core.Session.BuildId.Format(Build.Value) : "unknown") + (Loaded ? ", loaded" : "");
            }
        }

        /// <summary>
        /// The game's asset of this assembly: the mod manager's entry for it, else any IL asset of the global database whose
        /// loaded assembly is this one. Null when neither knows it (a test host, or a game that loads mods differently).
        /// </summary>
        public static ExecutableAsset OwnAsset()
        {
            Assembly own = typeof(InstallInfo).Assembly;
            try
            {
                ModManager manager = GameManager.instance?.modManager;
                if (manager != null && manager.TryGetExecutableAsset(own, out ExecutableAsset asset) && asset != null) return asset;
            }
            catch (Exception)
            {
                // fall through to the database search
            }
            try
            {
                foreach (ExecutableAsset asset in AssetDatabase.global.GetAssets(SearchFilter<ExecutableAsset>.ByCondition(a => a.isILAssembly)))
                {
                    if (asset != null && asset.assembly == own) return asset;
                }
            }
            catch (Exception)
            {
                // no database (yet)
            }
            return null;
        }

        /// <summary>
        /// The bytes of the DLL this assembly was loaded from: the file of <see cref="OwnAsset"/> (or the asset's own read
        /// stream when the path is not a plain file), else <see cref="Assembly.Location"/> when the runtime knows one.
        /// Null when none can be read. <paramref name="how"/> names the source for the log.
        /// </summary>
        public static byte[] ReadOwnDll(out string how)
        {
            how = null;
            ExecutableAsset asset = OwnAsset();
            if (asset != null)
            {
                byte[] bytes = ReadAsset(asset);
                if (bytes != null)
                {
                    how = "mod manager";
                    return bytes;
                }
            }
            try
            {
                string location = typeof(InstallInfo).Assembly.Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                {
                    how = "assembly location";
                    return File.ReadAllBytes(location);
                }
            }
            catch (Exception)
            {
                // unreadable
            }
            return null;
        }

        private static byte[] ReadAsset(ExecutableAsset asset)
        {
            try
            {
                string path = asset.path;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                // try the database's stream below
            }
            try
            {
                using (Stream stream = asset.GetReadStream())
                {
                    if (stream == null) return null;
                    using (var copy = new MemoryStream())
                    {
                        stream.CopyTo(copy);
                        return copy.Length > 0 ? copy.ToArray() : null;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Every copy of the game layer's DLL the game's asset database knows (normally exactly one).</summary>
        public static List<InstalledCopy> Copies()
        {
            var list = new List<InstalledCopy>();
            ExecutableAsset own = OwnAsset();
            try
            {
                foreach (ExecutableAsset asset in AssetDatabase.global.GetAssets(SearchFilter<ExecutableAsset>.ByCondition(IsOurs)))
                {
                    if (asset == null) continue;
                    var copy = new InstalledCopy { Loaded = (own != null && ReferenceEquals(asset, own)) || asset.assembly == typeof(InstallInfo).Assembly };
                    try
                    {
                        SourceMeta meta = asset.GetMeta();
                        copy.PlatformId = meta.platformID ?? "";
                        copy.PlatformVersion = meta.platformVersion ?? "";
                        copy.Source = !string.IsNullOrEmpty(copy.PlatformId) || IsUnder(meta.path, "pdx_mods") ? "paradox"
                            : asset.isLocal ? "local" : "other";
                    }
                    catch (Exception)
                    {
                        copy.Source = asset.isLocal ? "local" : "other";
                    }
                    byte[] bytes = ReadAsset(asset);
                    if (bytes != null) copy.Build = Core.Session.BuildId.OfBytes(bytes);
                    list.Add(copy);
                }
            }
            catch (Exception)
            {
                // no database: the list stays empty
            }
            return list;
        }

        /// <summary>How many UI modules named <see cref="UiModuleFile"/> the game registered (each copy brings one).</summary>
        public static int UiModuleCount()
        {
            int count = 0;
            try
            {
                foreach (UIModuleAsset module in AssetDatabase.global.GetAssets(default(SearchFilter<UIModuleAsset>)))
                {
                    if (module != null && string.Equals(Path.GetFileName(module.path ?? ""), UiModuleFile, StringComparison.OrdinalIgnoreCase)) count++;
                }
            }
            catch (Exception)
            {
                return count;
            }
            return count;
        }

        private static bool IsOurs(ExecutableAsset asset)
        {
            try
            {
                return asset != null && asset.isILAssembly && string.Equals(asset.definition?.Name?.Name, AssemblyName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsUnder(string path, string folder)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (string part in path.Split('/', '\\'))
            {
                if (string.Equals(part, folder, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The loaded copy in one line for the log and the diagnostics ("Paradox Mods (id 1, version 2), build ...").</summary>
        public static string DescribeLoaded()
        {
            foreach (InstalledCopy copy in Copies())
            {
                if (copy.Loaded) return copy.Describe();
            }
            return "unknown location";
        }

        /// <summary>
        /// Logs where the mod was loaded from and warns, in the log and with a notification that stays until clicked away,
        /// when the game found Skyline Together more than once (typically the local Mods folder and a Paradox Mods
        /// subscription). The game loads only one of the DLLs (the local one first) but registers every copy's UI module,
        /// and two copies of different builds are a classic source of crashes and of "different build" refusals.
        /// </summary>
        public static void CheckAtStartup(ILog log)
        {
            List<InstalledCopy> copies = Copies();
            int ui = UiModuleCount();
            InstalledCopy loaded = copies.Find(c => c.Loaded);
            log.Info(LogCategory.Session, "loaded from " + (loaded != null ? loaded.Describe() : "an unknown location") + "; build id from " + GameInfoProvider.BuildSourceDetail);
            if (copies.Count <= 1 && ui <= 1) return;

            var lines = new List<string>();
            foreach (InstalledCopy c in copies) lines.Add(c.Describe());
            log.Warn(LogCategory.Session, "Skyline Together is installed more than once (" + copies.Count.ToString(CultureInfo.InvariantCulture) + " DLLs, "
                + ui.ToString(CultureInfo.InvariantCulture) + " UI modules): " + string.Join("; ", lines)
                + ". Keep one copy: delete the Mods\\SkylineTogether folder or unsubscribe on Paradox Mods, then restart the game.");
            try
            {
                string text = LocaleSource.Resolve("st.toast.installedTwice", "COPIES", string.Join("; ", lines));
                Game.PSI.NotificationSystem.Push(DuplicateNotificationId, title: Game.UI.Localization.LocalizedString.Value(LocaleSource.Resolve("st.toast.title")),
                    text: Game.UI.Localization.LocalizedString.Value(text), progressState: ProgressState.Warning,
                    onClicked: () => Game.PSI.NotificationSystem.Pop(DuplicateNotificationId));
            }
            catch (Exception ex)
            {
                log.Warn(LogCategory.Session, "could not show the installed-twice notification", ex);
            }
        }
    }
}
