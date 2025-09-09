using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using Equativ.RoaringBitmaps;

namespace DeployTrace.Common.Roaring.Serialization;

/// <summary>
/// Serializer/Deserializer for the portable 32-bit Roaring format, interoperable with CRoaring and other languages.
/// Keeps Equativ.RoaringBitmaps as the in-memory structure.
/// </summary>
public static class RoaringPortableCodec
{
    // From the Roaring portable format spec
    private const ushort SERIAL_COOKIE_NO_RUNCONTAINER = 12346;
    private const ushort SERIAL_COOKIE = 12347; // appears in low 16 bits; high 16 bits encodes size-1
    private const int NO_OFFSET_THRESHOLD = 4;

    private enum ContainerKind : byte
    {
        Array,
        Bitmap,
        Run
    }

    private readonly struct Block
    {
        public readonly ushort Key;
        public readonly ContainerKind Kind;
        public readonly int Cardinality;
        public readonly int SerializedSizeBytes;
        public readonly ushort[] Lows; // for Array or for building runs/bitmap
        public readonly (ushort start, ushort lenMinus1)[] Runs; // only for Run

        public Block(ushort key, ContainerKind kind, int cardinality, int serializedSizeBytes, ushort[] lows, (ushort, ushort)[] runs)
        {
            Key = key;
            Kind = kind;
            Cardinality = cardinality;
            SerializedSizeBytes = serializedSizeBytes;
            Lows = lows;
            Runs = runs;
        }
    }

    public static void Serialize(Stream dst, RoaringBitmap bm)
    {
        if (dst is null) throw new ArgumentNullException(nameof(dst));
        if (bm is null) throw new ArgumentNullException(nameof(bm));

        // Extract sorted unique values
        List<int> valuesList = bm.ToArray();
        if (valuesList.Count == 0)
        {
            // Serialize an empty bitmap with no containers
            Span<byte> header = stackalloc byte[8];
            // no run containers cookie path
            BinaryPrimitives.WriteUInt32LittleEndian(header, SERIAL_COOKIE_NO_RUNCONTAINER);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0u);
            dst.Write(header);
            return;
        }

        // Group values by high 16 bits
        List<Block> blocks = BuildBlocks(valuesList);
        int size = blocks.Count;
        bool hasRun = false;
        for (int i = 0; i < size; i++)
        {
            if (blocks[i].Kind == ContainerKind.Run) { hasRun = true; break; }
        }

