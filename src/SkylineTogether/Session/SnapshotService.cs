using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Colossal;
using Colossal.Serialization.Entities;
using Game.Prefabs;
using Game.Prefabs.Modes;
using Game.Serialization;
using Game.Simulation;
using SkylineTogether.Core;
using SkylineTogether.Core.Transport;
using Unity.Entities;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Session
{
    /// <summary>
    /// Host side of joining (R12 §4.3): serializes the live city with the game's own <see cref="SaveGameSystem"/> into a
    /// temporary file on the "SaveLoadGame" queue (so it never overlaps an autosave or a manual save). Unlike a normal
    /// save it writes no package, no preview, no "Continue" entry and does not collect garbage.
    /// </summary>
    public sealed class SnapshotService : ISnapshotProvider
    {
        private readonly ILog _log;
        private readonly MainThreadQueue _main;
        private readonly string _folder;
        private readonly Dictionary<uint, string> _files = new Dictionary<uint, string>();
        private uint _next = 1;

        public SnapshotService(MainThreadQueue main, ILog log)
        {
            _main = main;
            _log = log;
            _folder = Path.Combine(Path.GetTempPath(), "SkylineTogether", "host");
        }

        public void RequestSnapshot(Action<SnapshotReady> onReady, Action<string> onFailed)
        {
            uint id = _next++;
            World world = World.DefaultGameObjectInjectionWorld;
            SaveGameSystem save = world.GetExistingSystemManaged<SaveGameSystem>();
            PrefabSystem prefabs = world.GetExistingSystemManaged<PrefabSystem>();
            GameModeSystem modes = world.GetExistingSystemManaged<GameModeSystem>();
            SimulationSystem simulation = world.GetExistingSystemManaged<SimulationSystem>();
            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, "snapshot-" + id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bin");
            _files[id] = path;
            TaskManager.instance.EnqueueTask("SaveLoadGame", async () =>
            {
                try
                {
                    var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    try
                    {
                        save.stream = stream;
                        save.context = new Context(Purpose.SaveGame, Game.Version.current, Colossal.Hash128.Empty);
                        // the only save that carries the net id bindings (the "SaveLoadGame" queue runs one save at a
                        // time, so the host's autosaves and manual saves never see the flag)
                        Sync.NetIdSystem.WritingSnapshot = true;
                        await save.RunOnce();
                    }
                    finally
                    {
                        Sync.NetIdSystem.WritingSnapshot = false;
                        stream.Dispose(); // WriteSystem normally disposed it already
                    }
                    var content = new List<string>();
                    if (save.referencedContent.IsCreated)
                    {
                        foreach (Entity e in save.referencedContent) content.Add(prefabs.GetPrefabName(e));
                    }
                    var manifest = new SnapshotManifest
                    {
                        SnapshotId = id,
                        ChunkSize = BlobCodec.DefaultChunkBytes,
                        FrameIndex = simulation.frameIndex,
                        GameModeName = modes.currentModeName ?? "",
                        RenderPrefabIds = new string[0],
                        ReferencedContent = content.ToArray(),
                    };
                    _main.Post(() =>
                    {
                        // hosting stopped while the city was being saved: ReleaseAll could not delete the open file
                        if (!_files.ContainsKey(id)) TryDelete(path);
                        onReady(new SnapshotReady { Manifest = manifest, FilePath = path });
                    });
                }
                catch (Exception ex)
                {
                    _log.Error(LogCategory.Join, "taking the snapshot failed", ex);
                    _main.Post(() =>
                    {
                        // review fix: the half-written temp file was never released (the host only releases ids it got)
                        _files.Remove(id);
                        TryDelete(path);
                        onFailed(ex.Message);
                    });
                }
            }, 1);
        }

        public void Release(uint snapshotId)
        {
            if (!_files.TryGetValue(snapshotId, out string path)) return;
            _files.Remove(snapshotId);
            TryDelete(path);
        }

        /// <summary>
        /// Deletes every snapshot file of this game session (called when hosting stops; a snapshot still being written
        /// is deleted when its release arrives, or at the next start).
        /// </summary>
        public void ReleaseAll()
        {
            foreach (uint id in new List<uint>(_files.Keys)) Release(id);
        }

        /// <summary>Snapshot files currently kept (diagnostics).</summary>
        public int FileCount => _files.Count;

        /// <summary>Removes leftovers of earlier sessions (called at startup).</summary>
        public void CleanUp()
        {
            try
            {
                if (!Directory.Exists(_folder)) return;
                foreach (string f in Directory.GetFiles(_folder)) TryDelete(f);
            }
            catch (Exception)
            {
                // best effort
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Join, "could not delete " + path + ": " + ex.Message);
            }
        }
    }
}
