using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Equativ.RoaringBitmaps.Tests;

public class RoaringPortableCodecTests
{
    [Fact]
    public void RoundTrip_Empty()
    {
        var rb = RoaringBitmap.Create();
        var bytes = DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Serialize(rb);
        var rb2 = DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Deserialize(bytes);
        Assert.Equal(rb, rb2);
    }

    [Fact]
    public void RoundTrip_SmallArray()
    {
        var rb = RoaringBitmap.Create(1, 2, 3, 4, 5);
        var bytes = DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Serialize(rb);
        var rb2 = DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Deserialize(bytes);
        Assert.Equal(rb, rb2);
    }

    [Fact]
    public void RoundTrip_DensePrefix_RunContainer()
    {
        var rb = RoaringBitmap.Create(Enumerable.Range(0, 365_000));
        var bytes = DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Serialize(rb);
        var rb2 = DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Deserialize(bytes);
        Assert.Equal(rb, rb2);

        // Rough size sanity: many blocks will be single runs; expect very small
        Assert.True(bytes.Length < 1024, $"Serialized length too large: {bytes.Length}");
    }

    [Fact]
    public void RoundTrip_MixedBlocks_ArrayAndBitmap()
    {
        var items = new System.Collections.Generic.List<int>();
        for (int i = 0; i < 2000; i += 2) items.Add(i); // sparse -> Array
        for (int i = 0; i < (1 << 16); i++) items.Add((1 << 16) | i); // full block -> Bitmap or Run
        for (int i = (2 << 16); i < (2 << 16) + 3000; i++) items.Add(i); // small run -> Array/Run

        var rb = RoaringBitmap.Create(items);
        var bytes = DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Serialize(rb);
        var rb2 = DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Deserialize(bytes);
        Assert.Equal(rb, rb2);
    }

    [Theory]
    [InlineData("bitmapwithoutruns.bin")]
    [InlineData("bitmapwithruns.bin")]
    public void Deserialize_SpecFixtures(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
        var rb = DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Deserialize(fs);
        // Compare with the existing Equativ deserializer on same data to ensure parity
        fs.Position = 0;
        var rbRef = RoaringBitmap.Deserialize(fs);
        Assert.Equal(rbRef, rb);
    }

    [Fact]
    public void Malformed_WrongCookie_Throws()
    {
        byte[] bad = new byte[8];
        // write wrong cookie
        bad[0] = 0xEE; bad[1] = 0xEE; bad[2] = 0xEE; bad[3] = 0xEE;
        Assert.Throws<FormatException>(() => DeployTrace.Common.Roaring.Serialization.RoaringPortableCodec.Deserialize(bad));
    }
}

