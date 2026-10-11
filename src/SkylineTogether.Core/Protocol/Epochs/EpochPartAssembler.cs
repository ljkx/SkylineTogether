using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Client side of epoch parts (ARCH §4.6.2, §4.7.10): buffers the parts of multi-part epochs and hands out an epoch
    /// only when all its parts arrived, so it is applied atomically. Parts may arrive out of order; duplicates are
    /// ignored. An epoch whose buffered bytes exceed <c>spillThresholdBytes</c> (default 32 MiB) is moved to a temp file
    /// in the spill directory and continues there; an epoch above <see cref="Limits.MaxEpochTotalBytes"/> is dropped
    /// and reported as <see cref="PartResult.Oversized"/> (resync, ARCH §12.2). At most
    /// <see cref="Limits.MaxPartialMessagesPerPeer"/> epochs may be partial at once, holding at most
    /// <see cref="Limits.MaxEpochTotalBytes"/> together (ARCH §14.3); the part that would exceed the total is dropped
    /// with its epoch and reported as <see cref="PartResult.Oversized"/>.
    /// <para>
    /// The complete epoch is a stream holding one ordinary epoch message: a 10-byte header with the epoch number, host
    /// tick and flags of the parts (<see cref="EpochFlags.MultiPart"/> cleared, no part numbers) and the total section
    /// count, followed by the sections of every part in part order, unchanged. The TX records thus arrive as several TX
    /// sections, each split at record boundaries, whose records form one sequence; every section is at most
    /// <see cref="Limits.MaxUncompressedBytes"/>. Read it with <see cref="EpochStreamReader"/> (section by section, bounded
    /// memory) or load it and use <see cref="EpochReader"/>; dispose it when done (a spilled epoch's temp file is deleted
    /// then). A single-part epoch passed to <see cref="Accept"/> is returned at once as a copy.
    /// </para>
    /// <para>
    /// Used on the network thread; <see cref="PartialEpochs"/> and <see cref="PartialBytes"/> may be read from any thread.
    /// <see cref="Accept"/> never throws for bad input: malformed or inconsistent parts report <see cref="PartResult.Invalid"/>.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Network)]
    public sealed class EpochPartAssembler : IDisposable
    {
        private const int RecentlyCompletedCapacity = 16;

        private sealed class Partial
        {
            public uint Epoch;
            public uint HostTick;
            public EpochFlags Flags;
            public int PartCount;
            public int Received;
            public long Bytes;
            public int Sections;
            public PartSlot[] Parts;
            public FileStream Spill;
            public string SpillPath;
            public long SpillLength;
        }

        private struct PartSlot
        {
            public bool Present;
            public byte[] Data;          // pooled; null once spilled
            public int Length;
            public long FileOffset;
            public int Sections;
        }

        private readonly string _spillDirectory;
        private readonly long _spillThreshold;
        private readonly long _maxEpochBytes;
        private readonly ILog _log;
        private readonly List<Partial> _partials = new List<Partial>(Limits.MaxPartialMessagesPerPeer);
        private readonly uint[] _recentlyCompleted = new uint[RecentlyCompletedCapacity];
        private int _recentCount;
        private int _recentNext;
        private readonly List<uint> _dropped = new List<uint>();
        private bool _spillFailed;
        private long _partialBytes;
        private int _partialCount;
        private bool _disposed;

        /// <param name="spillDirectory">Folder for spill files (created when needed); null or empty keeps everything in memory.</param>
        /// <param name="spillThresholdBytes">Buffered bytes of one epoch above which it moves to a spill file.</param>
        public EpochPartAssembler(string spillDirectory, long spillThresholdBytes = 32L << 20)
            : this(spillDirectory, spillThresholdBytes, null)
        {
        }

        /// <param name="spillDirectory">Folder for spill files (created when needed); null or empty keeps everything in memory.</param>
        /// <param name="spillThresholdBytes">Buffered bytes of one epoch above which it moves to a spill file.</param>
        /// <param name="log">Receives spill failures and dropped epochs; may be null.</param>
        public EpochPartAssembler(string spillDirectory, long spillThresholdBytes, ILog log)
            : this(spillDirectory, spillThresholdBytes, log, Limits.MaxEpochTotalBytes)
        {
        }

        /// <summary>Test seam: a smaller epoch limit.</summary>
        internal EpochPartAssembler(string spillDirectory, long spillThresholdBytes, ILog log, long maxEpochBytes)
        {
            if (spillThresholdBytes < 0) throw new ArgumentOutOfRangeException(nameof(spillThresholdBytes));
            if (maxEpochBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxEpochBytes));
            _spillDirectory = string.IsNullOrEmpty(spillDirectory) ? null : spillDirectory;
            _spillThreshold = spillThresholdBytes;
            _maxEpochBytes = maxEpochBytes;
            _log = log;
        }

        /// <summary>Epochs with some but not all parts received.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public int PartialEpochs => Volatile.Read(ref _partialCount);

        /// <summary>Bytes buffered for partial epochs, in memory and in spill files.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public long PartialBytes => Interlocked.Read(ref _partialBytes);

        /// <summary>
        /// Takes one epoch message: <paramref name="header"/> is its header (from <see cref="EpochReader.Open"/>) and
        /// <c>buffer[offset .. offset+count-1]</c> the whole message, which is copied (the buffer may be reused after the
        /// call). On <see cref="PartResult.Complete"/> the caller owns <paramref name="completeEpoch"/>.
        /// </summary>
        public PartResult Accept(in EpochHeader header, byte[] buffer, int offset, int count, out Stream completeEpoch)
        {
            completeEpoch = null;
            WireFormat.CheckRange(buffer, offset, count, nameof(buffer));
            if (_disposed) throw new ObjectDisposedException(nameof(EpochPartAssembler));

            EpochHeader parsed;
            int headerLength;
            try
            {
                headerLength = EpochFormat.ParseHeader(buffer, offset, count, out parsed);
                EpochFormat.ValidateSections(buffer, offset + headerLength, offset + count, parsed.SectionCount);
            }
            catch (DecodeException e)
            {
                _log.Warn(LogCategory.Protocol, "malformed epoch part: " + e.Message);
                return PartResult.Invalid;
            }
            if (parsed.Epoch != header.Epoch || parsed.HostTick != header.HostTick || parsed.Flags != header.Flags
                || parsed.SectionCount != header.SectionCount || parsed.PartIndex != header.PartIndex || parsed.PartCount != header.PartCount)
            {
                _log.Warn(LogCategory.Protocol, "epoch part header does not describe the message bytes (" + parsed.Epoch + ")");
                return PartResult.Invalid;
            }

            if (parsed.PartCount == 1)
            {
                var copy = new byte[count];
                System.Buffer.BlockCopy(buffer, offset, copy, 0, count);
                completeEpoch = new MemoryStream(copy, 0, count, false, true);
                return PartResult.Complete;
            }

            uint epoch = parsed.Epoch.Value;
            if (IsRecentlyCompleted(epoch)) return PartResult.Incomplete;     // a late duplicate of a delivered epoch
            if (_dropped.Contains(epoch)) return PartResult.Oversized;

            Partial partial = Find(epoch);
            if (partial == null)
            {
                if (_partials.Count >= Limits.MaxPartialMessagesPerPeer)
                {
                    _log.Warn(LogCategory.Protocol, "more than " + Limits.MaxPartialMessagesPerPeer.ToString(CultureInfo.InvariantCulture)
                                                    + " partial epochs (part of " + parsed.Epoch + ")");
                    return PartResult.Invalid;
                }
                partial = new Partial
                {
                    Epoch = epoch,
                    HostTick = parsed.HostTick,
                    Flags = parsed.Flags,
                    PartCount = parsed.PartCount,
                    Parts = new PartSlot[parsed.PartCount]
                };
                _partials.Add(partial);
                Interlocked.Increment(ref _partialCount);
            }
            else if (partial.HostTick != parsed.HostTick || partial.Flags != parsed.Flags || partial.PartCount != parsed.PartCount)
            {
                _log.Warn(LogCategory.Protocol, "parts of " + parsed.Epoch + " disagree on host tick, flags or part count");
                Drop(partial);
                return PartResult.Invalid;
            }

            if (partial.Parts[parsed.PartIndex].Present) return PartResult.Incomplete;   // duplicate part

            int bodyLength = count - headerLength;
            if (partial.Bytes + bodyLength > _maxEpochBytes)
            {
                _log.Warn(LogCategory.Protocol, parsed.Epoch + " exceeds " + _maxEpochBytes.ToString(CultureInfo.InvariantCulture) + " bytes; dropped (resync)");
                Drop(partial);
                RememberDropped(epoch);
                return PartResult.Oversized;
            }
            if (_partialBytes + bodyLength > _maxEpochBytes)
            {
                // the host sends the parts of one epoch back to back on the ordered lane, so several large partial epochs
                // at once mean a broken stream; the buffered total stays bounded by one epoch (ARCH §14.3)
                _log.Warn(LogCategory.Protocol, "partial epochs together exceed " + _maxEpochBytes.ToString(CultureInfo.InvariantCulture)
                                                + " bytes; " + parsed.Epoch + " dropped (resync)");
                Drop(partial);
                RememberDropped(epoch);
                return PartResult.Oversized;
            }
            if (partial.Sections + parsed.SectionCount > Limits.MaxSectionsPerEpoch)
            {
                _log.Warn(LogCategory.Protocol, parsed.Epoch + " has more than 255 sections in total");
                Drop(partial);
                return PartResult.Invalid;
            }

            if (partial.Spill == null && _spillDirectory != null && !_spillFailed && partial.Bytes + bodyLength > _spillThreshold)
            {
                StartSpill(partial);
            }
            if (!Store(partial, parsed.PartIndex, buffer, offset + headerLength, bodyLength))
            {
                Drop(partial);
                return PartResult.Invalid;
            }
            partial.Parts[parsed.PartIndex].Sections = parsed.SectionCount;
            partial.Received++;
            partial.Bytes += bodyLength;
            partial.Sections += parsed.SectionCount;
            Interlocked.Add(ref _partialBytes, bodyLength);

            if (partial.Received < partial.PartCount) return PartResult.Incomplete;
            completeEpoch = Complete(partial);
            if (completeEpoch == null) return PartResult.Invalid;
            RememberCompleted(epoch);
            return PartResult.Complete;
        }

        /// <summary>Drops every partial epoch (resync, session reset) and forgets completed and dropped epochs.</summary>
        public void Reset()
        {
            for (int i = _partials.Count - 1; i >= 0; i--) Drop(_partials[i]);
            _recentCount = 0;
            _recentNext = 0;
            _dropped.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Reset();
            _disposed = true;
        }

        private bool Store(Partial partial, int index, byte[] buffer, int offset, int length)
        {
            ref PartSlot slot = ref partial.Parts[index];
            if (partial.Spill != null)
            {
                try
                {
                    partial.Spill.Position = partial.SpillLength;
                    partial.Spill.Write(buffer, offset, length);
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    _log.Error(LogCategory.Protocol, "writing the epoch spill file failed", e);
                    return false;
                }
                slot.FileOffset = partial.SpillLength;
                partial.SpillLength += length;
            }
            else
            {
                byte[] data = BufferPool.Rent(Math.Max(length, 1));
                System.Buffer.BlockCopy(buffer, offset, data, 0, length);
                slot.Data = data;
            }
            slot.Length = length;
            slot.Present = true;
            return true;
        }

        private void StartSpill(Partial partial)
        {
            string path = null;
            FileStream file = null;
            try
            {
                Directory.CreateDirectory(_spillDirectory);
                path = Path.Combine(_spillDirectory, "st-epoch-" + partial.Epoch.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".tmp");
                file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
                long length = 0;
                for (int i = 0; i < partial.Parts.Length; i++)
                {
                    ref PartSlot slot = ref partial.Parts[i];
                    if (!slot.Present) continue;
                    file.Write(slot.Data, 0, slot.Length);
                    slot.FileOffset = length;
                    length += slot.Length;
                }
                for (int i = 0; i < partial.Parts.Length; i++)
                {
                    ref PartSlot slot = ref partial.Parts[i];
                    if (slot.Data == null) continue;
                    BufferPool.Return(slot.Data);
                    slot.Data = null;
                }
                partial.Spill = file;
                partial.SpillPath = path;
                partial.SpillLength = length;
                _log.Debug(LogCategory.Protocol, "epoch " + partial.Epoch.ToString(CultureInfo.InvariantCulture) + " spills to " + path);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // keep the epoch in memory (bounded by the epoch limit) and stop trying to spill
                _spillFailed = true;
                _log.Warn(LogCategory.Protocol, "cannot spill large epochs to " + _spillDirectory + "; keeping them in memory", e);
                file?.Dispose();
                TryDelete(path);
            }
        }

        private Stream Complete(Partial partial)
        {
            _partials.Remove(partial);
            Interlocked.Decrement(ref _partialCount);
            Interlocked.Add(ref _partialBytes, -partial.Bytes);

            var head = new byte[EpochFormat.FixedHeaderBytes];
            EpochFormat.WriteUInt32(head, 0, partial.Epoch);
            EpochFormat.WriteUInt32(head, 4, partial.HostTick);
            head[8] = (byte)(partial.Flags & ~EpochFlags.MultiPart);
            head[EpochFormat.SectionCountOffset] = (byte)partial.Sections;

            if (partial.Spill == null)
            {
                long total = head.Length + partial.Bytes;
                var all = new byte[total];
                System.Buffer.BlockCopy(head, 0, all, 0, head.Length);
                int position = head.Length;
                for (int i = 0; i < partial.Parts.Length; i++)
                {
                    ref PartSlot slot = ref partial.Parts[i];
                    System.Buffer.BlockCopy(slot.Data, 0, all, position, slot.Length);
                    position += slot.Length;
                    BufferPool.Return(slot.Data);
                    slot.Data = null;
                }
                return new MemoryStream(all, 0, all.Length, false, true);
            }

            try
            {
                partial.Spill.Flush();
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                _log.Error(LogCategory.Protocol, "flushing the epoch spill file failed", e);
                partial.Spill.Dispose();
                TryDelete(partial.SpillPath);
                return null;
            }
            var offsets = new long[partial.Parts.Length];
            var lengths = new int[partial.Parts.Length];
            for (int i = 0; i < partial.Parts.Length; i++)
            {
                offsets[i] = partial.Parts[i].FileOffset;
                lengths[i] = partial.Parts[i].Length;
            }
            return new SegmentedReadStream(head, partial.Spill, partial.SpillPath, offsets, lengths);
        }

        private void Drop(Partial partial)
        {
            if (!_partials.Remove(partial)) return;
            Interlocked.Decrement(ref _partialCount);
            Interlocked.Add(ref _partialBytes, -partial.Bytes);
            for (int i = 0; i < partial.Parts.Length; i++)
            {
                ref PartSlot slot = ref partial.Parts[i];
                if (slot.Data == null) continue;
                BufferPool.Return(slot.Data);
                slot.Data = null;
            }
            if (partial.Spill != null)
            {
                partial.Spill.Dispose();
                TryDelete(partial.SpillPath);
                partial.Spill = null;
            }
        }

        private Partial Find(uint epoch)
        {
            for (int i = 0; i < _partials.Count; i++)
            {
                if (_partials[i].Epoch == epoch) return _partials[i];
            }
            return null;
        }

        private bool IsRecentlyCompleted(uint epoch)
        {
            for (int i = 0; i < _recentCount; i++)
            {
                if (_recentlyCompleted[i] == epoch) return true;
            }
            return false;
        }

        private void RememberCompleted(uint epoch)
        {
            _recentlyCompleted[_recentNext] = epoch;
            _recentNext = (_recentNext + 1) % RecentlyCompletedCapacity;
            if (_recentCount < RecentlyCompletedCapacity) _recentCount++;
        }

        private void RememberDropped(uint epoch)
        {
            if (_dropped.Count >= RecentlyCompletedCapacity) _dropped.RemoveAt(0);
            _dropped.Add(epoch);
        }

        private static void TryDelete(string path)
        {
            if (path == null) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
