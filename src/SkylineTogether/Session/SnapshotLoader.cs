using System;
using System.Collections.Generic;
using System.IO;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.PSI.Common;
using Colossal.Serialization.Entities;
using Game;
using Game.Assets;
using Game.City;
using Game.Prefabs.Modes;
using Game.SceneFlow;
using Game.Settings;
using Game.Simulation;
using Game.UI;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using Unity.Entities;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Session
{
    /// <summary>
    /// Client side of joining (R12 §4.2-4.3): receives the host's snapshot into a <see cref="SaveGameData"/> asset of a
    /// transient, file-backed asset database and loads it like a save game, with the host's game mode, the city options
    /// from the save (not the client's), achievements off and the simulation paused after loading. The client's own
    /// settings files are never written.
    /// </summary>
    public sealed class SnapshotLoader : IWorldLoader
    {
        private readonly ILog _log;
        private readonly MainThreadQueue _main;
        private readonly string _root;
        private ILocalAssetDatabase _db;
        private SaveGameData _data;
        private string _folder;

        public SnapshotLoader(MainThreadQueue main, ILog log)
        {
            _main = main;
            _log = log;
            _root = Path.Combine(Path.GetTempPath(), "SkylineTogether", "join");
        }

        public bool IsLoading { get; private set; }

        /// <summary>polish: the joiner's achievements flag from before the first join load (achievements are off in
        /// the host's city).</summary>
        private readonly SuspendedFlag _achievements = new SuspendedFlag();

        /// <summary>
        /// polish: gives the achievements flag back as it was before the join (session over, offline copy, main menu).
        /// With <paramref name="forget"/> the held value is dropped instead: another city is loading, and the vanilla
        /// main menu set the flag for that city already (<c>MenuUISystem</c>), which must not be overwritten.
        /// </summary>
        public void RestoreAchievements(bool forget = false)
        {
            if (forget)
            {
                _achievements.Forget();
                return;
            }
            if (!_achievements.TryRestore(out bool value)) return;
            try
            {
                PlatformManager.instance.achievementsEnabled = value;
                _log.Debug(LogCategory.Join, "achievements restored (" + value + ")");
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Join, "could not restore achievements: " + ex.Message);
            }
        }

        /// <summary>The simulation's load progress (0..1) while loading.</summary>
        public float Progress
        {
            get
            {
                if (!IsLoading) return 0f;
                SimulationSystem sim = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<SimulationSystem>();
                return sim != null ? sim.loadingProgress : 0f;
            }
        }

        public Stream OpenBlobTarget(SnapshotManifest manifest)
        {
            Dispose();
            _disposeRequested = false;
            _folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _db = AssetDatabase.GetTransient(0L, _folder);
            _data = _db.AddAsset<SaveGameData>("SkylineTogetherJoin");
            // polish: the host's bundled render prefabs arrive in front of the save data (JoinAssetTransfer)
            _splitter = new JoinBlobSplitter(_data.GetWriteStream());
            return _splitter;
        }

        private JoinBlobSplitter _splitter;
        private bool _disposeRequested;

        public void BeginLoad(SnapshotManifest manifest, IReadOnlyList<SettingEntry> pushedSettings, Action<LoadOutcome> onDone)
        {
            if (_data == null) throw new InvalidOperationException("no downloaded city to load");
            List<JoinAssetEntry> bundle = _splitter?.Bundle;
            bool hasIds = manifest.RenderPrefabIds != null && manifest.RenderPrefabIds.Length > 0;
            if ((bundle == null || bundle.Count == 0) && !hasIds)
            {
                StartLoad(manifest, onDone);
                return;
            }
            if (bundle == null || bundle.Count == 0)
            {
                // loadspeed: nothing to import (the usual case): check the render prefabs now and start the load at once,
                // instead of a detour over the save/load queue and the next frame
                if (!RenderPrefabsMissing(manifest, onDone)) StartLoad(manifest, onDone);
                return;
            }
            // polish: add the host's bundled render prefabs, then check that every render prefab resolves, before the
            // load (the asset database registration is asynchronous)
            IsLoading = true;
            string folder = Path.Combine(_root, "assets-" + Guid.NewGuid().ToString("N"));
            TaskManager.instance.EnqueueTask("SaveLoadGame", async () =>
            {
                try
                {
                    await JoinAssetTransfer.Import(bundle, folder, _log);
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Join, "join assets: import failed", ex);
                }
                _main.Post(() =>
                {
                    IsLoading = false;
                    if (_disposeRequested)
                    {
                        // the session ended while the assets were added: no load
                        _disposeRequested = false;
                        Dispose();
                        return;
                    }
                    if (RenderPrefabsMissing(manifest, onDone)) return;
                    StartLoad(manifest, onDone);
                });
            }, 1);
        }

        /// <summary>
        /// True (and the load reported as failed) when a render prefab of the manifest does not resolve here: the joiner
        /// is refused with a clear text instead of loading <c>[Missing]</c> prefabs.
        /// </summary>
        private bool RenderPrefabsMissing(SnapshotManifest manifest, Action<LoadOutcome> onDone)
        {
            List<string> missing;
            try
            {
                missing = JoinAssetTransfer.MissingRenderPrefabs(manifest.RenderPrefabIds);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Join, "join assets: render prefab check failed; loading anyway", ex);
                return false;
            }
            if (missing.Count == 0) return false;
            onDone(new LoadOutcome { Success = false, FailureKey = "content", FailureDetail = Settings.LocaleSource.Resolve("st.toast.join.renderPrefabs", "NAMES", string.Join(", ", missing)) });
            return true;
        }

        private void StartLoad(SnapshotManifest manifest, Action<LoadOutcome> onDone)
        {
            if (_data.GetAsyncReadDescriptor() == AsyncReadDescriptor.Invalid)
            {
                onDone(new LoadOutcome { Success = false, FailureDetail = "the downloaded city file is missing" });
                return;
            }
            GameManager game = GameManager.instance;
            if (game.isGameLoading)
            {
                // review fix: a second load on top of a running one would fail obscurely (or not at all)
                onDone(new LoadOutcome { Success = false, FailureKey = "busy", FailureDetail = "your game is still loading another city. Wait until it has finished, then join again" });
                return;
            }
            if (manifest.ReferencedContent != null && manifest.ReferencedContent.Length > 0 && !game.ArePrerequisitesMet(manifest.ReferencedContent))
            {
                onDone(new LoadOutcome { Success = false, FailureKey = "content", FailureDetail = "the host's city uses DLC or content packs you do not have (" + string.Join(", ", manifest.ReferencedContent) + ")" });
                return;
            }

            World world = World.DefaultGameObjectInjectionWorld;
            CityConfigurationSystem city = world.GetExistingSystemManaged<CityConfigurationSystem>();
            GameModeSystem modes = world.GetExistingSystemManaged<GameModeSystem>();
            MapMetadataSystem map = world.GetExistingSystemManaged<MapMetadataSystem>();
            city.overrideLoadedOptions = false;
            city.overrideThemeName = null;
            if (!string.IsNullOrEmpty(manifest.GameModeName)) modes.overrideMode = manifest.GameModeName;
            map.mapName = "Skyline Together";
            try
            {
                // polish: the player's own value is kept and given back when the session ends (RestoreAchievements)
                PlatformManager.instance.achievementsEnabled = _achievements.Suspend(PlatformManager.instance.achievementsEnabled);
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Join, "could not turn achievements off: " + ex.Message);
            }
            GameplaySettings gameplay = SharedSettings.instance.gameplay;
            bool pausedBefore = gameplay.pausedAfterLoading;
            gameplay.pausedAfterLoading = true; // in memory only: the first simulated frame must not run ahead of the host
            IsLoading = true;
            SaveGameData data = _data;
            ILocalAssetDatabase db = _db;
            string folder = _folder;
            TaskManager.instance.EnqueueTask("SaveLoadGame", async () =>
            {
                bool ok = false;
                string error = null;
                try
                {
                    ok = await game.Load(GameMode.Game, Purpose.LoadGame, data);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _log.Error(LogCategory.Join, "loading the host's city failed", ex);
                }
                _main.Post(() =>
                {
                    gameplay.pausedAfterLoading = pausedBefore;
                    IsLoading = false;
                    // review fix (robust branch): release this load's own database; if a newer download replaced the
                    // fields meanwhile (a rejoin attempt), Dispose() would have released the newer one instead
                    if (ReferenceEquals(_data, data)) Dispose();
                    else Release(db, folder);
                    try
                    {
                        onDone(new LoadOutcome { Success = ok, FailureDetail = ok ? null : error ?? "the game could not load the city (see the game log)", SlotCoverage = 1f });
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Guard.Report(Diagnostics.Features.Session, "join load completion", ex);
                    }
                });
            }, 1);
        }

        /// <summary>
        /// Releases the transient database and deletes its folder. While the game is still loading from it (the
        /// session ended mid-load), this waits for the load to finish: pulling the asset away under a running
        /// <c>GameManager.Load</c> could break it.
        /// </summary>
        public void Dispose()
        {
            if (IsLoading)
            {
                _disposeRequested = true; // polish: honoured after the asset import (the load's completion disposes anyway)
                // the load's completion callback disposes
                return;
            }
            ILocalAssetDatabase db = _db;
            string folder = _folder;
            _db = null;
            _data = null;
            _folder = null;
            _splitter = null;
            Release(db, folder);
        }

        private void Release(ILocalAssetDatabase db, string folder)
        {
            try
            {
                db?.Dispose();
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Join, "disposing the join database: " + ex.Message);
            }
            if (folder == null) return;
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex)
            {
                _log.Debug(LogCategory.Join, "could not delete " + folder + ": " + ex.Message);
            }
        }

        /// <summary>Removes leftovers of earlier sessions (called at startup).</summary>
        public void CleanUp()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }
}
