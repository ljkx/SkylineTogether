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
            _folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _db = AssetDatabase.GetTransient(0L, _folder);
            _data = _db.AddAsset<SaveGameData>("SkylineTogetherJoin");
            return _data.GetWriteStream();
        }

        public void BeginLoad(SnapshotManifest manifest, IReadOnlyList<SettingEntry> pushedSettings, Action<LoadOutcome> onDone)
        {
            if (_data == null) throw new InvalidOperationException("no downloaded city to load");
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
                PlatformManager.instance.achievementsEnabled = false;
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
                // the load's completion callback disposes
                return;
            }
            ILocalAssetDatabase db = _db;
            string folder = _folder;
            _db = null;
            _data = null;
            _folder = null;
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