        // Write cookie and potential run-container bitmap / size
        if (hasRun)
        {
            uint cookie = (uint)(SERIAL_COOKIE | ((size - 1) << 16));
            Span<byte> cookieBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(cookieBuf, cookie);
            dst.Write(cookieBuf);

            // run bitmap
            int runBitmapBytes = (size + 7) / 8;
            byte[] runBitmap = new byte[runBitmapBytes];
            for (int i = 0; i < size; i++)
            {
                if (blocks[i].Kind == ContainerKind.Run)
                {
                    runBitmap[i >> 3] |= (byte)(1 << (i & 7));
                }
            }
            dst.Write(runBitmap, 0, runBitmap.Length);
        }
        else
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, SERIAL_COOKIE_NO_RUNCONTAINER);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)size);
            dst.Write(header);
        }

        // Descriptive header
        // For each container: key (UInt16), cardinalityMinusOne (UInt16)
        {
            byte[] desc = new byte[size * 4];
            Span<byte> span = desc;
            for (int i = 0; i < size; i++)
            {
                var b = blocks[i];
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 4, 2), b.Key);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 4 + 2, 2), checked((ushort)(b.Cardinality - 1)));
            }
            dst.Write(desc, 0, desc.Length);
        }

        // Offsets header: present if (!hasRun || size >= 4)
        int cookieExtraBytes = hasRun ? ((size + 7) / 8) : 4;
        bool writeOffsets = (!hasRun) || size >= NO_OFFSET_THRESHOLD;
        if (writeOffsets)
        {
            // startOffset from beginning of stream
            int startOffset = 4 + cookieExtraBytes + (size * 4) + (size * 4); // cookie + extra + descriptive + offsets
            // Write one UInt32 offset per container
            byte[] offsets = new byte[size * 4];
            Span<byte> span = offsets;
            int running = startOffset;
            for (int i = 0; i < size; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(i * 4, 4), (uint)running);
                running += blocks[i].SerializedSizeBytes;
            }
            dst.Write(offsets, 0, offsets.Length);
        }

        // Write container bodies
        for (int i = 0; i < size; i++)
        {
            var b = blocks[i];
            switch (b.Kind)
            {
                case ContainerKind.Array:
                {
                    WriteArray(dst, b.Lows, b.Cardinality);
                    break;
                }
                case ContainerKind.Bitmap:
                {
                    WriteBitmap(dst, b.Lows, b.Cardinality);
                    break;
                }
                case ContainerKind.Run:
                {
                    WriteRuns(dst, b.Runs);
                    break;
                }
            }
        }
    }

    public static byte[] Serialize(RoaringBitmap bm)
    {
        using var ms = new MemoryStream();
        Serialize(ms, bm);
        return ms.ToArray();
    }

    public static RoaringBitmap Deserialize(Stream src)
    {
        if (src is null) throw new ArgumentNullException(nameof(src));

        // Read cookie
        Span<byte> four = stackalloc byte[4];
        if (src.Read(four) != 4) throw new FormatException("Truncated portable roaring header.");
        uint cookie = BinaryPrimitives.ReadUInt32LittleEndian(four);
        ushort low = (ushort)(cookie & 0xFFFF);
        bool hasRun;
        int size;
        if (low == SERIAL_COOKIE)
        {
            hasRun = true;
            size = (int)((cookie >> 16) + 1);
        }
        else if (cookie == SERIAL_COOKIE_NO_RUNCONTAINER)
        {
            hasRun = false;
            if (src.Read(four) != 4) throw new FormatException("Truncated portable roaring size.");
            size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(four));
        }
        else
        {
            throw new FormatException("Invalid portable roaring cookie.");
        }

        byte[]? runBitmap = null;
        if (hasRun)
        {
            int runBytes = (size + 7) / 8;
            runBitmap = new byte[runBytes];
            int read = ReadExact(src, runBitmap, 0, runBytes);
            if (read != runBytes) throw new FormatException("Truncated run-container bitmap.");
        }

        // Descriptive header
        ushort[] keys = new ushort[size];
        int[] cards = new int[size];
        bool[] isRun = new bool[size];
        bool[] isBitmap = new bool[size];
        for (int i = 0; i < size; i++)
        {
            if (src.Read(four) != 4) throw new FormatException("Truncated descriptive header.");
            keys[i] = BinaryPrimitives.ReadUInt16LittleEndian(four);
            ushort cardMinus1 = BinaryPrimitives.ReadUInt16LittleEndian(four[2..]);
            int card = cardMinus1 + 1;
            cards[i] = card;
            bool runHere = hasRun && ((runBitmap![i >> 3] & (1 << (i & 7))) != 0);
            isRun[i] = runHere;
            isBitmap[i] = !runHere && card > 4096;
        }

        // Offsets header (skip or read and ignore)
        if (!hasRun || size >= NO_OFFSET_THRESHOLD)
        {
            // read size*4 bytes
            int toSkip = size * 4;
            SkipExact(src, toSkip);
        }

        var allValues = new List<int>();
        for (int i = 0; i < size; i++)
        {
            int shiftedKey = keys[i] << 16;
            if (isRun[i])
            {
                // Run container
                if (src.Read(four[..2]) != 2) throw new FormatException("Truncated run-container header.");
                int numRuns = BinaryPrimitives.ReadUInt16LittleEndian(four);
                for (int r = 0; r < numRuns; r++)
                {
                    if (src.Read(four) != 4) throw new FormatException("Truncated run entry.");
                    ushort start = BinaryPrimitives.ReadUInt16LittleEndian(four);
                    ushort lenMinus1 = BinaryPrimitives.ReadUInt16LittleEndian(four[2..]);
                    int count = lenMinus1 + 1;
                    for (int j = 0; j < count; j++)
                    {
                        allValues.Add(shiftedKey | (start + j));
                    }
                }
            }
            else if (isBitmap[i])
            {
                // Bitset: 8192 bytes (1024 ulong words)
                Span<byte> buf = stackalloc byte[8192];
                if (ReadExact(src, buf) != 8192) throw new FormatException("Truncated bitset container.");
                // Iterate bits
                for (int w = 0; w < 1024; w++)
                {
                    ulong word = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(w * 8, 8));
                    while (word != 0)
                    {
                        int t = BitOperations.TrailingZeroCount(word);
                        int lo = (w * 64) + t;
                        allValues.Add(shiftedKey | lo);
                        word &= word - 1; // clear lowest set bit
                    }
                }
            }
            else
            {
                // Array: card 16-bit lows
                int card = cards[i];
                int bytesNeeded = card * 2;
                byte[] lowBuf = new byte[bytesNeeded];
                if (ReadExact(src, lowBuf, 0, bytesNeeded) != bytesNeeded) throw new FormatException("Truncated array container.");
                for (int j = 0; j < card; j++)
                {
                    ushort lo = BinaryPrimitives.ReadUInt16LittleEndian(lowBuf.AsSpan(j * 2, 2));
                    allValues.Add(shiftedKey | lo);
                }
            }
        }

        return RoaringBitmap.Create(allValues);
    }

    public static RoaringBitmap Deserialize(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.ToArray());
        return Deserialize(ms);
    }

    public static bool TryDeserialize(Stream src, out RoaringBitmap? bitmap)
    {
        try
        {
            bitmap = Deserialize(src);
            return true;
        }
        catch
        {
            bitmap = null;
            return false;
        }
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out RoaringBitmap? bitmap)
    {
        try
        {
            bitmap = Deserialize(data);
            return true;
        }
        catch
        {
            bitmap = null;
            return false;
        }
    }

    public static bool IsPortableRoaring(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        uint cookie = BinaryPrimitives.ReadUInt32LittleEndian(data);
        ushort low = (ushort)(cookie & 0xFFFF);
        return low == SERIAL_COOKIE || cookie == SERIAL_COOKIE_NO_RUNCONTAINER;
    }

    private static List<Block> BuildBlocks(List<int> sortedValues)
    {
        // input is sorted; walk and group
        var result = new List<Block>(Math.Max(1, sortedValues.Count / 2048));

        int idx = 0;
        int n = sortedValues.Count;
        while (idx < n)
        {
            int value = sortedValues[idx];
            ushort hi = Utils.HighBits(value);
            int start = idx;
            idx++;
            while (idx < n && Utils.HighBits(sortedValues[idx]) == hi) idx++;
            int count = idx - start;
            ushort[] lows = new ushort[count];
            for (int j = 0; j < count; j++) lows[j] = Utils.LowBits(sortedValues[start + j]);

            // build runs
            var runs = BuildRuns(lows);
            int runSize = 2 + (runs.Length * 4);
            int arraySize = count * 2;
            int bitmapSize = 8192;

            // Choose smallest encoding; break ties favor Run then Array
            ContainerKind kind;
            int chosenSize;
            if (runSize <= arraySize && runSize <= bitmapSize)
            {
                kind = ContainerKind.Run;
                chosenSize = runSize;
            }
            else if (arraySize <= bitmapSize)
            {
                kind = ContainerKind.Array;
                chosenSize = arraySize;
            }
            else
            {
                kind = ContainerKind.Bitmap;
                chosenSize = bitmapSize;
            }

            result.Add(new Block(hi, kind, count, chosenSize, lows, runs));
        }

        return result;
    }

    private static (ushort start, ushort lenMinus1)[] BuildRuns(ReadOnlySpan<ushort> lows)
    {
        if (lows.Length == 0) return Array.Empty<(ushort, ushort)>();
        var runs = new List<(ushort, ushort)>(Math.Max(1, lows.Length / 8));
        int i = 0;
        while (i < lows.Length)
        {
            ushort start = lows[i++];
            ushort end = start;
            while (i < lows.Length && lows[i] == (ushort)(end + 1)) { end = lows[i++]; }
            runs.Add((start, (ushort)(end - start)));
        }
        return runs.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteArray(Stream dst, ReadOnlySpan<ushort> lows, int count)
    {
        // Write count 16-bit lows
        byte[] buf = new byte[count * 2];
        Span<byte> span = buf.AsSpan();
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 2, 2), lows[i]);
        }
        dst.Write(buf, 0, buf.Length);
    }

    private static void WriteBitmap(Stream dst, ReadOnlySpan<ushort> lows, int count)
    {
        // 1024 words of 64-bit = 8192 bytes
        byte[] buf = new byte[8192];
        Span<byte> span = buf;
        for (int i = 0; i < count; i++)
        {
            int lo = lows[i];
            int wordIndex = lo >> 6; // divide by 64
            int bitIndex = lo & 63;
            // Read-modify-write word
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(wordIndex * 8, 8));
            word |= 1UL << bitIndex;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(wordIndex * 8, 8), word);
        }
        dst.Write(buf, 0, buf.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRuns(Stream dst, ReadOnlySpan<(ushort start, ushort lenMinus1)> runs)
    {
        Span<byte> head = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(head, checked((ushort)runs.Length));
        dst.Write(head);
        Span<byte> pair = stackalloc byte[4];
        for (int i = 0; i < runs.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(pair, runs[i].start);
            BinaryPrimitives.WriteUInt16LittleEndian(pair[2..], runs[i].lenMinus1);
            dst.Write(pair);
        }
    }

    private static int ReadExact(Stream src, byte[] buffer, int offset, int count)
    {
        int readTotal = 0;
        while (readTotal < count)
        {
            int r = src.Read(buffer, offset + readTotal, count - readTotal);
            if (r == 0) break;
            readTotal += r;
        }
        return readTotal;
    }

    private static int ReadExact(Stream src, Span<byte> buffer)
    {
        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int r = src.Read(buffer.Slice(readTotal));
            if (r == 0) break;
            readTotal += r;
        }
        return readTotal;
    }

    private static void SkipExact(Stream src, int count)
    {
        Span<byte> tmp = stackalloc byte[4096];
        int remaining = count;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, tmp.Length);
            int r = ReadExact(src, tmp[..chunk]);
            if (r != chunk) throw new FormatException("Truncated portable roaring offsets.");
            remaining -= r;
        }
    }
}

public static class RoaringBitmapExtensions
{
    public static byte[] ToPortableBytes(this RoaringBitmap bm)
    {
        return RoaringPortableCodec.Serialize(bm);
    }

    public static RoaringBitmap FromPortableBytes(this ReadOnlySpan<byte> data)
    {
        return RoaringPortableCodec.Deserialize(data);
    }
}

