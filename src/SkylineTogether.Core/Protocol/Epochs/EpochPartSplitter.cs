using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Host side of epoch parts (ARCH §4.5.4, §4.6.2): builds the epoch message(s) of one frame for one peer and never
    /// emits a message above the part size (default <see cref="Limits.MaxUncompressedBytes"/>). The logical order of an
    /// epoch is
    /// <code>
    /// [TSET sections] [shared sections: SYS PFB ICON EVT NAME CLK SES] [TX records] [per-peer sections: VAL HASH MAP INSP]
    /// </code>
    /// each group in the caller's order. When it fits, one ordinary epoch message is emitted. Otherwise the sequence is
    /// packed greedily into parts that share the epoch number: whole sections are never split, the TX records are split
    /// at record boundaries into several TX sections, so part 0 carries the header sections and the per-peer sections
    /// ride in the last part (unless they do not fit there). The client applies the epoch only when every part arrived
    /// (<see cref="EpochPartAssembler"/>).
    /// <para>
    /// A single record or section larger than a part, more than <see cref="Limits.MaxEpochParts"/> parts, or more than
    /// <see cref="Limits.MaxSectionsPerEpoch"/> sections in the whole epoch cannot be sent:
    /// <see cref="InvalidOperationException"/> before anything is emitted. TX records are validated (by
    /// <see cref="RecordReader"/>) only when they have to be split.
    /// </para>
    /// <para>Not thread-safe; one instance per worker thread.</para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class EpochPartSplitter
    {
        private struct SectionRef
        {
            public SectionType Type;
            public byte[] Array;
            public int BodyOffset;
            public int BodyCount;
            public int Size;            // header + body
        }

        private struct Item
        {
            public bool IsTx;
            public int SectionIndex;    // into _sections
            public int TxOffset;
            public int TxCount;
        }

        private struct PartPlan
        {
            public int FirstItem;
            public int ItemCount;
        }

        private readonly int _maxPartBytes;
        private readonly EpochWriter _writer = new EpochWriter();
        private readonly ByteWriter _part = new ByteWriter(64 * 1024);
        private readonly RecordReader _scanner = new RecordReader();
        private readonly List<SectionRef> _sections = new List<SectionRef>();
        private readonly List<int> _typeSets = new List<int>();
        private readonly List<int> _shared = new List<int>();
        private readonly List<int> _perPeer = new List<int>();
        private readonly List<Item> _items = new List<Item>();
        private readonly List<PartPlan> _parts = new List<PartPlan>();

        // packing state of the part being planned
        private int _partUsed;
        private int _partSections;
        private int _partFirstItem;
        private int _capacity;

        /// <summary>Parts of at most <see cref="Limits.MaxUncompressedBytes"/>.</summary>
        public EpochPartSplitter() : this(Limits.MaxUncompressedBytes)
        {
        }

        /// <param name="maxPartBytes">Largest epoch message, 4 KiB ... <see cref="Limits.MaxUncompressedBytes"/> (smaller values serve tests).</param>
        public EpochPartSplitter(int maxPartBytes)
        {
            if (maxPartBytes < 4096 || maxPartBytes > Limits.MaxUncompressedBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPartBytes), maxPartBytes, "4096 ... " + Limits.MaxUncompressedBytes.ToString(CultureInfo.InvariantCulture));
            }
            _maxPartBytes = maxPartBytes;
        }

        /// <summary>The largest message this splitter emits.</summary>
        public int MaxPartBytes => _maxPartBytes;

        /// <summary>Parts emitted by the last <see cref="Split"/> call.</summary>
        public int LastPartCount { get; private set; }

        /// <summary>
        /// Emits the epoch message(s) for <paramref name="txRecords"/> (records written by <see cref="RecordWriter"/>;
        /// may be empty, then no TX section is written) and <paramref name="otherSections"/> (each segment exactly one
        /// encoded section, <c>u8 type | varint length | body</c>, not of type TX). Each emitted message is valid only
        /// during the callback. <see cref="EpochFlags.MultiPart"/> is managed here; the other flags are copied to every part.
        /// </summary>
        public void Split(EpochId epoch, uint hostTick, EpochFlags flags, byte[] txRecords, int txOffset, int txCount,
                          IReadOnlyList<ArraySegment<byte>> otherSections, Action<byte[], int, int> emitPart)
        {
            WireFormat.CheckOptionalRange(txRecords, txOffset, txCount, nameof(txRecords));
            if (emitPart == null) throw new ArgumentNullException(nameof(emitPart));
            if ((flags & ~EpochFormat.KnownFlags) != 0) throw new ArgumentException("unknown epoch flags", nameof(flags));
            flags &= ~EpochFlags.MultiPart;
            LastPartCount = 0;
            Classify(otherSections);

            long singleSize = EpochFormat.FixedHeaderBytes;
            for (int i = 0; i < _sections.Count; i++) singleSize += _sections[i].Size;
            if (txCount > 0) singleSize += EpochFormat.SectionHeaderSize(txCount) + txCount;
            int singleSections = _sections.Count + (txCount > 0 ? 1 : 0);
            if (singleSize <= _maxPartBytes && singleSections <= Limits.MaxSectionsPerEpoch)
            {
                _part.Reset();
                _writer.Begin(epoch, hostTick, flags, _part);
                AppendGroup(_typeSets);
                AppendGroup(_shared);
                if (txCount > 0) _writer.AppendSection(SectionType.Tx, txRecords, txOffset, txCount);
                AppendGroup(_perPeer);
                _writer.End();
                LastPartCount = 1;
                emitPart(_part.Buffer, 0, _part.Length);
                return;
            }

            Plan(txRecords, txOffset, txCount);
            int partCount = _parts.Count;
            for (int index = 0; index < partCount; index++)
            {
                PartPlan plan = _parts[index];
                _part.Reset();
                _writer.Begin(epoch, hostTick, flags, _part, index, partCount);
                for (int i = plan.FirstItem; i < plan.FirstItem + plan.ItemCount; i++)
                {
                    Item item = _items[i];
                    if (item.IsTx)
                    {
                        _writer.AppendSection(SectionType.Tx, txRecords, item.TxOffset, item.TxCount);
                    }
                    else
                    {
                        SectionRef s = _sections[item.SectionIndex];
                        _writer.AppendSection(s.Type, s.Array, s.BodyOffset, s.BodyCount);
                    }
                }
                _writer.End();
                LastPartCount = index + 1;
                emitPart(_part.Buffer, 0, _part.Length);
            }
        }

        private void Classify(IReadOnlyList<ArraySegment<byte>> otherSections)
        {
            _sections.Clear();
            _typeSets.Clear();
            _shared.Clear();
            _perPeer.Clear();
            if (otherSections == null) return;
            for (int i = 0; i < otherSections.Count; i++)
            {
                ArraySegment<byte> segment = otherSections[i];
                if (segment.Array == null || segment.Count < 2) throw new ArgumentException("other section " + i.ToString(CultureInfo.InvariantCulture) + " is empty", nameof(otherSections));
                int position = segment.Offset;
                int end = segment.Offset + segment.Count;
                SectionType type;
                int length;
                try
                {
                    EpochFormat.ReadSectionHeader(segment.Array, ref position, end, out type, out length);
                }
                catch (DecodeException e)
                {
                    throw new ArgumentException("other section " + i.ToString(CultureInfo.InvariantCulture) + " is not an encoded section: " + e.Message, nameof(otherSections), e);
                }
                if (position + length != end)
                {
                    throw new ArgumentException("other section " + i.ToString(CultureInfo.InvariantCulture) + " is not exactly one section", nameof(otherSections));
                }
                if (type == SectionType.Tx) throw new ArgumentException("TX records are passed as txRecords, not as another section", nameof(otherSections));
                _sections.Add(new SectionRef { Type = type, Array = segment.Array, BodyOffset = position, BodyCount = length, Size = segment.Count });
                switch (type)
                {
                    case SectionType.TypeSets:
                        _typeSets.Add(_sections.Count - 1);
                        break;
                    case SectionType.Values:
                    case SectionType.Hash:
                    case SectionType.CellMaps:
                    case SectionType.Inspection:
                        _perPeer.Add(_sections.Count - 1);
                        break;
                    default:
                        _shared.Add(_sections.Count - 1);
                        break;
                }
            }
        }

        private void AppendGroup(List<int> group)
        {
            for (int i = 0; i < group.Count; i++)
            {
                SectionRef s = _sections[group[i]];
                _writer.AppendSection(s.Type, s.Array, s.BodyOffset, s.BodyCount);
            }
        }

        private void Plan(byte[] tx, int txOffset, int txCount)
        {
            _items.Clear();
            _parts.Clear();
            _capacity = _maxPartBytes - EpochFormat.HeaderSize(Limits.MaxEpochParts - 1, Limits.MaxEpochParts);
            _partUsed = 0;
            _partSections = 0;
            _partFirstItem = 0;

            PlanSections(_typeSets);
            PlanSections(_shared);
            if (txCount > 0) PlanTx(tx, txOffset, txCount);
            PlanSections(_perPeer);
            if (_items.Count > _partFirstItem) ClosePart();

            int totalSections = _items.Count;
            if (_parts.Count > Limits.MaxEpochParts)
            {
                throw new InvalidOperationException("the epoch needs " + _parts.Count.ToString(CultureInfo.InvariantCulture) + " parts, more than "
                                                    + Limits.MaxEpochParts.ToString(CultureInfo.InvariantCulture));
            }
            if (totalSections > Limits.MaxSectionsPerEpoch)
            {
                throw new InvalidOperationException("the epoch needs " + totalSections.ToString(CultureInfo.InvariantCulture)
                                                    + " sections, more than 255; the part size of " + _maxPartBytes.ToString(CultureInfo.InvariantCulture) + " bytes is too small");
            }
        }

        private void PlanSections(List<int> group)
        {
            for (int i = 0; i < group.Count; i++)
            {
                int size = _sections[group[i]].Size;
                if (size > _capacity)
                {
                    throw new InvalidOperationException(_sections[group[i]].Type + " section of " + size.ToString(CultureInfo.InvariantCulture)
                                                        + " bytes cannot fit an epoch part of " + _maxPartBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
                }
                if (_partUsed + size > _capacity || _partSections == Limits.MaxSectionsPerEpoch) ClosePart();
                _items.Add(new Item { SectionIndex = group[i] });
                _partUsed += size;
                _partSections++;
            }
        }

        private void PlanTx(byte[] tx, int txOffset, int txCount)
        {
            int chunkStart = -1;
            int chunkCount = 0;
            int previousStart = -1;
            int txEnd = txOffset + txCount;
            _scanner.Reset(tx, txOffset, txCount);
            try
            {
                for (;;)
                {
                    bool more = _scanner.Next(out _);
                    int boundary = more ? _scanner.RecordOffset : txEnd;
                    if (previousStart >= 0)
                    {
                        AddRecord(previousStart, boundary - previousStart, ref chunkStart, ref chunkCount);
                    }
                    if (!more) break;
                    previousStart = boundary;
                }
            }
            catch (DecodeException e)
            {
                throw new ArgumentException("txRecords is not a valid record sequence: " + e.Message, "txRecords", e);
            }
            if (chunkStart >= 0) AddTxChunk(chunkStart, chunkCount);
        }

        private void AddRecord(int start, int length, ref int chunkStart, ref int chunkCount)
        {
            if (EpochFormat.SectionHeaderSize(length) + length > _capacity)
            {
                throw new InvalidOperationException("a " + (RecordKind)KindAt(start) + " record of " + length.ToString(CultureInfo.InvariantCulture)
                                                    + " bytes cannot fit an epoch part of " + _maxPartBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
            }
            if (chunkStart >= 0)
            {
                int grown = chunkCount + length;
                if (_partUsed + EpochFormat.SectionHeaderSize(grown) + grown <= _capacity)
                {
                    chunkCount = grown;
                    return;
                }
                AddTxChunk(chunkStart, chunkCount);
                ClosePart();
            }
            else if (_partUsed + EpochFormat.SectionHeaderSize(length) + length > _capacity || _partSections == Limits.MaxSectionsPerEpoch)
            {
                ClosePart();
            }
            chunkStart = start;
            chunkCount = length;
        }

        private byte KindAt(int start) => _scanner.Buffer[start];

        private void AddTxChunk(int start, int count)
        {
            _items.Add(new Item { IsTx = true, TxOffset = start, TxCount = count });
            _partUsed += EpochFormat.SectionHeaderSize(count) + count;
            _partSections++;
        }

        private void ClosePart()
        {
            if (_items.Count == _partFirstItem) return;
            _parts.Add(new PartPlan { FirstItem = _partFirstItem, ItemCount = _items.Count - _partFirstItem });
            _partFirstItem = _items.Count;
            _partUsed = 0;
            _partSections = 0;
        }
    }
}
