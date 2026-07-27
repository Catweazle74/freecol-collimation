using System.Text;
using FreeCol.Core.Imaging;
using OpenCvSharp;

namespace FreeCol.Core.Tests.Imaging;

public class FitsReaderTests
{
    // Baut eine minimale FITS-Datei: ASCII-Header (auf 2880-Byte-Blockgrenze mit
    // Leerzeichen aufgefüllt, abgeschlossen durch END) gefolgt von den Bilddaten.
    private static string WriteFits(IEnumerable<(string Key, string Value)> cards, byte[] data)
    {
        var bytes = new List<byte>();
        void Card(string text) => bytes.AddRange(Encoding.ASCII.GetBytes(text.PadRight(80)[..80]));

        foreach (var (k, v) in cards)
            Card($"{k,-8}= {v}"); // Spalte 8 = '=', ab Spalte 10 der Wert
        Card("END");
        while (bytes.Count % 2880 != 0) bytes.Add((byte)' ');
        bytes.AddRange(data);

        var path = Path.Combine(Path.GetTempPath(), $"freecol-fits-{Guid.NewGuid():N}.fits");
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    // Kodiert physikalische Werte (0..65535) als big-endian int16 nach FITS-Konvention
    // (unsigned über BZERO=32768): gespeichert wird signed = phys - 32768.
    private static byte[] Be16Unsigned(params int[] physical)
    {
        var b = new byte[physical.Length * 2];
        for (var i = 0; i < physical.Length; i++)
        {
            var signed = unchecked((short)(physical[i] - 32768));
            b[i * 2] = (byte)((signed >> 8) & 0xFF);
            b[i * 2 + 1] = (byte)(signed & 0xFF);
        }
        return b;
    }

    private static void WithFits(IEnumerable<(string, string)> cards, byte[] data, Action<string> body)
    {
        var path = WriteFits(cards, data);
        try { body(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadGray16_Bitpix16_RoundTripsPhysicalValues()
    {
        // 2×2-Bild; SIMPLE- und OBJECT-Cards (String) müssen ignoriert werden,
        // der Kommentar hinter '/' darf den Wert nicht verfälschen.
        var cards = new (string, string)[]
        {
            ("SIMPLE", "T"),
            ("BITPIX", "16 / bits per pixel"),
            ("NAXIS", "2"),
            ("NAXIS1", "2"),
            ("NAXIS2", "2"),
            ("BZERO", "32768"),
            ("BSCALE", "1"),
            ("OBJECT", "'STAR'"),
        };
        var data = Be16Unsigned(0, 1000, 32768, 65535); // row-major: (0,0)(0,1)(1,0)(1,1)

        WithFits(cards, data, path =>
        {
            using var mat = FitsReader.ReadGray16(path);
            Assert.Equal(MatType.CV_16UC1, mat.Type());
            Assert.Equal(2, mat.Width);
            Assert.Equal(2, mat.Height);
            Assert.Equal((ushort)0, mat.At<ushort>(0, 0));
            Assert.Equal((ushort)1000, mat.At<ushort>(0, 1));
            Assert.Equal((ushort)32768, mat.At<ushort>(1, 0));
            Assert.Equal((ushort)65535, mat.At<ushort>(1, 1));
        });
    }

    [Fact]
    public void ReadGray16_Bitpix8_ReadsBytesDirectly()
    {
        var cards = new (string, string)[]
        {
            ("BITPIX", "8"), ("NAXIS", "2"), ("NAXIS1", "3"), ("NAXIS2", "1"),
        };
        WithFits(cards, new byte[] { 0, 128, 255 }, path =>
        {
            using var mat = FitsReader.ReadGray16(path);
            Assert.Equal((ushort)0, mat.At<ushort>(0, 0));
            Assert.Equal((ushort)128, mat.At<ushort>(0, 1));
            Assert.Equal((ushort)255, mat.At<ushort>(0, 2));
        });
    }

    [Fact]
    public void ReadGray16_PhysicalAboveRange_ClampsToMax()
    {
        // BSCALE hebt raw·1000 über 65535 → muss auf 65535 begrenzt werden.
        var cards = new (string, string)[]
        {
            ("BITPIX", "8"), ("NAXIS", "2"), ("NAXIS1", "1"), ("NAXIS2", "1"),
            ("BZERO", "0"), ("BSCALE", "1000"),
        };
        WithFits(cards, new byte[] { 100 }, path =>
        {
            using var mat = FitsReader.ReadGray16(path);
            Assert.Equal((ushort)65535, mat.At<ushort>(0, 0));
        });
    }

    [Fact]
    public void ReadGray16_NegativePhysical_ClampsToZero()
    {
        var cards = new (string, string)[]
        {
            ("BITPIX", "8"), ("NAXIS", "2"), ("NAXIS1", "1"), ("NAXIS2", "1"),
            ("BZERO", "-100"), ("BSCALE", "1"),
        };
        WithFits(cards, new byte[] { 50 }, path => // phys = -100 + 50 = -50 → 0
        {
            using var mat = FitsReader.ReadGray16(path);
            Assert.Equal((ushort)0, mat.At<ushort>(0, 0));
        });
    }

    [Fact]
    public void ReadGray16_Naxis3_Throws()
    {
        var cards = new (string, string)[]
        {
            ("BITPIX", "16"), ("NAXIS", "3"), ("NAXIS1", "1"), ("NAXIS2", "1"),
        };
        WithFits(cards, Be16Unsigned(0), path =>
            Assert.Throws<NotSupportedException>(() => FitsReader.ReadGray16(path)));
    }

    [Fact]
    public void ReadGray16_MissingBitpix_Throws()
    {
        var cards = new (string, string)[]
        {
            ("NAXIS", "2"), ("NAXIS1", "1"), ("NAXIS2", "1"),
        };
        WithFits(cards, Be16Unsigned(0), path =>
            Assert.Throws<InvalidDataException>(() => FitsReader.ReadGray16(path)));
    }

    [Fact]
    public void ReadGray16_UnsupportedBitpix_Throws()
    {
        var cards = new (string, string)[]
        {
            ("BITPIX", "32"), ("NAXIS", "2"), ("NAXIS1", "1"), ("NAXIS2", "1"),
        };
        WithFits(cards, new byte[8], path =>
            Assert.Throws<NotSupportedException>(() => FitsReader.ReadGray16(path)));
    }
}
