using System;
using SkylineTogether.Core;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace SkylineTogether.Sync
{
    /// <summary>
    /// One non-blocking GPU → CPU copy of a texture region (terrain and water sync). The request is queued in the GPU
    /// command stream at the moment <see cref="Start"/> is called, so it sees every GPU write issued before it (brush
    /// dispatches, <c>Graphics.CopyTexture</c>) and none issued after. Unity calls the completion callback on the main
    /// thread a few frames later; the data is copied out there (the native array is only valid inside the callback)
    /// and picked up by the owner's per-frame processing. Never blocks (no <c>WaitForCompletion</c>, unlike the game's
    /// own save path, R07 §3).
    /// </summary>
    internal sealed class GpuReadback<T> where T : struct
    {
        private GpuReadback()
        {
        }

        public int X { get; private set; }

        public int Y { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>Session generation at the request; results of an older session are dropped by the owner.</summary>
        public int Generation { get; private set; }

        /// <summary>What the owner does with the result.</summary>
        public object Tag { get; private set; }

        /// <summary>The texels, row-major (<see cref="Width"/> × <see cref="Height"/> × channels), once <see cref="Done"/>.</summary>
        public T[] Data { get; private set; }

        /// <summary>
        /// A request that has not completed after this many seconds counts as failed: after a device loss Unity may never
        /// call back, and the owner (one job at a time) stopped auditing terrain or sampling water for good.
        /// </summary>
        public const double TimeoutSeconds = 10.0;

        private bool _done, _failed;
        private double _startedAt;

        /// <summary>Completed, failed, or timed out (<see cref="TimeoutSeconds"/>).</summary>
        public bool Done => _done || TimedOut;

        public bool Failed => _failed || (!_done && TimedOut);

        private bool TimedOut => Now - _startedAt > TimeoutSeconds;

        private static double Now
        {
            get
            {
                try
                {
                    return Time.realtimeSinceStartupAsDouble;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>Whether this machine can read textures back asynchronously at all.</summary>
        public static bool Supported
        {
            get
            {
                try
                {
                    return SystemInfo.supportsAsyncGPUReadback;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Queues the readback of the region; null (logged) when the request could not be made.</summary>
        public static GpuReadback<T> Start(Texture texture, int x, int y, int width, int height, int generation, object tag, ILog log)
        {
            if (texture == null || width <= 0 || height <= 0) return null;
            var job = new GpuReadback<T> { X = x, Y = y, Width = width, Height = height, Generation = generation, Tag = tag, _startedAt = Now };
            try
            {
                AsyncGPUReadback.Request(texture, 0, x, width, y, height, 0, 1, job.OnComplete);
                return job;
            }
            catch (Exception ex)
            {
                log?.Warn(LogCategory.Fields, "GPU readback of " + texture.name + " [" + x + "," + y + " " + width + "x" + height + "] failed to start", ex);
                return null;
            }
        }

        private void OnComplete(AsyncGPUReadbackRequest request)
        {
            try
            {
                if (request.hasError)
                {
                    _failed = true;
                    return;
                }
                NativeArray<T> data = request.GetData<T>();
                var copy = new T[data.Length];
                data.CopyTo(copy);
                Data = copy;
            }
            catch (Exception)
            {
                _failed = true;
            }
            finally
            {
                _done = true;
            }
        }
    }

    /// <summary>
    /// Cached upload textures for <c>Graphics.CopyTexture</c> into a game render texture: one per size and format, so
    /// tile writes allocate nothing per tile. Unity executes uploads and copies in submission order on the render
    /// thread, so one texture can be reused for several tiles in a frame.
    /// </summary>
    internal sealed class UploadTextures : IDisposable
    {
        private readonly GraphicsFormat _format;
        private readonly System.Collections.Generic.Dictionary<long, Texture2D> _cache = new System.Collections.Generic.Dictionary<long, Texture2D>();

        public UploadTextures(GraphicsFormat format)
        {
            _format = format;
        }

        public Texture2D Get(int width, int height)
        {
            long key = ((long)width << 32) | (uint)height;
            if (_cache.TryGetValue(key, out Texture2D tex) && tex != null) return tex;
            tex = new Texture2D(width, height, _format, TextureCreationFlags.None)
            {
                name = "SkylineTogether.Upload." + _format,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _cache[key] = tex;
            return tex;
        }

        /// <summary>True when this machine can copy a texture into a render texture.</summary>
        public static bool CopySupported
        {
            get
            {
                try
                {
                    return (SystemInfo.copyTextureSupport & CopyTextureSupport.TextureToRT) != 0;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            foreach (Texture2D tex in _cache.Values)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
            _cache.Clear();
        }
    }
}
